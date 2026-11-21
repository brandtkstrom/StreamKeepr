# Stream Keepr Recorder Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 4; `docs/research/recorder-process.md`; `docs/domain-model.md`.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

One `RecorderJob` run records exactly one part of one session. It runs Streamlink and FFmpeg, publishes `PartProducingMedia` and one `RecorderExited`, and stops. It never writes `index.m3u8`, stitches parts, finalizes a session, or updates `session.json`. Those belong to the session store.

## 2. Process model

- Two independent CliWrap commands per part: Streamlink `--stdout` and FFmpeg `pipe:0`.
- A bounded `System.IO.Pipelines.Pipe` bridges them: pause the writer at 4 MiB, resume at 2 MiB.
- Separate graceful and forceful cancellation tokens per process. Never signal the process group.
- Capture the direct PID from the CliWrap command task and its start time from `Process.GetProcessById(pid).StartTime` immediately after launch.
- Drain stdout and stderr concurrently. The CliWrap command task completes only after the direct process exits and both streams reach EOF. Task completion is necessary but not sufficient for process-tree exit: a descendant can keep a stream open after the direct PID is gone.
- After a force kill, verify the direct process by PID identity: the captured PID must be gone, or it must now resolve to a different start time (the PID was reused). The task must also have completed. An unreadable identity fails closed; never assume the process is gone.

## 3. Part files

| File | Pattern |
|---|---|
| Part playlist | `p{part:D3}.m3u8` |
| Init segment | `init_{part:D3}.mp4` |
| Media segment | `p{part:D3}_%05d.m4s` |

- Titles never appear in paths.
- `PartFiles.Delete` removes every `p{part:D3}*` and `init_{part:D3}*`, including `.tmp` files.
- Before starting, if any file matching `p{part:D3}*` or `init_{part:D3}*` exists, return `RecorderExited` without overwriting. The reducer should have assigned the next part; this is a backstop.
- FFmpeg's `+temp_file` writes segments and the EVENT playlist through `.tmp` + atomic rename. A hard kill may leave `.tmp` and orphan complete segments; recovery cleans them.

## 4. Arguments

### FFmpeg

```text
-hide_banner -loglevel warning -nostats
-progress pipe:1 -stats_period 5
-fflags +genpts+discardcorrupt
-i pipe:0
-map 0:v -map 0:a?
-c copy
-bsf:a aac_adtstoasc
-f hls
-hls_segment_type fmp4
-hls_time {SegmentSeconds}
-hls_list_size 0
-hls_playlist_type event
-hls_flags +temp_file+independent_segments+program_date_time
-hls_fmp4_init_filename init_{part:D3}.mp4
-hls_segment_filename {dir}/p{part:D3}_%05d.m4s
{dir}/p{part:D3}.m3u8
```

`-bsf:a aac_adtstoasc` is always present. It is harmless for fMP4 and required when Streamlink emits MPEG-TS/AAC.

### Streamlink

```text
--stdout --loglevel info
--hls-live-edge {HlsLiveEdge}
--stream-segment-threads 2
--stream-segment-timeout 20
--stream-timeout 60
--retry-open 3
--twitch-supported-codecs h264,h265,av1
[--twitch-api-header "Authorization=OAuth {TwitchUserOAuthToken}"]
https://www.twitch.tv/{channel} {Quality}
```

`--twitch-supported-codecs h264,h265,av1` is always present. It allows Twitch to select HEVC or AV1; the player and export must handle codecs the browser cannot decode. See “Accepted risks.”

## 5. Shutdown

Order, with every wait bounded:

1. If FFmpeg is already complete, complete the pipe reader first so Streamlink cannot block on a full pipe.
2. Stop Streamlink with the graceful token (SIGINT). Wait 5 s; force-kill if needed; wait 5 s. Never use stdin EOF to stop Streamlink.
3. Complete the pipe writer only after Streamlink is stopped or known dead. This delivers EOF to FFmpeg.
4. Wait `FinalizeTimeoutSeconds` for FFmpeg to finish on EOF. Then send exactly one SIGINT (graceful token), wait 5 s, force-kill, wait 5 s.
5. Complete the pipe reader. If no media was produced, delete the part files.

`FinalizeTimeoutSeconds` is capped at 15 s and defaults to 10 s. Worst case is `20 s + FinalizeTimeoutSeconds`: 30 s by default, 35 s at the cap, inside the 35 s job / 40 s host / 50 s Compose budgets. Do not send repeated SIGINTs; more than three can make FFmpeg hard-exit without a trailer. Never write `q\n` into FFmpeg's stdin.

