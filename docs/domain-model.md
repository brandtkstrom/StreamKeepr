# Stream Keepr Domain Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 1, amended by the plan review.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope and purity

- One reducer instance per watched channel. Each channel has its own `DvrState`.
- `Transition(state, event, settings)` returns `Step` and is pure: no clocks, randomness, I/O, threads, or mutable static state.
- All time arrives on events or in settings. IDs are deterministic.
- Byte counts, media time, and heartbeats are telemetry for `DvrStatus`, not reducer state.
- Commands are intents. The coordinator executes them sequentially; the reducer never performs I/O.

## 2. Models

```csharp
using System.Collections.Immutable;

namespace StreamKeepr.Domain;

public sealed record LiveStream(
    string StreamId, string Title, string Game, DateTimeOffset StartedAt, string? ThumbnailUrl);

public sealed record Session(
    string Id, string Channel, string StreamId, DateTimeOffset StartedAt, string Directory)
{
    // Channel is a validated Twitch login; StreamId is digits. Id is a safe path segment.
    public static Session For(LiveStream live, string channel, string recordingsRoot)
    {
        string id = $"{live.StartedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{live.StreamId}";
        return new(id, channel, live.StreamId, live.StartedAt, Path.Combine(recordingsRoot, channel, id));
    }
}

public readonly record struct JobId(string SessionId, int Part)
{
    public override string ToString() => $"{SessionId}/p{Part:D3}";
}

// Complete means the manifest is already Complete and must be reopened before the next part.
public sealed record Resumable(Session Session, int NextPart, bool Complete = false);

public sealed record DvrSettings(
    string RecordingsRoot,
    string Channel,
    int MaxRetries,
    TimeSpan RetryBase,
    TimeSpan RetryMax,
    int FailuresBeforeCooldown,
    TimeSpan Cooldown,
    TimeSpan HealthyPart);
```

`Session.Id` is `yyyyMMdd'T'HHmmss'Z'_{streamId}` in UTC. Titles are never part of a path or session ID. `JobId` is deterministic: one session plus one part number.

## 3. State and invariants

```csharp
public abstract record DvrState
{
    private DvrState() { }

    public sealed record Idle(
        Resumable? Resume = null, int Streak = 0, Resumable? Finished = null) : DvrState;

    public sealed record Recording(
        Session Session, JobId Job, LiveStream Latest, int Attempt, bool HasMedia,
        int Streak, DateTimeOffset? FirstMediaAt = null) : DvrState;

    public sealed record WaitingToRetry(
        Session Session, JobId Next, LiveStream Latest, int Attempt, int Streak,
        int OfflineCount = 0) : DvrState;

    public sealed record CoolingDown(
        DateTimeOffset Until, Resumable? Resume, string LastError) : DvrState;

    public sealed record Paused(Resumable? Finished = null) : DvrState;
}
```

Invariants:

- `Idle.Resume` and `Idle.Finished` are mutually exclusive. The reducer never produces both; if both are constructed externally, `Finished` takes precedence.
- `Recording.HasMedia == (Recording.FirstMediaAt is not null)`.
- `Recording.Attempt >= 0`; `PartProducingMedia` sets it to 0. `Attempt` counts retries within the current part, not across parts.
- `WaitingToRetry.Next.Part` is the part to start; `OfflineCount >= 0`.
- `Paused` means no recorder is running and live observations are ignored until `ResumeRequested`.
- `CoolingDown.Until` is an absolute time supplied by an event; the reducer never calls a clock.

## 4. Observations, events, and commands

