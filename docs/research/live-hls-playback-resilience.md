# Live HLS playback resilience

Status: proposed; incident diagnosis and issue set (2026-11-19)
Tracking issue: [#240](https://github.com/brandtkstrom/StreamKeepr/issues/240)
Work issues: [#241](https://github.com/brandtkstrom/StreamKeepr/issues/241), [#242](https://github.com/brandtkstrom/StreamKeepr/issues/242), [#243](https://github.com/brandtkstrom/StreamKeepr/issues/243), [#244](https://github.com/brandtkstrom/StreamKeepr/issues/244), [#245](https://github.com/brandtkstrom/StreamKeepr/issues/245), [#246](https://github.com/brandtkstrom/StreamKeepr/issues/246), [#247](https://github.com/brandtkstrom/StreamKeepr/issues/247), [#248](https://github.com/brandtkstrom/StreamKeepr/issues/248)
Binding: no. Several items change the session-store, recorder, player, and hosting contracts and need owner approval.

## 1. Incident

An active `hasanabi` recording (session `20261119T190837Z_321241052636`) stuttered and showed the video loading spinner well before the live edge. The full log payload shows three independent symptoms that reinforce each other:

- **Live manifest failures.** 320 `GET /media/hasanabi/.../index.m3u8` requests returned 500 between `22:20:44Z` and `00:01:06Z`. Elapsed time was 10.0 s minimum, 13.7 s median, 41.4 s maximum. hls.js abandons a live playlist load at its 10 s time-to-first-byte timeout.
- **Storage errors.** 43 `IOException: Input/output error` failures writing `viewers.jsonl` under `/Volumes/data-1/...` between `23:20:44Z` and `00:02:41Z`. Startup loaded 2 recordings in 20.8 s and recovered 2 sessions in 23.0 s.
- **Recorder churn.** Six parts failed with `System.TimeoutException: ffmpeg did not stop after a force kill` between `23:05` and `23:51Z`. Old FFmpeg processes kept writing after the job was declared failed, and the coordinator started the next part.

The storage volume is the primary fault. The app's request path, stitching algorithm, lock scope, retry classification, and player waiting state turn a slow/failing volume into a playback outage. This note records the primary-source research behind the eight work issues.

## 2. Request path and locks

### 2.1 Current path

`LiveIndexEndpoint.HandleAsync` calls `SessionStore.TryLoadAsync` and `SessionStore.StitchLiveAsync` on every GET/HEAD. `StitchLiveAsync`:

1. Enumerates `p*.m3u8` and stats the last part to form a cache key (`SessionStore.cs:251`).
2. On a miss, takes the per-session `SemaphoreSlim` and holds it across `TryLoadAsync`, `ReadPartsAsync` (every part), and `PlaylistStitcher.Stitch` (`:255-286`).
3. Rebuilds the entire body whenever the last part changes, which is roughly every 6 s.

The live playlist is small and changes on a known cadence. It does not need a request-time filesystem projection.

### 2.2 Cache/service design

The recommended pattern is an immutable snapshot produced by a `BackgroundService` and served from memory:

- A singleton `LivePlaylistCache` keyed by session id holds an immutable `LivePlaylistSnapshot` (body, pre-encoded UTF-8, segment count, refresh time). Publish with `Volatile.Write`; read with `Volatile.Read`.
- `LivePlaylistService : BackgroundService` runs an immediate refresh and then a `PeriodicTimer` loop (default 1 s). Each session has a single-flight refresh gate; a busy gate skips the tick rather than queueing.
- On failure, keep the last-known-good body. A single EIO must not stop the host; `BackgroundServiceExceptionBehavior` defaults to `StopHost`, so the service must catch per-session failures.
- The endpoint serves the snapshot with exact `Content-Length`, no body for HEAD, `Cache-Control: no-cache`, and `RequestAborted` on the body write. `Results.Text` re-encodes and copies per request; a pre-encoded `IResult` avoids that.
- A cold cache returns 503 + `Retry-After`; the player already retries the manifest every 3 s.
- Complete sessions keep the existing finalized `index.m3u8` path with Range support.

`IMemoryCache` is not the right primitive: it is not an atomic snapshot, can evict at any time, and stores references. `FileSystemWatcher` is not the correctness trigger: its buffer can overflow, network shares have a small notification buffer, and it still cannot distinguish an append from a rewrite.

### 2.3 Lock separation

The manifest lock protects read-modify-write of `session.json`; a read-only playlist projection does not need it. The live refresh should use a separate per-session gate with a strict one-way order:

- Never acquire `LockFor` while holding a refresh gate.
- Never acquire a refresh gate while holding `LockFor`.
- Requests take neither.
- `ThumbnailService` takes the refresh gate, then the manifest lock sequentially.

`SemaphoreSlim.WaitAsync(ct)` cancels only the wait; it cannot abort the holder's I/O. On Unix, async file I/O is a blocking syscall on the thread pool; an in-flight read is not cancellable ([File I/O improvements in .NET 6](https://devblogs.microsoft.com/dotnet/file-io-improvements-in-dotnet-6/), [dotnet/runtime#51985](https://github.com/dotnet/runtime/issues/51985)). Holding a lock across that read turns every waiter into a queue.

Part playlists must be opened with `FileShare.ReadWrite | FileShare.Delete`. `File.ReadAllTextAsync` uses `FileShare.Read` ([dotnet/runtime `File.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/File.cs#L1106-L1109)), and a reader holding a part playlist can block FFmpeg's `MoveFileEx` rename on Windows. The existing `OpenSharedRead` already uses the shared flags; `ReadPartsAsync` does not.

## 3. Incremental stitching

FFmpeg does not append bytes to `pNNN.m3u8`. `hls_window()` writes a complete new playlist to `<name>.m3u8.tmp` and renames it over the final path. For the file protocol, playlists of a type other than `vod` are always temp-written, even without `+temp_file`; the flag applies to playlists as well as segments ([FFmpeg HLS docs](https://ffmpeg.org/ffmpeg-formats.html#hls-2), [`hlsenc.c` `hls_window`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/hlsenc.c#L1598-L1738)).

Consequences:

- A reader sees the old complete file or the new complete file, never a partial final file. A hard kill can leave the old final plus a partial `.tmp`.
- The final path is replaced, not extended, so `(length, mtime)` is only an invalidation hint.
- The authoritative append-only test is: new length is greater, `newContent.StartsWith(oldContent, Ordinal)`, the tail ends at a complete line with at least one complete segment URI, and the changed part is the last non-empty part. A last-URI check or a content hash is not sufficient.
- If the test fails, rebuild from cached parsed part bodies; never re-read unchanged parts.

EVENT invariants with `-hls_list_size 0 -hls_playlist_type event`:

- `#EXT-X-MEDIA-SEQUENCE` stays 0.
- `#EXT-X-MAP` is emitted for the first entry; existing segment lines are stable.
- `#EXT-X-TARGETDURATION` can increase when a longer segment appears. RFC 8216 §6.2.1 says it MUST NOT change; this is an existing StreamKeepr risk because both FFmpeg and `PlaylistStitcher` can raise it. The incremental path treats a target-duration change as non-append and rebuilds the header.
- `#EXT-X-ENDLIST` appears only at finalize or graceful exit.
- RFC 8216 §6.2.1: an EVENT playlist “MUST NOT change or delete any part of the Playlist file; it MAY append lines to it.”

A Windows probe with FFmpeg 9.0.2 and the same flags saw the final playlist's file ID change on every update, 377 reads with no empty/partial responses, and a frozen playlist while a read handle was held across updates (`failed to rename file ... Operation not permitted`). Read quickly, close, and never cache handles.

A per-session `LiveSnapshot` should carry an ordered list of `PartSnapshot` values (path, length, last-write time, content, parsed body lines, target duration, has-ENDLIST). The warm path stats parts and returns the cached body when nothing changed. The dirty path reads only changed parts, tests append-only, and either appends the delta or rebuilds from cached part bodies. Composition rules:

- No body change: update signatures.
- Last part append-only: append the delta lines.
- New last part with segments: append one `#EXT-X-DISCONTINUITY` before its `#EXT-X-MAP`/body.
- Empty new part: add nothing.
- Any other change: rebuild from cached parts.

## 4. HTTP cancellation and last-known-good

The 500s are not a good signal. In Development the app does not register `UseExceptionHandler` (`Program.cs:427-428`), so an aborted `OperationCanceledException` surfaces as a 500 and an Error-level request log. .NET 10's `ExceptionHandlerMiddleware` maps OCE/IOException + canceled `RequestAborted` to 499 and logs `RequestAbortedException`; Kestrel also uses 499 for aborted-request logging ([`ExceptionHandlerMiddlewareImpl.cs`](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Middleware/Diagnostics/src/ExceptionHandler/ExceptionHandlerMiddlewareImpl.cs#L117-L125), [`HttpProtocol.cs`](https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Servers/Kestrel/Core/src/Internal/Http/HttpProtocol.cs#L714-L764)). A server-side timeout token is not `RequestAborted`, so its OCE would still become a 500 unless caught.

Recommended policy:

| Situation | Response |
|---|---|
| Client disconnected | 499 for the completion event; no body; Debug |
| Server deadline/transient I/O, last-known-good exists | 200 + last-known-good, `Cache-Control: no-cache`, optional stale marker |
| Server deadline/transient I/O, no last-known-good | 503 + `Retry-After`, `Cache-Control: no-store` |
| Unknown session/manifest | 404 |
| Complete session | finalized index with one `#EXT-X-ENDLIST` |
| Unexpected bug | 500 |

499 is an unregistered nginx/ASP.NET convention, not an IANA status. RFC 9110 permits extension codes and clients treat unknown codes as the x00 of their class; 503 is “temporarily unable” and permits `Retry-After` ([RFC 9110 §15](https://www.rfc-editor.org/rfc/rfc9110.html#section-15), [§15.6.4](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.6.4), [IANA registry](https://www.iana.org/assignments/http-status-codes/http-status-codes.xhtml)). hls.js retries 5xx but not 4xx, and ignores `Retry-After` for playlists ([`error-helper.ts`](https://github.com/video-dev/hls.js/blob/v1.7.4/src/utils/error-helper.ts#L86-L90), `hls.mjs:25535`). A proxy that rewrites 499 to a 4xx would stop hls.js retries; keep 499 for the completion event rather than the client response.

`RequestLogPolicy.GetLevel` currently makes every 4xx a Warning (`RequestLogPolicy.cs:33-42`). The 320 aborts produced 320 warnings; client aborts should be Debug at the endpoint and the `/media` 499 completion event should be Verbose/Information. LKG/503 should be logged once per failure window.

## 5. Recorder shutdown

`RecorderJob.StopAsync` returns `ProcessStop.TimedOut` after 5 s graceful and 5 s force waits; the call site throws `TimeoutException` (`RecorderJob.cs:241`, `:388`). `ChannelCoordinator.RecordPartAsync` catches every exception, and `FailPartAsync` publishes `RecorderExited(..., RecorderExitKind.Exited, detail)` (`ChannelCoordinator.cs:283`/`:314`). `DvrReducer.RecordingExited` treats a part with more than 30 s of media as healthy, resets the failure streak, and schedules a retry after about 2 s (`DvrReducer.cs:189`). `StopJobAsync` already set `_job = null` before waiting (`:324`), so `StartJobAsync` sees no previous writer and starts the next part.

CliWrap 3.10.5 makes this worse:

- `ExecuteAsync(forceKillToken, gracefulToken)` sends SIGINT to the direct PID for graceful cancellation, and `Process.Kill(entireProcessTree: true)` for force.
- Both helpers catch all exceptions and return false; `ExecuteAsync` ignores the result.
- The returned task uses `WaitForExitAsync(CancellationToken.None)` and its `finally` awaits the process task, so the task is unbounded. Task completion means the direct process exited; a pending task can also mean a descendant holds stdout/stderr ([`Command.Execution.cs`](https://github.com/Tyrrrz/CliWrap/blob/3.10.5/CliWrap/Command.Execution.cs), [`ProcessExtensions.cs`](https://github.com/Tyrrrz/CliWrap/blob/3.10.5/CliWrap/Utils/Extensions/ProcessExtensions.cs)).
- .NET's `Process.Kill(true)` does not prove descendants exited: `WaitForExit`/`HasExited` “will indicate that exiting has completed after the given process exits, even if all descendants have not yet exited” ([`Process.Kill`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)).
- Linux `D` state is uninterruptible disk sleep; SIGKILL cannot be caught/blocked/ignored, but delivery waits until the task wakes ([`signal(7)`](https://man7.org/linux/man-pages/man7/signal.7.html), [`proc_pid_stat(5)`](https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html)).

The safe invariant is a per-channel writer lease: no new FFmpeg until the previous one is proven gone, or the channel faults and the host stops. A failed proof poisons the lease; do not retry it in-process. Capture the PID and start time, verify the PID is gone (or reused) after force-kill, and fail closed if the CliWrap task is still pending. Publish `RecorderExitKind.Killed` for the failed part, but keep the coordinator's `_shutdownFault` gate as the safety boundary because a media-producing killed part still schedules a retry. Do not null `_job` until verification. A restart can still race an orphan; a PID/start-time marker checked by recovery, or a systemd cgroup kill guarantee, is required for the full invariant.

## 6. Recorder byte tracking

`RecorderJob` calls `PartFiles.TotalBytes` inside the FFmpeg stdout handler (`RecorderJob.cs:118`). `PartFiles.TotalBytes` enumerates `p{part}_*.m4s` and `init_{part}*.mp4` and stats every file. For 6 s segments, one hour is 600 files; at 720 samples/hour that is roughly 432,000 length stats and 1,440 directory scans per part. On a slow volume a sample can exceed 5 s; CliWrap's `PipeTarget.ToDelegate` awaits the handler per line, so the scan blocks FFmpeg's stdout and can trip the 120 s stall watchdog.

FFmpeg's `total_size` is `N/A` for HLS: `ffmpeg.c` derives it from `of_filesize(output_files[0])`, and `ffmpeg_mux.c` falls back to `avio_tell` only for seekable output ([`ffmpeg.c`](https://github.com/FFmpeg/FFmpeg/blob/n9.0/fftools/ffmpeg.c#L575), [`ffmpeg_mux.c`](https://github.com/FFmpeg/FFmpeg/blob/n9.0/fftools/ffmpeg_mux.c#L51)). The HLS EVENT playlist is the muxer's own list of completed segments. A `PartByteTracker` can read the small playlist when it changes, parse `#EXT-X-MAP` and segment URIs, stat each new final segment once, ignore `.tmp`, and keep a monotonic high-water total. At part finish, do one authoritative `PartFiles.TotalBytes` scan for telemetry and the final log.

`FileSystemWatcher` is not authoritative: its buffer can overflow and it only provides blanket notification ([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0)). A watcher would still need a stat per event and could miss renames. Read the playlist instead.

## 7. Player waiting state

`VideoPlayer.razor.js` includes `manifestLoadError`, `manifestParsingError`, `levelLoadError`, and `levelEmptyError` in `WAITING_DETAILS` (`:37-40`), and calls `enterWaiting()` for any live listed error (`:574-585`). It does not include `levelLoadTimeOut`, so a timeout follows the generic fatal path. `enterWaiting()` always shows the spinner and the hard-coded “Waiting for the first segment.” (`VideoPlayer.razor:40`); `video.waiting` always spins (`:766`).

hls.js 1.7.4 (`src/StreamKeepr/wwwroot/lib/hls.js/hls.mjs`) emits `LEVEL_LOAD_ERROR`/`LEVEL_LOAD_TIMEOUT` with `fatal: false` initially; the error controller retries and sets `fatal` after recovery is exhausted ([`playlist-loader.ts`](https://github.com/video-dev/hls.js/blob/v1.7.4/src/loader/playlist-loader.ts), [`error-controller.ts`](https://github.com/video-dev/hls.js/blob/v1.7.4/src/controller/error-controller.ts)). `LEVEL_LOADED` fires on every successful playlist load; `LEVEL_LOADING` is not fired for the only media playlist loaded via `loadSource()`. `hls.levels.length` is not a reliable “media playlist loaded” flag; `hls.latestLevelDetails` is null until then.

The reliable split is:

- `hadPlaylist`: set on the first `LEVEL_LOADED` with fragments.
- `bufferedAhead()`: the buffered range containing `currentTime`, else 0.
- `networkUnhealthy`: set on a live playlist error, cleared on `LEVEL_LOADED`/`FRAG_LOADED`.

States:

| State | UI |
|---|---|
| Startup, `!hadPlaylist` | spinner + “Waiting for the first segment.” after 500 ms; 3 s manifest retry |
| Playing, buffer > 0.5 s | no spinner |
| Reconnecting, `hadPlaylist`, buffer > 0.5 s | “Reconnecting…”, no spinner |
| Stalled reconnecting, buffer ≤ 0.5 s | spinner + “Reconnecting…” |
| Failed, fatal after retries or Complete with no playlist | error overlay + Retry |

A seek into an unbuffered region is not reconnecting unless `networkUnhealthy`. `bufferStalledError` is a playback stall, not playlist health. `docs/player-contract.md` §13 currently mandates the broad waiting behavior and must be amended.

## 8. hls.js live playback mitigation

Decision (2026-11-20): set `maxBufferLength: 60` and a 30 s live playlist TTFB/load timeout. Keep retry counts/delays and buffer ceilings at hls.js defaults; keep the existing player-contract values.

hls.js 1.7.4 defaults to `playlistLoadPolicy.default.maxTimeToFirstByteMs = 10000` and `maxLoadTimeMs = 20000`; `levelLoadingTimeOut` is deprecated and sets both timeout fields. The XHR loader starts a TTFB timer, then re-arms to the total load time ([`config.ts`](https://github.com/video-dev/hls.js/blob/v1.7.4/src/config.ts), [`xhr-loader.ts`](https://github.com/video-dev/hls.js/blob/v1.7.4/src/utils/xhr-loader.ts)). The incident's manifest latency was 13.7 s median and 23 s p90, so 30 s covers most refreshes; the 41.4 s maximum still needs #244's server deadline and last-known-good policy.

`maxBufferLength` (30 s default) is a guaranteed floor “even if that value exceeds ... 60 MB”; `maxMaxBufferLength` is 600 s ([API.md](https://github.com/video-dev/hls.js/blob/v1.7.4/docs/API.md)). At 6.2 Mb/s, 60 s is about 46.5 MB per tab; at 8 Mb/s it is about 60 MB. `liveSyncDurationCount: 1` keeps the live latency; `maxBufferLength` only adds forward resilience.

Retry and buffer-ceiling overrides are not recommended:

- hls.js already defaults to two timeout retries at 0 ms and two error retries at 1 s/8 s. More retries would delay error surfacing and increase load during an outage; the player also has its own live waiting/retry loop.
- `maxMaxBufferLength` (600 s) is far above the 60 s target and only acts as a hard ceiling. Lowering it would reduce resilience without a measured memory problem.
- `maxBufferSize` (60 MB) is roughly the 60 s target at 8 Mb/s, and `maxBufferLength` is guaranteed above it. Raising it only increases memory.

`HLS_CONFIG` is shallow-merged over the defaults (`mergeConfig`), so a `playlistLoadPolicy.default` object must include the retry fields explicitly at their default values; a partial object would drop them.

## 9. Verification and open questions

- The storage diagnosis is inferred from the logs; the host must confirm the volume type, I/O errors, and SMART. Code changes cannot make EIO safe for recording.
- The 500s are partly a Development-environment artifact because `UseExceptionHandler` is not registered. Verify the production/proxy path before deciding whether the framework's 499 mapping applies.
- The exact cause of the six shutdown timeouts is unknown: `D` state, a denied/partial `Kill(true)`, or a descendant holding the pipes all produce the same symptom. All require fail-closed behavior.
- FFmpeg does not document byte-level append-only playlists. The incremental path relies on EVENT semantics plus exact prefix comparison and falls back to a rebuild.
- The 60 s buffer and 30 s playlist timeout are the committed values; retries and buffer ceilings remain hls.js defaults. Measure memory at the deployed bitrate if the buffer behavior changes.
- Changing 499 from Warning and changing the player waiting contract are contract changes and need owner approval.

## 10. References

- FFmpeg HLS: https://ffmpeg.org/ffmpeg-formats.html#hls-2; `hlsenc.c`: https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/hlsenc.c
- RFC 8216: https://www.rfc-editor.org/rfc/rfc8216.html (§6.2.1, §6.3.4)
- RFC 9110: https://www.rfc-editor.org/rfc/rfc9110.html (§15, §15.6.4, §10.2.3)
- ASP.NET Core: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services, https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory, `ExceptionHandlerMiddlewareImpl.cs`, `HttpProtocol.cs`
- hls.js 1.7.4: https://github.com/video-dev/hls.js/blob/v1.7.4/docs/API.md, `config.ts`, `xhr-loader.ts`, `playlist-loader.ts`, `error-controller.ts`, `level-helper.ts`
- CliWrap 3.10.5: https://github.com/Tyrrrz/CliWrap/blob/3.10.5/CliWrap/Command.Execution.cs, `ProcessExtensions.cs`, `PipeTarget.cs`
- .NET Process: https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0
- Linux signals and process state: https://man7.org/linux/man-pages/man7/signal.7.html, https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html
- FileSystemWatcher: https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0
