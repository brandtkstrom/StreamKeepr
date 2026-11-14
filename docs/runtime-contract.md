# Stream Keepr Runtime Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 6; `docs/domain-model.md`; `docs/recorder-contract.md`; `docs/session-store-contract.md`; plan review B-02/B-05/C-04.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Ownership and lifetimes

| Component | Lifetime | Owns |
|---|---|---|
| `DvrSupervisor` | Singleton `BackgroundService` + `IChannelControl` | Startup recovery, channel loops, poller/EventSub supervision, channel add/remove/stop/resume |
| `ChannelCoordinator` | One per watched channel | Its event queue, reducer state, active job, command execution |
| `ChannelStore` | Singleton | `channels.json`, the watched-channel snapshot, pause flag, profile fields |
| `SessionStore` | Singleton | `session.json`, `index.m3u8`, thumbnails, session locks |
| `RecordingLibrary` | Singleton | In-memory recording summaries and `Changed` |
| `ThumbnailService` | Singleton `BackgroundService` | Thumbnail frame grabs (FFmpeg) and `.streamkeepr/thumbnails/` scratch files |
| `DvrStatus` | Singleton | Read-only runtime snapshots for UI and health |
| `RecorderJob` | Transient worker per part | Streamlink/FFmpeg processes and part files |
| `TwitchStreamPoller` / `EventSubClient` | Singletons, supervised | Observations and push notifications |
| `ChatCaptureService` | Singleton `BackgroundService` | Session-to-chat reconciliation and chat-event routing |
| `IrcChatClient` / `MockChatSource` | Singleton `BackgroundService` + `IChatSource` | The read-only IRC connection or its deterministic Mock substitute |
| `ChatLogStore` | Singleton | The per-session chat writer registry (`ChatSessionWriter`) and chat log reads; file naming is `ChatLogLayout`, the finalize purge `ChatLogPurge` |

Chat capture is isolated from the DVR path: a source or store failure is logged and retried, never propagated into a channel loop, the host, or `/health/live`. If the source's event stream fails, `ChatCaptureService` stops both its loops, logs an error, closes open outages and session writers, and restarts capture after 5 s. Shutdown closes open outages the same way.

- Sources hand events to the capture service through a bounded `ChatEventQueue` (10,000 events). When it is full, each channel's new events wait in an ordered backlog: messages and chat log events collapse into one `dropped` range, which replay shows as a gap, and `Disconnected`/`Joined` are always kept. Backlogs flush as the reader frees room.
- A source reports `Disconnected` when its connection ends and `Joined` when it (re)joins a channel. The capture service writes a `gap` from the last captured message (or the disconnect, when none arrived) to the rejoin, or to the session end when it never rejoins.
- A failed join is logged and retried on the next 1 s reconciliation tick; reconciliation ticks on the injected `TimeProvider`.
- Dependency direction (ADR 0010): `Infrastructure/Twitch/Chat` and the Mock chat source may use `Infrastructure/Storage`; Storage never imports Twitch/Chat. `ChatSourceEvent` and `ChatMessage` are the chat log's input contract and live in Storage, as does the `ChatSources` name each source reports.

| `Chat:` key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Turns capture on. While off, live recordings show no chat pane; recordings with captured chat still replay it. |
| `TwitchUserOAuthToken`, `TwitchLogin` | unset | Optional dedicated `chat:read` identity (`docs/twitch-auth-contract.md`); a rejected token falls back to anonymous. |
| `LeadInSeconds` | `12` | How much earlier the content at a part start aired than its first PDT. |
| `BucketMinutes` | `10` | Bucket length; buckets are floored on Unix time, so any value 1–60 works. |
| `MaxChannels` | `100` | Joined-channel cap; joins beyond it are refused and logged. |
| `JoinIntervalMs` | `600` | Spacing between `JOIN`s (Twitch allows 20 per 10 s). |
| `ConnectTimeoutSeconds`, `RegistrationTimeoutSeconds` | `10`, `15` | WebSocket connect and IRC registration limits. |
| `ReconnectMaxSeconds` | `300` | Cap on the doubling reconnect backoff (from 5 s, jittered). |
| `MaxBytesPerSession` | 512 MiB | Per-session cap; a full log is marked `truncated` and capture stops for that session. |
| `PurgeDeletedText` | `Off` | `OnFinalize` removes deleted text at finalize (ADR 0009). |

Only the coordinator loop mutates domain state. Readers use `DvrStatus` and `RecordingLibrary`; they never inspect a live coordinator.

## 2. Event queue and routing

- Each coordinator has one unbounded `Channel<DvrEvent>` with `SingleReader = true`.
- `PublishAsync` uses `TryWrite`, never blocks, and never throws. Events are not dropped while the loop is running; after the loop completes, late events are dropped.
- Unbounded is intentional: the loop can await a job stop for up to 35 s, and a bounded channel would deadlock if the job’s stdout callback also publishes.
- Log a warning when the queue depth exceeds 1,000; do not drop.
- Producers: the poller and EventSub route `StreamStatusObserved` per channel; the recorder publishes `PartProducingMedia` and `RecorderExited`; the supervisor publishes `StopRequested`, `ResumeRequested`, and `SessionRecovered`.
- Per-producer order is preserved. Cross-producer ordering is not significant: the reducer filters stale job events by `JobId`, and observations carry timestamps.
- A removed channel’s router drops events; the coordinator is completed and its loop ends.