```csharp
public abstract record StreamObservation
{
    private StreamObservation() { }
    public sealed record Live(LiveStream Stream) : StreamObservation;
    public sealed record Offline : StreamObservation;
    public sealed record Unknown(string Error) : StreamObservation;   // API failure: never Offline
}

public enum RecorderExitKind
{
    Exited,   // The process ended on its own: stream end, network failure, or tool crash.
    Stopped,  // The coordinator requested a graceful stop.
    Killed,   // The watchdog or a force-kill ended the process.
}

public abstract record DvrEvent
{
    private DvrEvent() { }
    public sealed record StreamStatusObserved(StreamObservation Observation, DateTimeOffset At) : DvrEvent;
    public sealed record SessionRecovered(Resumable Resume) : DvrEvent;
    public sealed record PartProducingMedia(JobId Job, DateTimeOffset At) : DvrEvent;   // once per part
    public sealed record RecorderExited(JobId Job, DateTimeOffset At, RecorderExitKind Kind, string Detail) : DvrEvent;
    public sealed record RetryTimerElapsed(JobId Job) : DvrEvent;
    public sealed record StopRequested : DvrEvent;
    public sealed record ResumeRequested : DvrEvent;
}

public abstract record DvrCommand
{
    private DvrCommand() { }
    public sealed record OpenSession(Session Session, LiveStream Stream) : DvrCommand;
    public sealed record ReopenSession(Session Session) : DvrCommand;
    public sealed record RecordMetadata(Session Session, LiveStream Stream) : DvrCommand;
    public sealed record StartRecorder(Session Session, JobId Job) : DvrCommand;
    public sealed record StopRecorder(JobId Job) : DvrCommand;
    public sealed record ScheduleRetry(JobId Job, TimeSpan BaseDelay) : DvrCommand;
    public sealed record FinalizeSession(Session Session) : DvrCommand;
}

public readonly record struct Step(DvrState Next, ImmutableArray<DvrCommand> Commands);
```

`Detail` is diagnostic text; the reducer never parses it for control. `RecorderExitKind.Stopped` while `Recording` is a supervisor contract violation: the reducer stays put and logs it; the supervisor must send `StopRequested` first.

## 5. Transition rules

### Idle

| Event | Condition | Next | Commands |
|---|---|---|---|
| `SessionRecovered(r)` | `r.Complete` | `Idle(Finished: r)` | — |
| `SessionRecovered(r)` | not complete | `Idle(Resume: r)` | — |
| `StreamStatusObserved(Live)` | `Finished` matches stream | `Recording` at `Finished.NextPart` | `ReopenSession`, `RecordMetadata`, `StartRecorder` |
| `StreamStatusObserved(Live)` | `Resume` matches stream | `Recording` at `Resume.NextPart` | `RecordMetadata`, `StartRecorder` |
| `StreamStatusObserved(Live)` | `Resume` is a different stream | new `Recording` at part 1 | `FinalizeSession(old)`, `OpenSession`, `StartRecorder` |
| `StreamStatusObserved(Live)` | no resumable | new `Recording` at part 1 | `OpenSession`, `StartRecorder` |
| `StreamStatusObserved(Offline)` | `Resume` exists | `Idle(Finished: Resume)` | `FinalizeSession` |
| `StreamStatusObserved(Unknown)` | any | stay | — |
| `StopRequested` | `Resume` exists | `Paused(Resume)` | `FinalizeSession` |
| `StopRequested` | no resume | `Paused()` | — |

### Recording

| Event | Condition | Next | Commands |
|---|---|---|---|
| `PartProducingMedia(job, at)` | `job == Job` and `!HasMedia` | `Recording(HasMedia: true, FirstMediaAt: at, Attempt: 0)` | — |
| `RecorderExited(job, at, Exited/Killed, detail)` | `job == Job` | result of “After exit” | see below |
| `RecorderExited(job, at, Stopped, detail)` | `job == Job` | stay | — |
| `StreamStatusObserved(Live)` | same stream, metadata changed | update `Latest` | `RecordMetadata` |
| `StreamStatusObserved(Live)` | different stream | new `Recording` at part 1 | `StopRecorder(old)`, `FinalizeSession(old)`, `OpenSession`, `StartRecorder` |
| `StreamStatusObserved(Offline)` | any | stay | — |
| `StreamStatusObserved(Unknown)` | any | stay | — |
| `StopRequested` | any | `Paused(Resumable(session, Job.Part + 1))` | `StopRecorder(Job)`, `FinalizeSession(session)` |