### Unverified shutdown

A stop is verified only when the bounded wait completes, both streams reach EOF (the CliWrap task completes), and, after a force kill, the captured PID is gone or reused. If the force wait expires with the task still pending, or the captured PID still resolves to the captured start time, or the identity cannot be read (including a probe that throws), the stop is an unverified shutdown. Nothing is thrown: the job returns a `PartOutcome` with `Kind = Killed` and `Fault` set to an `UnverifiedShutdown`.

`UnverifiedShutdown` carries the tool, PID, captured start time, stop result, task-pending flag, and observed process state. The job force-cancels both tools, never finalizes FFmpeg after an unverified Streamlink stop, never deletes part files on this path, counts the part as `Killed` with one force kill in telemetry, and writes the finish log with the fault's diagnostic text. Diagnostic text includes the tool, PID, start time, task state, and observed state; it never includes the OAuth token or the raw Streamlink command line.

The coordinator responds to an outcome with a fault as follows:

1. Publish exactly one `RecorderExited(Killed, detail)` for the failed part, so status and the failure streak stay accurate.
2. Record a durable shutdown fault and retain the active job; do not treat the recorder task as a verified stop.
3. Fault the channel loop with `ChannelFatalException` immediately, even when the reducer enters `CoolingDown` and emits no retry command. `DvrSupervisor` stops the host.
4. `StartRecorder`, `StopRecorder`, and `ScheduleRetry` refuse to start another writer after the fault.

A verified force-kill remains `Killed` and retryable. A clean stream end remains `Exited` and healthy. Only an unverified shutdown is fatal. SIGKILL cannot be blocked, so a `D`-state process may stay unkillable until its I/O completes; the only safe action is to stop writing and alert.

### Restart and orphan policy

The application never signals the process group. A host restart can still kill the app while a tool process survives and keeps writing the old part. The supported guarantee is the service manager's, not a PID marker:

- Docker Compose runs the service with `init: true` and a `stop_grace_period` above the host shutdown timeout, and removes the whole container process tree on stop or restart.
- A systemd unit must set `KillMode=control-group` so stopping or restarting the unit kills every process in the cgroup.
- Bare `dotnet run` is not a supported production supervisor; it provides no process-tree guarantee.

There is no PID marker, startup orphan check, or new configuration knob. Recovery may repair orphaned part files; it must not start a writer for a part whose previous process may still be alive.

### Exit classification

| Kind | Rule |
|---|---|
| `Stopped` | The coordinator's stop token was requested and no watchdog/force path won. The domain ignores a late stop event; the `StopRequested` path already paused/finalized. |
| `Killed` | The stall watchdog fired, or a force-kill token was used because a graceful wait timed out. |
| `Exited` | Streamlink or FFmpeg ended on its own: stream end, network failure, or tool crash. |

Precedence when more than one applies: `Killed` > `Stopped` > `Exited`. A requested stop that had to be forced, or whose FFmpeg finalize timed out, is `Killed`, so the exit kind always agrees with the force-kill count.

## 6. Progress and media detection

- Parse `-progress` key=value lines and emit a sample when `progress=continue` or `progress=end` arrives.
- `total_size` is `N/A` for the HLS muxer. Live byte progress comes from a per-part `PartByteTracker`; the progress path never enumerates the part directory.
- The tracker re-stats the part playlist only when its length or last-write time changed. It reads `#EXT-X-MAP` for the init URI and non-comment lines for segment names, accepts only exact names for the current part, and stats each newly seen init or completed segment once. `.tmp` files, playlists, other parts, duplicates, and partial writes are ignored.
- The tracker opens the playlist with sharing that permits FFmpeg's atomic rename. A missing, empty, malformed, or momentarily unreadable playlist returns the last known total and retries on the next sample; metadata errors never fault FFmpeg's stdout pipe or fail the recorder.
- Live `PartBytes` is monotonic within a part (the high-water total); deleting a segment does not lower it. A new part starts at zero, and `DvrStatus` resets the bytes when the active job changes.
- At part end one authoritative `PartFiles.TotalBytes` scan supplies `telemetry.PartExited`, the finish log, and their equality. If that scan throws, the size is unknown: the part is counted without a size sample, the finish log reports null bytes, and a warning logs the tracker total as a hint. The tracker total is never substituted, because it is stale after an empty part is deleted and zero before the tracker has seen a segment.
- `Advanced` means `out_time_us` increased since the last report. The final block arrives after the trailer but before process exit; the job still awaits the process.
- Publish `PartProducingMedia(job, At)` exactly once, when progress advances or at shutdown when at least one segment file exists. It must be published before `RecorderExited`.
- Guard the “producing” flag with `Interlocked` or an equivalent atomic. Cleanup must never delete a part that produced media.
- A very short part that emits only the final progress block still counts as media because the shutdown check sees its segment files.