## 3. Command execution

The coordinator executes a step’s commands sequentially, in the returned order.

- `OpenSession` / `ReopenSession`: on failure, log and `DvrStatus.Failed`. Mark the current command list so the following `StartRecorder` for that session is skipped and a synthetic `RecorderExited(job, Exited, "open failed")` is published. The reducer retries cleanly; no parts are written without a manifest.
- `RecordMetadata`: on failure, log and `DvrStatus.Failed`; continue. The part keeps recording; metadata may be missing.
- `FinalizeSession`: on failure, log and `DvrStatus.Failed`. The on-disk manifest may still say `Recording`; recovery re-finalizes on the next start. Do not silently mark the session complete.
- `StopRecorder`: await the active job stop. A failure to stop within the budget is fatal (see §4).
- `StartRecorder`: start the job and return without awaiting its completion.
- `ScheduleRetry`: schedule a cancellable timer with ±25 % jitter; the timer publishes `RetryTimerElapsed(job)`. A failure to schedule is fatal: the channel would otherwise sit in `WaitingToRetry` forever.

A command failure other than the fatal cases is logged and surfaced through `DvrStatus`; it never tears down the channel.

## 4. Job supervision

- One active job per channel. `StartRecorder` and `ScheduleRetry` first stop the previous job.
- `JobStopBudget` is 35 s, matching the recorder contract’s worst case (20 s + 15 s finalize cap).
- If the job does not stop within the budget, the coordinator throws a fatal supervisor fault. The channel loop faults; the supervisor’s fault handler logs critical and stops the host. Recovery runs on restart. Never start a second job while the first may still write.
- `RecordPartAsync` checks `DiskGuard.HasRoomToRecord()` before starting. A full disk produces a `RecorderExited` without launching the recorder.
- `RecordPartAsync` catches every recorder exception and always publishes exactly one `RecorderExited` with the detail. `RecorderJob` itself returns one; the coordinator synthesizes only when the job throws or is skipped.
- A channel loop that faults for any reason stops the host. A channel loop is not restarted in-process: its state may be inconsistent.
- The poller and EventSub are supervised with exponential backoff (5 s, doubling to 5 min). Polling is the fallback whenever EventSub is unavailable.
- Stale job events are filtered by the reducer; the coordinator does not need a separate job-generation check beyond the `JobId`.

## 5. Channel control

`IChannelControl` is the only write surface for watched channels. The UI wraps it in `ChannelsView` with sign-in and authorization; `IChannelControl` itself trusts an authenticated caller.

- `AddChannelAsync(login)`: normalize and validate the Twitch login, resolve the Helix profile (`UserId`, display name, avatar), persist through `ChannelStore`, start the channel loop if absent, then nudge the poller. The UI add fails with a clear error if Helix cannot resolve the login.
- `RemoveChannelAsync(login)`: under the control gate, publish `StopRequested`, complete the coordinator, await its loop, remove the channel from `ChannelStore`, and remove its `DvrStatus` entry. Recordings stay in the library. EventSub restarts because `SubscriptionsChanged` fires.
- `StopRecordingAsync(login)`: persist `Paused = true`, then route `StopRequested`. The reducer finalizes an open session and enters `Paused`.
- `ResumeRecordingAsync(login)`: persist `Paused = false`, route `ResumeRequested`, then nudge the poller. The next observation decides whether to record.
- Add/remove/stop/resume are serialized by one control gate. Recording continues while channel control is busy.
- At startup, channels seeded from `Twitch:Channels` are added without Helix validation; failures to resolve their profiles are logged and the channel stays polling-only until a later resolve. A corrupt `channels.json` fails startup with a clear error; never silently reset the list.
- EventSub’s `SubscriptionsChanged` fires only when a login or `UserId` is added or removed, not when a name, avatar, or pause flag changes.

## 6. `DvrStatus`

```csharp
public sealed record DvrSnapshot(
    DvrState State,
    DateTimeOffset LastEventAt,
    StreamObservation? LastObservation,
    JobId? ActiveJob,
    long PartBytes,
    TimeSpan PartMediaTime,
    DateTimeOffset? LastProgressAt,
    string? LastError,
    string? LastDetail);
```

- `Current` returns a copied snapshot of the per-channel dictionary; callers cannot mutate runtime state.
- `Changed` fires on every update and on remove. The UI’s `LibraryView` owns the 500 ms throttle.
- `ReportProgress` updates only when the `JobId` matches `ActiveJob`.
- `Applied` resets `PartBytes`, `PartMediaTime`, and `LastProgressAt` when the active job changes.
- `LastDetail` stores the diagnostic reason from every `RecorderExited`. `LastError` is set by `Failed` (command/recorder exception, disk full, open failure) and by a `Killed` exit; a normal `Exited` does not overwrite it.
- `Remove` drops the channel snapshot and raises `Changed`.