Offline is advisory while `Recording`; `RecorderExited` is the authoritative end of a part. The stall watchdog exists for a recorder that never exits.

### WaitingToRetry

| Event | Condition | Next | Commands |
|---|---|---|---|
| `RetryTimerElapsed(job)` | `job == Next` and `OfflineCount == 0` | `Recording(Attempt: 0, HasMedia: false, FirstMediaAt: null)` | `StartRecorder(Next)` |
| `RetryTimerElapsed(job)` | `job == Next` and `OfflineCount > 0` | stay | — |
| `StreamStatusObserved(Offline)` | `OfflineCount >= 1` | `Idle(Finished: Resumable(session, Next.Part))` | `FinalizeSession` |
| `StreamStatusObserved(Offline)` | `OfflineCount == 0` | `WaitingToRetry(OfflineCount: 1)` | — |
| `StreamStatusObserved(Live)` | same stream | update `Latest`, reset `OfflineCount` | `RecordMetadata` when changed |
| `StreamStatusObserved(Live)` | different stream | new `Recording` at part 1 | `FinalizeSession(old)`, `OpenSession`, `StartRecorder` |
| `StreamStatusObserved(Unknown)` | any | stay | — |
| `StopRequested` | any | `Paused(Resumable(session, Next.Part))` | `FinalizeSession` |

Skipping the retry when `OfflineCount > 0` prevents launching a recorder after an offline observation. The next `Live` starts a part; the next `Offline` finalizes.

### CoolingDown

| Event | Condition | Next | Commands |
|---|---|---|---|
| `StreamStatusObserved(obs, at)` | `at >= Until` | `FromIdle(Idle(Resume, Streak: 0), obs)` | according to `Idle` |
| `StreamStatusObserved(Offline)` | `Resume` exists and `at < Until` | `CoolingDown(Resume: null)` | `FinalizeSession` |
| `StreamStatusObserved(Live)` | `at < Until` | stay | — |
| `StreamStatusObserved(Unknown)` | any | stay | — |
| `StopRequested` | `Resume` exists | `Paused(Resume)` | `FinalizeSession` |
| `StopRequested` | no resume | `Paused()` | — |

### Paused

| Event | Next | Commands |
|---|---|---|
| `ResumeRequested` | `Idle(Finished: Paused.Finished)` | — |
| anything else | stay | — |

## 6. After exit

For `RecorderExited` with `Exited` or `Killed` for the current job:

```text
healthy = Kind == Exited
          and HasMedia
          and FirstMediaAt is not null
          and (At - FirstMediaAt) >= Settings.HealthyPart

attempt = HasMedia ? 1 : Attempt + 1
streak  = healthy ? 0 : Streak + 1
```

1. If `!healthy` and `streak >= FailuresBeforeCooldown`:
   `CoolingDown(At + Cooldown, Resumable(Session, Job.Part + 1), Detail)`.
2. Else if `attempt <= MaxRetries`:
   `WaitingToRetry(Session, JobId(Session.Id, Job.Part + 1), Latest, attempt, streak)` with
   `ScheduleRetry(Next, Backoff(attempt))`.
3. Else:
   `Idle(Resumable(Session, Job.Part + 1), streak)`.

`Backoff(attempt)` is `min(RetryMax, RetryBase * 2^(attempt - 1))`. The coordinator adds ±25 % jitter when it schedules the timer.

A part that reaches `HealthyPart` resets `Streak` even though it still retries when the stream may return. A short or failed part increments `Streak`; repeated short parts therefore reach `CoolingDown` even when every part produced some media.

## 7. Recovery and pause

