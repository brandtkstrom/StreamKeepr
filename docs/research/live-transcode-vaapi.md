# Live H.264 transcode for Stream Keepr captures (VAAPI)

Status: proposed **GO**, prototype-gated (2026-11-13)
Issue: [#144](https://github.com/brandtkstrom/StreamKeepr/issues/144) "Spike: evaluate VAAPI GPU acceleration for the Streamlink/FFmpeg recording image"
Feasibility evidence: [`vaapi-streamlink-spike.md`](vaapi-streamlink-spike.md)
Binding: no. Implementation needs an ADR, a transcode contract, and owner approval.

**Owner decision (2026-11-13).** Stream Keepr must transcode the live capture for **both live playback and persistent storage**. The VAAPI no-go in the feasibility memo applies only to putting VAAPI in the copy-only recorder. The product decision is now GO for a separate live H.264 sidecar.

## 1. Decision

Build a **live H.264/AAC sidecar** that reads the same capture as the source recorder and writes a persistent, browser-playable HLS archive. The source recording stays exactly as it is: Streamlink `--stdout` → FFmpeg `-c copy` → fMP4 HLS. VAAPI is an optional accelerator for the sidecar, never a dependency of the recorder.

- Source: unchanged, lossless, authoritative. `RecorderJob` and `FfmpegArgs.ForPart` are not modified.
- Proxy: H.264 High/AAC, fMP4 HLS in `session/proxy/`, playable live and after finalize.
- Default playback: the proxy when present; the source remains available for fidelity and export.
- Source retention: keep both by default. Deleting the source after a successful proxy is an explicit, destructive opt-in.
- Live GPU failure: the proxy stops; the source recording continues. Do not add a software live transcode by default.

## 2. Architecture

```text
source recorder (unchanged, reliable)
  Streamlink --stdout -> bounded pipe -> FFmpeg -c copy -> source fMP4 HLS

live proxy (best effort, GPU)
  app live playlist  -> FFmpeg (dynamic, VAAPI) -> proxy H.264/AAC HLS
```

### 2.1 Preferred input: the app's stitched live playlist

The proxy reads the source's live playlist from the app's own media endpoint:

```text
http://127.0.0.1:8080/media/{channel}/{id}/index.m3u8
```

That endpoint is `LiveIndexEndpoint` (`src/StreamKeepr/Hosting/LiveIndexEndpoint.cs:13`). For a recording session it returns `SessionStore.StitchLiveAsync`, which stitches the non-empty part playlists in part order, keeps `#EXT-X-MAP` lines, and inserts `#EXT-X-DISCONTINUITY` between parts (`src/StreamKeepr/Infrastructure/Storage/PlaylistStitcher.cs:40-88`).

Why this is the preferred first design:

- **Same capture.** No second Streamlink process, no second Twitch download, no extra Twitch connection.
- **Aligned.** The proxy sees the same segments as the source, including ad breaks and part boundaries.
- **Isolated.** The proxy reads finalized segment files over HTTP; it cannot backpressure the recorder pipe. A slow or dead GPU only makes the proxy lag or fail.
- **Simple failure boundary.** The source has no knowledge of the proxy process.

**Prototype gate.** Before production work, run a dynamic FFmpeg against a real or fake generation of this live playlist and prove it:

1. follows an EVENT playlist with no `#EXT-X-ENDLIST`;
2. handles a part boundary with `#EXT-X-DISCONTINUITY` and a new `#EXT-X-MAP:URI="init_NNN.mp4"`;
3. starts at the first available segment (`-live_start_index 0`) and catches up to the live edge;
4. emits a playable H.264/AAC fMP4 HLS playlist with an ENDLIST on graceful stop;
5. never reads or blocks the source pipe.

If the gate fails, the fallback is a **second Streamlink sidecar** (a second `--stdout` capture into the VAAPI FFmpeg). That is simpler to run but doubles Twitch bandwidth and is not frame-aligned; it is a fallback, not the preferred design.

### 2.2 Output

```text
session/
├── p001.m3u8, init_001.mp4, p001_00001.m4s   # source, unchanged
└── proxy/
    ├── index.m3u8                            # EVENT live, ENDLIST after finalize
    ├── init.mp4
    ├── proxy_00001.m4s
    └── proxy.json                            # status/metadata, not served
```

- H.264 High profile, `yuv420p`, AAC. Default: 1080p maximum (no upscale), 6 Mbps VBR, 160 kbps AAC, 2 s segments.
- `proxy/index.m3u8` is served by the existing `/media` static middleware; `.m3u8` is already no-cache (`src/StreamKeepr/Hosting/RecordingsMedia.cs:10-32`).
- `proxy.json` is outside the media allowlist and is not served.

### 2.3 Why not transcode inside the recorder

A single FFmpeg with two outputs, or a tee into two FFmpegs, couples the source's progress to the GPU encoder. If VAAPI stalls, the copy output can back up and the recording suffers. The source path must remain the one path that never fails. A sidecar that reads finalized segments preserves that invariant.

## 3. Lifecycle and failure semantics

### 3.1 Start and stop

- Start the proxy after the first `DvrEvent.PartProducingMedia` for a session, when at least one source segment exists. Do not start it before the playlist is readable.
- One proxy per session. If the source retries a part, the proxy continues on the same session; if the proxy has failed, it does not restart.
- Stop the proxy before `SessionStore.FinalizeAsync` on offline, stop, stream change, or host shutdown. Give it a short bounded stop (10 s); the source keeps its existing 35 s budget.
- If the source enters `WaitingToRetry` without a finalize, the proxy may continue. If the source changes stream, the old session finalizes and the new one starts a new proxy.

### 3.2 Failure rules

| Failure | Behaviour |
|---|---|
| No VAAPI device, driver, or permission | Probe fails once; proxy disabled; source records normally |
| GPU encode fails or stalls | Stop the proxy, mark `Failed`, delete the incomplete `proxy/` folder; source continues |
| VAAPI image absent (default image) | Probe reports unavailable; proxy disabled |
| Disk below the safety floor | Stop the proxy first; source recording keeps priority |
| App restart mid-proxy | Recovery finalizes a readable proxy, otherwise deletes it; never touches source parts |
| Proxy deletion/corruption | Source is unaffected; proxy status shows failed/missing |

A proxy failure publishes no `DvrEvent.RecorderExited`; it must never be interpreted as a source failure. The proxy has its own status channel.

### 3.3 Concurrency and capacity

- `Transcode:MaxConcurrent` default **1**. A live 1080p60 H.264 encode may share the GPU with several channels, but the safe default is one; raise it only after measurement.
- If no slot is free when a session's first media arrives, mark the proxy `Skipped` and continue the source. Do not queue a late start that would miss most of the session.
- The proxy encoder is best-effort. Set a bounded input reconnect/read timeout; a stuck proxy must not consume the only slot forever.

## 4. Image and deployment

The default image stays unchanged: static FFmpeg 9.0.2, copy-only, multi-arch.

Add a separate **`vaapi` image variant** (amd64 only):

- Ubuntu 24.04 runtime (the existing `mcr.microsoft.com/dotnet/aspnet:10.0.14` base).
- Dynamic `ffmpeg` 6.1.1 + `libva2`/`libva-drm2` + `intel-media-va-driver` + `vainfo` (+ `mesa-va-drivers` for AMD if we claim AMD support).
- Keep the static `ffmpeg`/`ffprobe` at `/usr/local/bin` for `-c copy`; use `/usr/bin/ffmpeg` for transcode (`Transcode:FfmpegPath`).
- `Transcode:Enabled=true` and `Transcode:FfmpegPath=/usr/bin/ffmpeg` only in this variant.
- Compose fragment (documented, not default):

```yaml
services:
  stream-keepr:
    devices:
      - /dev/dri/renderD128:/dev/dri/renderD128:rw
    group_add:
      - "109"   # host render GID; read it with: stat -c '%g' /dev/dri/renderD128
```

- The default image must not require `/dev/dri`, `libva`, or a GPU. It reports the proxy as unavailable.
- The `vaapi` variant is built and published as a separate amd64 tag. The existing `linux/amd64,linux/arm64` publish (`.github/workflows/publish.yml:65`) is unchanged.
- Probe once with `vainfo` or a tiny `ffmpeg -init_hw_device vaapi` check under a 5 s timeout; cache the result. A missing device, driver, or group permission disables the proxy, never startup.

## 5. Storage, session, and UI

### 5.1 Persistence

Use a separate `proxy/proxy.json` rather than changing the session manifest schema:

```json
{
  "version": 1,
  "status": "recording|complete|failed|skipped",
  "startedAt": "2026-11-13T16:30:00Z",
  "endedAt": null,
  "encoder": "h264_vaapi",
  "width": 1920,
  "height": 1080,
  "bitrate": 6000000,
  "error": null
}
```

`SessionStore` gains proxy operations that use the session lock:

- `StartProxyAsync(session, settings)` — create `proxy/`, write `proxy.json`.
- `CompleteProxyAsync(session)` — verify the playlist has segments, append `#EXT-X-ENDLIST` if the FFmpeg stop was forced and the playlist is otherwise valid, update `proxy.json`.
- `FailProxyAsync(session, error)` — mark failed and delete incomplete files.
- `RecoverProxyAsync(...)` — startup repair, called from `RecoveryService`.

The source manifest, part files, `SessionStore.IndexFileName`, and source `SizeBytes` are unchanged. Proxy bytes are reported separately.

### 5.2 Playback and UI

- `RecordingSummary` gains `ProxyStatus` and `ProxyPlaylistUrl` (`/media/{channel}/{id}/proxy/index.m3u8`).
- `RecordingLibrary`/`SessionStore.SummarizeAsync` read `proxy.json`; a missing or failed proxy leaves the source-only summary.
- The player prefers the proxy when its status is `recording` or `complete`; otherwise it uses the source playlist. The source remains available as an explicit choice.
- The live channel status shows proxy state (`live`, `preparing`, `unavailable`, `failed`, `skipped`) next to the source recording state. A separate `TranscodeStatus` service is cleaner than adding proxy state to `DvrSnapshot`.
- The details/stats views show the proxy playlist URL and status; the source URL remains.
- Export remains source-based for the first slice. Exporting the proxy is a stream copy and can be a follow-up.

### 5.3 Retention

Keep source and proxy by default. A future `Transcode:DeleteSourceAfterProxy` option may delete the source only after a verified `complete` proxy; it is destructive, off by default, and requires a confirmation in Settings. Do not delete source parts automatically in the first slice.

## 6. Code seams and affected files

| Area | Change |
|---|---|
| Source recorder | **None.** `RecorderJob`, `FfmpegArgs`, `StreamlinkArgs` keep the copy path |
| New job | `src/StreamKeepr/Infrastructure/Media/LiveTranscodeJob.cs` + `ILiveTranscoder` |
| New args | `src/StreamKeepr/Infrastructure/Media/TranscodeArgs.cs` |
| New probe | `src/StreamKeepr/Infrastructure/Media/VaapiProbe.cs` |
| Coordinator | `ChannelCoordinator` starts on first `PartProducingMedia`, stops before finalize/shutdown, isolates failures |
| Factory/DI | `ChannelCoordinatorFactory`, `Program.cs`, new `TranscodeOptions` |
| Store | `SessionStore` proxy JSON/finalize/recovery; `RecordingSummary`; `RecordingLibrary` |
| Media | `LiveIndexEndpoint` (source playlist already exists), `RecordingsMedia` (serves `proxy/`), no new public route required |
| UI | `LibraryView`, `LibraryViewModels`, `VideoPlayer.razor`, `StatsPanel.razor`, `RecordingDetails.razor`, channel status |
| Image/CI | `Dockerfile` vaapi target, `.github/workflows/publish.yml`, `deploy/compose.yaml`, README/deploy docs |
| Tests | Fake FFmpeg script, HLS fixture, fake probe, coordinator/store/UI tests; no GPU in CI |
| Docs | New ADR, transcode contract, testing contract, glossary, decision digest |

The proxy job should reuse the bounded process/stop/watchdog logic where it fits. It does not need a bounded pipe or a second Streamlink; it needs one FFmpeg process, a progress parser, a stderr tail, and a short graceful/force stop.

## 7. Prototype and implementation slices

**Slice 0 — prototype gate (0.5-1 day, disposable).** Feed a dynamic FFmpeg a live stitched playlist from a fake recording with two parts and a discontinuity; encode with VAAPI on a real Intel/AMD host; verify the five gate conditions in §2.1. Record the exact FFmpeg command, latency, CPU/GPU use, and failure modes. If the gate fails, stop and evaluate the second-Streamlink fallback.

**Slice 1 — VAAPI image and probe (1 day).** Build the amd64 variant, install the driver stack, add the probe and Compose docs; verify `vainfo` and a small encode under UID 1654 with `/dev/dri`; verify the default image still builds and reports unavailable.

**Slice 2 — live transcode job and coordinator (2-3 days).** `ILiveTranscoder`/`LiveTranscodeJob`, `TranscodeArgs`, start on first media, stop before finalize, max concurrency, failure isolation, fake-FFmpeg tests.

**Slice 3 — persistence and playback (2-3 days).** `proxy.json`, `SessionStore` operations, recovery, `RecordingSummary`/library, proxy URL, player preference, live status, UI.

**Slice 4 — settings, retention, disk, hardening (2 days).** Options/validation, skip/queue policy, disk priority, source-delete opt-in (if approved), logs/metrics, manual VAAPI matrix.

**Slice 5 — docs and review (1 day).** ADR, transcode contract, testing contract, deploy docs, full suite/format/code review.

Rough total after the prototype: **1.5-2 weeks.** The prototype result decides whether the live-playlist input or the second-Streamlink fallback is the implementation base.

## 8. Acceptance criteria and open decisions

Acceptance:

- Source recording is byte-identical in behavior with the proxy disabled or failed.
- A session produces a playable live proxy and a persistent H.264/AAC archive; the source remains playable/exportable.
- VAAPI is used when available; a missing GPU/driver/permission disables the proxy and never fails a recording.
- Proxy failure is visible in status/logs and never becomes a source `RecorderExited`.
- Max concurrency and disk pressure are enforced; the source wins.
- The default image remains multi-arch and GPU-free; the vaapi image is documented as amd64-only.
- Recovery handles an interrupted proxy without touching source parts.

Open decisions:

1. **Source retention:** keep both (recommended) or offer auto-delete after proxy success.
2. **Quality target:** 1080p/6 Mbps default; confirm whether 1440p or a lower bitrate is wanted.
3. **Transcode policy:** always create a proxy, or only when the source is HEVC/AV1 or above the target bitrate. Always is simpler; needed-only saves GPU/storage.
4. **Max concurrency:** default 1; measure the target GPU before raising it.
5. **Fallback:** if the live-playlist prototype fails, accept the second-Streamlink bandwidth cost or fall back to post-recording transcode.
6. **Hardware decode:** software decode + VAAPI encode baseline; add hardware decode only if the CPU cannot keep up.

## 9. References

- [`vaapi-streamlink-spike.md`](vaapi-streamlink-spike.md) — static FFmpeg/VAAPI evidence, image cost, host matrix, benchmark plan.
- `src/StreamKeepr/Infrastructure/Media/RecorderJob.cs` — source recorder process and shutdown model.
- `src/StreamKeepr/Infrastructure/Media/FfmpegArgs.cs` — current `-c copy` source args.
- `src/StreamKeepr/Infrastructure/Storage/PlaylistStitcher.cs` — stitched live playlist and part discontinuities.
- `src/StreamKeepr/Hosting/LiveIndexEndpoint.cs` — `/media/{channel}/{id}/index.m3u8`.
- `src/StreamKeepr/Infrastructure/Runtime/ChannelCoordinator.cs` — per-channel event loop and job lifecycle.
- `src/StreamKeepr/Infrastructure/Storage/SessionStore.cs`, `RecordingSummary.cs` — manifest/finalize/summary.
- `src/StreamKeepr/Hosting/RecordingsMedia.cs` — `/media` allowlist and caching.
- `Dockerfile`, `.github/workflows/publish.yml`, `deploy/compose.yaml` — image and deployment seams.
- FFmpeg VAAPI encoder and HLS demuxer docs; Docker Compose `devices`/`group_add`; Intel media driver — see the feasibility memo's source list.