## 7. Stall watchdog and ad breaks

- A 5 s periodic watchdog checks `max(last media advance, ad grace until)`.
- `StallTimeoutSeconds` defaults to 120 s and covers pre-rolls and unlogged gaps.
- Streamlink's `Detected advertisement break of N seconds` line pushes the grace deadline to `now + N + 30 s`.
- A watchdog-triggered end is `Killed`; the job still runs the bounded shutdown and returns `RecorderExited`.
- The live stitched target duration is the session store's concern. The recorder writes part playlists only.

## 8. Boundary and cleanup

- The recorder writes only part files. It never writes the session manifest, the stitched index, progress, or library state.
- `IRecorder.RunAsync` returns one `PartOutcome` (kind, detail, part size or null, duration, a failed flag, and an optional unverified-shutdown fault) on every path and never publishes `RecorderExited`. The coordinator publishes exactly one `RecorderExited` per part from the outcome, so `PartProducingMedia` always precedes it.
- An unexpected exception after the tools start is reported, not thrown: the job force-kills each tool still running, verifies each stop exactly as the bounded shutdown does, finalizes and reports the part, and returns an outcome with `Failed = true` and the error in the detail. If a forced stop cannot be verified, the outcome carries the fault and is fatal.
- An exception before any tool starts escapes the job; the coordinator turns it into a failed `RecorderExited(Exited, detail)` with no recorder metric, because no tool ran. A failed outcome sets the channel's last error.
- If no media was produced, delete every part file including `.tmp` and init temp files before returning.
- If the part already exists, return `RecorderExited` immediately; do not overwrite.
- `RecorderExited.Detail` carries the reason, exit codes, process identity, stop result, and the last stderr lines. It is diagnostic; the reducer uses `RecorderExitKind`, never the detail string.

## 9. Failure modes

| Scenario | Recorder behaviour | Exit kind |
|---|---|---|
| Streamlink reaches stream end | FFmpeg gets EOF, finalizes the part; job returns | `Exited` |
| Streamlink network failure | Same as stream end; the observation/state machine decides whether to retry | `Exited` |
| FFmpeg dies first | Complete the pipe reader, stop Streamlink, return; the part may lack a trailer, recovery repairs | `Exited` |
| User stop | Stop Streamlink, EOF FFmpeg, finalize, return | `Stopped` |
| Stall watchdog | Stop both processes with the bounded shutdown; return | `Killed` |
| Force kill verified (PID gone or reused, task complete) | Return; the part may lack a trailer and is retryable | `Killed` |
| Force kill timed out (task still pending) | Return an outcome with an `UnverifiedShutdown` fault; the coordinator publishes one `Killed`, faults the channel loop, and the host stops; no second writer starts | `Killed` (fatal) |
| Captured PID still alive after the force kill | Same fail-closed path | `Killed` (fatal) |
| PID identity unreadable after the force kill | Same fail-closed path; never assume the process is gone | `Killed` (fatal) |
| No media | Delete part files, return | `Exited` |
| Part files already exist | Return without starting | `Exited` |
| Tool missing or fails to start | Force-kill any child, return with the error detail | `Exited` |
| App crash / container teardown | No trailer; recovery deletes `.tmp`/orphans and finalizes. The service manager must kill the whole process group/cgroup (see §5) | n/a |

## 10. Test matrix

Unit:

- `PartFiles` paths, the delete globs (including `.tmp` and init temp files), and strict segment/init name parsing.
- `PartByteTracker`: missing, empty, and malformed playlists; init-only; out-of-order and duplicate segments; `.tmp` and foreign names; late segments; deletion; transient read/stat failures; part isolation; an unlisted segment that only the full `PartFiles.TotalBytes` scan counts; and a probe proving one stat per newly seen segment.
- FFmpeg and Streamlink argument builders, including `-bsf:a aac_adtstoasc` and `--twitch-supported-codecs h264,h265,av1`.
- `FfmpegProgressParser`: `total_size=N/A`, `out_time_us`, `progress=continue`, `progress=end`, missing keys, first-block-only parts.
- `StderrTail` capacity and ordering.
- Ad-break regex and grace calculation.
- PID identity: missing process is gone; the same start time is alive; a different start time is a reused PID; an unreadable identity is unknown and fails closed.
- Coordinator shutdown fault: an outcome with an `UnverifiedShutdown` publishes one `Killed` exit, faults the loop, retains the active job, blocks a second `StartRecorder`, and is observed even when the reducer enters `CoolingDown` with no retry command.