- At startup, the recovery service scans the recordings root and returns the newest session for each channel.
- An unfinished session (`Recording`) is delivered as `SessionRecovered(Resumable(Session, NextPart, Complete: false))`; it becomes `Idle.Resume`.
- A finished session (`Complete`) is delivered as `SessionRecovered(Resumable(Session, NextPart, Complete: true))`; it becomes `Idle.Finished`. If the same stream is still live, the next `Live` reopens it as the next part instead of colliding with the existing folder.
- `NextPart` is one greater than the highest existing part.
- A paused channel starts in `Paused`. The paused flag is the only persisted pause preference; the newest complete session may seed `Paused.Finished` from the startup scan. On `ResumeRequested`, the channel returns to `Idle(Finished: Paused.Finished)`; if no session is available, `Idle()`.
- `Idle.Resume` is always unfinished. `Idle.Finished` is always complete or already finalized.

## 8. Metadata

`WhileActive` updates `Latest` and emits `RecordMetadata` when:

- the title or game differs, or
- `Latest.ThumbnailUrl` is null and the incoming thumbnail is non-null.

A non-null thumbnail changing to another non-null thumbnail does not emit. The session store saves a thumbnail once; title and game changes become chapters.

## 9. Command ordering

The coordinator executes commands in the order returned:

- `StopRecorder` completes before `FinalizeSession`.
- `OpenSession` or `ReopenSession` precedes `RecordMetadata` and `StartRecorder`.
- `FinalizeSession` precedes `OpenSession` when a new stream replaces the current session.
- `ScheduleRetry` carries the next `JobId`; `RetryTimerElapsed` is ignored unless it matches the current `WaitingToRetry.Next`.

A command failure is converted by the coordinator into `RecorderExited` or a supervisor error; it is never swallowed. The reducer itself never throws for a valid state/event pair.

## 10. Test matrix

Table-driven tests for every row above, plus:

1. Stale `PartProducingMedia`/`RecorderExited`/`RetryTimerElapsed` events change nothing.
2. `Idle` with `Finished` and `Resume` both set: `Finished` wins; the reducer never produces both.
3. `Recording.HasMedia` always matches `FirstMediaAt != null`.
4. `PartProducingMedia` twice: the second is ignored.
5. A short media part (`< HealthyPart`) increments `Streak`; a healthy part resets it.
6. Repeated short parts reach `CoolingDown` after `FailuresBeforeCooldown`.
7. `Stopped` while `Recording` is ignored; `StopRequested` produces the pause/finalize path.
8. `RetryTimerElapsed` with `OfflineCount > 0` is ignored.
9. First offline in `WaitingToRetry` counts; second finalizes.
10. `Unknown` never finalizes a session or starts a recorder.
11. `CoolingDown` ignores live before `Until` and starts after it.
12. `Paused` ignores everything except `ResumeRequested`.
13. Recovery of a complete session with the same stream live reopens it at `NextPart`.
14. Recovery of an unfinished session resumes at `NextPart`.
15. Metadata changes: title/game emit; null-to-known thumbnail emits; known-to-known does not.
16. Command order is exactly the table order for stop, restart, and stream replacement.
17. Property tests: `Transition` never throws for any state/event pair; the same inputs produce the same output; stale job events produce no commands; `Idle.Resume`/`Finished` are never both produced by the reducer.

## 11. Binding versus implementation freedom

Binding: the model fields and state cases, event and command names/payloads, transition table, invariants, purity, `Session.Id` format, `JobId` determinism, retry/streak algorithm, metadata predicate, recovery classification, and `RecorderExitKind` semantics.

Freedom: helper methods, pattern-matching style, immutable collection choice beyond `ImmutableArray` in `Step`, logging, exception types, and test framework details.

## 12. Related

- `GLOSSARY.md` for the canonical terms.
- `docs/reviews/implementation-plan-amendments.md` (A-01, A-03, A-09, B-05).
- `docs/adr/0001-one-web-project-strangler-migration.md` and ADR-0002–0005.