## 7. Startup

1. `ChannelStore.LoadAsync` reads `channels.json`, or seeds it from `Twitch:Channels` on first start.
2. Resolve missing profiles for seeded channels; log failures and continue polling-only.
3. `RecoveryService.RecoverAsync` scans the recordings root and returns `Unfinished`/`Finished` per channel.
4. `RecordingLibrary.MarkLoaded()`; the UI leaves skeleton state.
5. Start one coordinator per channel:
   - Paused: `Paused(Finished)`.
   - Unpaused: `Idle(Finished)`; if an unfinished session exists, publish `SessionRecovered(Unfinished)`.
6. Start the supervised poller and EventSub loops.

A startup recovery failure stops the host with a clear error; do not start a partial set of channels.

## 8. Shutdown

- The stopping token cancels every channel loop and the shared loops.
- Each coordinator’s `finally` stops its active job gracefully; the recorder’s bounded shutdown finalizes the part.
- The supervisor awaits all channel loops, then the poller/EventSub loops.
- Channels stop in parallel; the 35 s job budget and 40 s host timeout are the bounds. Compose’s 50 s grace is the backstop.
- The host timeout is not a substitute for finalization: if it fires, the container kills the children and recovery repairs on the next start.

## 9. Failure isolation

| Failure | Behaviour |
|---|---|
| One recorder fails | `RecorderExited`; reducer retries/cools down; other channels unaffected. |
| Recorder will not stop | Fatal supervisor fault; host stops; recovery on restart. |
| Open/Reopen fails | Skip the recorder, synthesize `RecorderExited`; reducer retries. |
| Finalize fails | Log, status error; recovery re-finalizes on restart. |
| Retry timer cannot schedule | Fatal; host stops. |
| Channel loop faults | Log critical; host stops. |
| Poller/EventSub crashes | Supervised restart with backoff; polling fallback. |
| Disk full | No recorder launched; reducer retries/cools down. |
| Corrupt `channels.json` | Startup fails with a clear error; no silent reset. |
| Corrupt session manifest | Session store skips it; channel control unaffected. |

## 10. Concurrency

- One job per channel; channels record in parallel.
- No global concurrent-recording cap. The channel list is user-controlled; add a cap only if disk or bandwidth pressure appears.
- The control gate serializes channel list mutations; it never blocks a channel loop’s event processing.
- `DvrStatus` uses a concurrent dictionary internally and exposes copied snapshots.
- `ChannelStore` serializes `channels.json` updates and writes atomically.

## 11. Test matrix

Coordinator:

- Event order and reducer command execution; stale `JobId` events ignored.
- `OpenSession`/`ReopenSession` failure skips the recorder and synthesizes `RecorderExited`.
- `FinalizeSession` failure leaves the session recoverable.
- Start/stop job; stop budget overrun is fatal.
- Retry timer start, cancel, jitter, and stale timer.
- Disk-full path skips the recorder.
- `DvrStatus` progress only for the active job; job change resets bytes/media.
- `LastDetail` vs `LastError` on normal and failed exits.

Supervisor and channel control:

- Startup recovery, paused vs unpaused initial state, `SessionRecovered`.
- Add with Helix validation; unknown login fails; seeded channel with no profile stays polling-only.
- Remove while Recording finalizes; recordings stay; `SubscriptionsChanged` fires.
- Stop persists paused and finalizes; resume clears paused and nudges.
- Control gate serializes add/remove/stop/resume.
- Poller/EventSub crash restarts with backoff.
- Channel loop fault stops the host.
- Shutdown stops channels in parallel and awaits finalization.

`ChannelStore`:

- First-start seed, normalization, validation, distinct logins.
- Atomic write; corrupt file fails startup.
- Add/remove/pause/profile; `Changed` vs `SubscriptionsChanged`.
- Concurrent updates serialized.

`DvrStatus`:

- Copied `Current`; concurrent readers.
- `Changed` on apply/progress/fail/remove.
- Stale progress ignored; job change resets counters.

Integration:

- Two channels, one recording and one paused; remove the recording one.
- Shutdown during recording; restart; recovery resumes or finalizes.
- Host restart after a channel-loop fault; recovery completes.
- Mock profile end-to-end with the fake poller and Streamlink.

## 12. Binding versus implementation freedom

Binding: ownership and lifetimes, unbounded single-reader event queue with non-blocking publish, command order and failure policies, job-stop budget and fatal overrun, channel control methods and persistence, `DvrStatus` shape and `LastError`/`LastDetail` split, startup/shutdown order, failure isolation, and no global recording cap.

Freedom: dictionary/lock implementation, task scheduling, exception types beyond fatal-vs-recoverable, logging, queue-depth warning threshold implementation, and test harness.

## 13. Related

- `docs/domain-model.md` for reducer states, events, commands, and recovery classification.
- `docs/recorder-contract.md` for job stop budget and exit kinds.
- `docs/session-store-contract.md` for finalize and recovery.
- `docs/reviews/implementation-plan-amendments.md` (B-02, B-05, C-04).
