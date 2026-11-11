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
- Drain stdout and stderr concurrently. The CliWrap command task completes only after the process exits and both streams reach EOF.

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

### Exit classification

| Kind | Rule |
|---|---|
| `Stopped` | The coordinator's stop token was requested and no watchdog/force path won. The domain ignores a late stop event; the `StopRequested` path already paused/finalized. |
| `Killed` | The stall watchdog fired, or a force-kill token was used because a graceful wait timed out. |
| `Exited` | Streamlink or FFmpeg ended on its own: stream end, network failure, or tool crash. |

Precedence when more than one applies: `Stopped` > `Killed` > `Exited`.

## 6. Progress and media detection

- Parse `-progress` key=value lines and emit a sample when `progress=continue` or `progress=end` arrives.
- `total_size` is `N/A` for the HLS muxer. Compute `TotalBytes` from the part's init and segment files on each sample; report 0 only when no files exist.
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
- It publishes exactly one `RecorderExited`. If an unexpected exception escapes, the coordinator synthesizes a `RecorderExited(Exited, detail)`; the event count stays one.
- If no media was produced, delete every part file including `.tmp` and init temp files before returning.
- If the part already exists, return `RecorderExited` immediately; do not overwrite.
- `RecorderExited.Detail` carries the reason, exit codes, and the last stderr lines. It is diagnostic; the reducer uses `RecorderExitKind`, never the detail string.

## 9. Failure modes

| Scenario | Recorder behaviour | Exit kind |
|---|---|---|
| Streamlink reaches stream end | FFmpeg gets EOF, finalizes the part; job returns | `Exited` |
| Streamlink network failure | Same as stream end; the observation/state machine decides whether to retry | `Exited` |
| FFmpeg dies first | Complete the pipe reader, stop Streamlink, return; the part may lack a trailer, recovery repairs | `Exited` |
| User stop | Stop Streamlink, EOF FFmpeg, finalize, return | `Stopped` |
| Stall watchdog | Stop both processes with the bounded shutdown; return | `Killed` |
| No media | Delete part files, return | `Exited` |
| Part files already exist | Return without starting | `Exited` |
| Tool missing or fails to start | Force-kill any child, return with the error detail | `Exited` |
| App crash / container teardown | No trailer; recovery deletes `.tmp`/orphans and finalizes | n/a |

## 10. Test matrix

Unit:

- `PartFiles` paths and the delete globs, including `.tmp` and init temp files.
- FFmpeg and Streamlink argument builders, including `-bsf:a aac_adtstoasc` and `--twitch-supported-codecs h264,h265,av1`.
- `FfmpegProgressParser`: `total_size=N/A`, `out_time_us`, `progress=continue`, `progress=end`, missing keys, first-block-only parts.
- `StderrTail` capacity and ordering.
- Ad-break regex and grace calculation.

Integration with the fake Streamlink and a synthetic media fixture:

- Normal stream end: part playlist, init, segments, media event before exit event.
- Streamlink ends first; FFmpeg ends first.
- User stop: graceful order, `Stopped`.
- Stall watchdog: `Killed`, bounded shutdown.
- No media: files deleted.
- Very short part with only the final progress block: media event still published.
- Part already exists: immediate `RecorderExited`.
- Ad-break log line: watchdog does not fire during the grace.
- MPEG-TS/AAC input succeeds with `-bsf:a aac_adtstoasc`.
- HEVC/AV1 argument path records successfully (playback support is a separate test).

## 11. Accepted risks

- **HEVC/AV1 playback:** `h265,av1` may produce codecs not supported by every browser. The player must surface an unsupported-codec error; the export may also be HEVC/AV1. Owner: **Player, stats, and resume** and **Details, export, delete, auth UX, and settings**.
- **HEVC tagging:** FFmpeg may tag HEVC as `hev1`; MP4 export should use `-tag:v hvc1` where required. Owner: **Details, export, delete, auth UX, and settings**.
- **Twitch container mix:** Streamlink may emit fMP4 or MPEG-TS/AAC; the bitstream filter covers the latter.
- **Live target duration drift:** a part spanning an ad break can change the target duration; the session store must use a stable live value. Owner: **Session store, stitching, recovery, and retention**.

## 12. Binding versus implementation freedom

Binding: the two-process model, part file names, argument set, shutdown order and budgets, exit-kind rules, progress/media detection, stall/ad-break behaviour, recorder boundary, cleanup, and event order.

Freedom: CliWrap versus raw `Process`, pipe sizes beyond the 4 MiB recommendation, parser internals, logging, regex, and test harness details.

## 13. Related

- `docs/domain-model.md` for `RecorderExited`, `PartProducingMedia`, and the state transitions.
- `docs/reviews/implementation-plan-amendments.md` (A-01, A-07, A-11).
- `docs/research/recorder-process.md` for the Linux process evidence.