Recorder job through the process seam. `IRecorderProcessRunner.Start` takes a `ToolLaunch` (tool name, path, arguments, working directory, and optional stdin/stdout byte streams) plus stdout and stderr line callbacks; no CliWrap type crosses it. The fake runner scripts output lines, file writes, and exits, honours the stop tokens like the real tools (Streamlink exits on its graceful signal, FFmpeg on stdin EOF), and runs callbacks in the starting context as CliWrap does. With an injected identity probe and part-size scan, no child process is needed:

- Part already exists: immediate `RecorderExited`, no tool started, nothing overwritten.
- No media: the part files FFmpeg opened are deleted and no media event is published.
- FFmpeg ends first: Streamlink gets its graceful stop; `Exited`.
- A failing Streamlink surfaces its stderr and exit code in the detail.
- The subscriber token reaches Streamlink's arguments but never the detail or a log.
- Start and finish logs carry the channel, session, and job scope; finish bytes equal the on-disk total.
- Hostile tool output is logged only at debug, redacted, and escaped.
- Stall watchdog: `Killed`, stall counted, media event before exit, graceful Streamlink stop and FFmpeg finish on EOF.
- Ad-break line: the watchdog does not fire during the grace.
- Progress bytes never decrease and the final sample equals the on-disk total; an unreadable playlist keeps the last total and never fails the part.
- A never-completing tool with the captured PID alive returns a `Killed` outcome with an `UnverifiedShutdown` fault; an identity probe that throws fails closed the same way.
- An unexpected error after the tools start force-stops and verifies both tools and returns a failed outcome; if a forced stop cannot be verified, the outcome is fatal.
- A verified force-kill (task completed, PID gone or reused) remains `Killed`.
- A force kill whose captured PID is still alive, or whose identity is unreadable, fails closed.
- A clean stream end remains `Exited` and never inspects process identity.
- An unverified shutdown still counts the part as `Killed`, counts one force kill, and writes the finish log before raising.
- A part whose size scan throws is counted with no size sample, logs null bytes, and warns.

Process runner adapter (`CliWrapProcessRunner`, real child processes): output lines reach their callbacks and the exit code and PID are reported; stdin and stdout streams carry bytes; a force kill ends a process that ignores its graceful signal; a missing executable completes with an error and no identity; standard output takes exactly one of a stream or a line callback.

Integration with real FFmpeg and the fake Streamlink fixtures:

- Normal stream end: part playlist, init, segments, media event before exit event, playable output.
- User stop: graceful order, `Stopped`, `ENDLIST`, playable output.
- Worst-case stop with tools that ignore their graceful signal fits the 35 s job budget.
- The bounded pipe backpressures a source that is not being drained.
- MPEG-TS/AAC input succeeds with `-bsf:a aac_adtstoasc`.
- HEVC/AV1 argument path records successfully (playback support is a separate test).

## 11. Accepted risks

- **HEVC/AV1 playback:** `h265,av1` may produce codecs not supported by every browser. The player must surface an unsupported-codec error; the export may also be HEVC/AV1. Owner: **Player, stats, and resume** and **Details, export, delete, auth UX, and settings**.
- **HEVC tagging:** FFmpeg may tag HEVC as `hev1`; MP4 export should use `-tag:v hvc1` where required. Owner: **Details, export, delete, auth UX, and settings**.
- **Twitch container mix:** Streamlink may emit fMP4 or MPEG-TS/AAC; the bitstream filter covers the latter.
- **Live target duration drift:** a part spanning an ad break can change the target duration; the session store must use a stable live value. Owner: **Session store, stitching, recovery, and retention**.

## 12. Binding versus implementation freedom

Binding: the two-process model, part file names, argument set, shutdown order and budgets, process-identity verification and the fatal unverified-shutdown rule, the service-manager restart/orphan requirement, exit-kind rules, progress/media detection, stall/ad-break behaviour, recorder boundary, cleanup, and event order.

Freedom: CliWrap versus raw `Process`, pipe sizes beyond the 4 MiB recommendation, parser internals, logging, regex, and test harness details.

## 13. Related

- `docs/domain-model.md` for `RecorderExited`, `PartProducingMedia`, and the state transitions.
- `docs/reviews/implementation-plan-amendments.md` (A-01, A-07, A-11).
- `docs/research/recorder-process.md` for the Linux process evidence.
