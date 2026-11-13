# Live H.264 transcode with Intel, NVIDIA, and AMD GPUs

Status: proposed **GO**, three-vendor GPU image, prototype-gated (2026-11-13)
Issue: [#144](https://github.com/brandtkstrom/StreamKeepr/issues/144) "Spike: evaluate VAAPI GPU acceleration for the Streamlink/FFmpeg recording image"
Feasibility evidence: [`vaapi-streamlink-spike.md`](vaapi-streamlink-spike.md)
Binding: no. Implementation needs an ADR, a transcode contract, and owner approval.

**Owner decision (2026-11-13).** Stream Keepr must transcode the live capture for **both live playback and persistent storage**, with GPU support for **Intel, NVIDIA, and AMD**. The VAAPI no-go in the feasibility memo applies only to putting a GPU encoder in the copy-only recorder. The product decision is now GO for a separate live H.264 sidecar.

## 1. Decision

Build a **live H.264/AAC sidecar** that reads the same capture as the source recorder and writes a persistent, browser-playable HLS archive. The source recording stays exactly as it is: Streamlink `--stdout` → FFmpeg `-c copy` → fMP4 HLS. A GPU encoder (VAAPI or NVENC) is an optional accelerator for the sidecar, never a dependency of the recorder.

- Source: unchanged, lossless, authoritative. `RecorderJob` and `FfmpegArgs.ForPart` are not modified.
- Proxy: H.264 High/AAC, fMP4 HLS in `session/proxy/`, playable live and after finalize.
- GPU matrix: Intel and AMD use VAAPI; NVIDIA uses NVENC/NVDEC. NVIDIA drivers are host-injected; the image never bakes them in.
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
| No VAAPI device, driver, or permission; no NVIDIA toolkit/NVENC | Probe fails once for that encoder; if neither works, proxy disabled; source records normally |
| GPU encode fails or stalls | Stop the proxy, mark `Failed`, delete the incomplete `proxy/` folder; source continues |
| GPU image absent (default image) | Probe reports unavailable; proxy disabled |
| Disk below the safety floor | Stop the proxy first; source recording keeps priority |
| App restart mid-proxy | Recovery finalizes a readable proxy, otherwise deletes it; never touches source parts |
| Proxy deletion/corruption | Source is unaffected; proxy status shows failed/missing |

A proxy failure publishes no `DvrEvent.RecorderExited`; it must never be interpreted as a source failure. The proxy has its own status channel.

### 3.3 Concurrency and capacity

- `Transcode:MaxConcurrent` default **1**. A live 1080p60 H.264 encode may share the GPU with several channels, but the safe default is one; raise it only after measurement.
- If no slot is free when a session's first media arrives, mark the proxy `Skipped` and continue the source. Do not queue a late start that would miss most of the session.
- The proxy encoder is best-effort. Set a bounded input reconnect/read timeout; a stuck proxy must not consume the only slot forever.

## 4. GPU image and deployment

The default image stays unchanged: static FFmpeg 9.0.2, copy-only, multi-arch.

Add a separate **`gpu` image variant** (amd64 only) that supports:

| Vendor | API | Image driver/libs | Runtime device | Encoder |
|---|---|---|---|---|
| Intel | VAAPI | `intel-media-va-driver` (iHD, Gen8+); `i965-va-driver` for older | `/dev/dri/renderD128` + host render GID | `h264_vaapi` |
| AMD | VAAPI | `mesa-va-drivers` (radeonsi) | `/dev/dri/renderD128` + host render GID | `h264_vaapi` |
| NVIDIA | NVENC/NVDEC | **No driver in the image.** Host driver + NVIDIA Container Toolkit; FFmpeg must have NVENC support | NVIDIA runtime (`--gpus` / Compose `gpus`); no `/dev/dri` | `h264_nvenc` |

- Ubuntu 24.04 runtime (the existing `mcr.microsoft.com/dotnet/aspnet:10.0.14` base). Ubuntu's `ffmpeg` 6.1.1 is built with both VAAPI and NVENC; the package lists `h264_vaapi`, `h264_nvenc`, `hevc_nvenc`, and `av1_nvenc` (verified 2026-11-13).
- Apt packages: `ffmpeg intel-media-va-driver i965-va-driver mesa-va-drivers vainfo`. `libva2`/`libva-drm2` come in as dependencies. Do **not** install an NVIDIA driver in the image; the toolkit injects it. If the prototype shows a gap in Ubuntu's FFmpeg, fall back to the linuxserver/docker-ffmpeg build pattern or use that image as a separate transcoder.
- **Driver freshness:** Ubuntu 24.04 ships iHD 24.1.0, Mesa 25.2.8, and FFmpeg 6.1.1. The linuxserver image compiles iHD 26.3.5, Mesa 26.2.4, and FFmpeg 9.0.2. The apt stack is likely sufficient for Gen8-12 Intel and common AMD; newer Intel (Meteor Lake/Lunar Lake/Battlemage) or AMD GPUs may need the newer stack. The prototype records the GPU model, `vainfo`, `ffmpeg -version`, and driver version. If the target is newer than the apt stack supports, use the linuxserver image as a separate transcoder or replicate its build in the gpu stage; do not copy Resolute-built libraries into the Noble runtime.
- Keep the static `ffmpeg`/`ffprobe` at `/usr/local/bin` for `-c copy`; use `/usr/bin/ffmpeg` for transcode (`Transcode:FfmpegPath`).
- Environment in the gpu image:
  - `NVIDIA_DRIVER_CAPABILITIES=video,utility` by default. Add `compute` only if a CUDA/NVDEC interop path needs it; the linuxserver image uses `compute,video,utility`.
  - `NVIDIA_VISIBLE_DEVICES=all` (or let Compose `gpus` set it).
  - Optional `LIBVA_DRIVER_NAME=iHD` or `i965`; leave unset for driver autodetect.
- Compose, Intel/AMD:

```yaml
services:
  stream-keepr:
    devices:
      - /dev/dri/renderD128:/dev/dri/renderD128:rw
    group_add:
      - "109"   # host render GID; read it with: stat -c '%g' /dev/dri/renderD128
```

- Compose, NVIDIA:

```yaml
services:
  stream-keepr:
    gpus: all
    environment:
      NVIDIA_DRIVER_CAPABILITIES: video,utility
```

  or the portable reservation form:

```yaml
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
```

  The host needs the NVIDIA driver and the [NVIDIA Container Toolkit](https://github.com/NVIDIA/nvidia-container-toolkit). This is the same model the linuxserver/docker-ffmpeg image documents: its Dockerfile sets `NVIDIA_DRIVER_CAPABILITIES="compute,video,utility"` and `NVIDIA_VISIBLE_DEVICES="all"`, and does not bake the driver into the image. Its FFmpeg build enables `--enable-vaapi --enable-nvenc --enable-nvdec --enable-cuvid --enable-libvpl`; Ubuntu 24.04's package already covers the first slice.
- Both Intel/AMD and NVIDIA can be exposed at once; the probe chooses one encoder. `Transcode:Encoder=auto|vaapi|nvenc` (auto prefers a working NVENC, then VAAPI, then disabled); `Transcode:VaapiDevice` default `/dev/dri/renderD128`; `Transcode:NvidiaGpu` default 0.
- Probe with a **tiny real encode**, not just `-encoders`: VAAPI (`vainfo`, then a short `h264_vaapi` encode) and NVENC (a short `h264_nvenc` encode) under a 5 s timeout. Cache the selected encoder. A missing device, toolkit, driver, or permission disables that encoder; if both fail, the proxy is disabled. Never fail startup or recording.
- The default image must not require `/dev/dri`, `libva`, or a GPU. It reports the proxy as unavailable.
- The `gpu` variant is built and published as a separate amd64 tag. The existing `linux/amd64,linux/arm64` publish (`.github/workflows/publish.yml:65`) is unchanged. Intel/AMD/NVIDIA GPUs are an x86_64 target; NVIDIA Jetson/arm64 is out of scope.
- No `privileged` container. Pass only the render node for VAAPI and only the NVIDIA runtime for NVENC.

## 5. Storage, session, and UI

### 5.1 Persistence

Use a separate `proxy/proxy.json` rather than changing the session manifest schema:

```json
{
  "version": 1,
  "status": "recording|complete|failed|skipped",
  "startedAt": "2026-11-13T16:30:00Z",
  "endedAt": null,
  "encoder": "h264_vaapi|h264_nvenc",
  "vendor": "intel|amd|nvidia",
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
| New probe | `src/StreamKeepr/Infrastructure/Media/GpuEncoderProbe.cs` (VAAPI + NVENC) |
| Coordinator | `ChannelCoordinator` starts on first `PartProducingMedia`, stops before finalize/shutdown, isolates failures |
| Factory/DI | `ChannelCoordinatorFactory`, `Program.cs`, new `TranscodeOptions` (`Encoder`, `VaapiDevice`, `NvidiaGpu`, `FfmpegPath`) |
| Store | `SessionStore` proxy JSON/finalize/recovery; `RecordingSummary`; `RecordingLibrary` |
| Media | `LiveIndexEndpoint` (source playlist already exists), `RecordingsMedia` (serves `proxy/`), no new public route required |
| UI | `LibraryView`, `LibraryViewModels`, `VideoPlayer.razor`, `StatsPanel.razor`, `RecordingDetails.razor`, channel status |
| Image/CI | `Dockerfile` gpu target, `.github/workflows/publish.yml`, `deploy/compose.yaml`, README/deploy docs |
| Tests | Fake FFmpeg script, HLS fixture, fake probe, coordinator/store/UI tests; no GPU in CI |
| Docs | New ADR, transcode contract, testing contract, glossary, decision digest |

The proxy job should reuse the bounded process/stop/watchdog logic where it fits. It does not need a bounded pipe or a second Streamlink; it needs one FFmpeg process, a progress parser, a stderr tail, and a short graceful/force stop.

## 7. Prototype and implementation slices

**Prototype status (2026-11-13).** Disposable harness on branch `prototype/live-transcode` (commit `e249b8c`, not merged). The software gate passed locally:

- FFmpeg followed the stitched EVENT playlist from part 1 into part 2 across `#EXT-X-DISCONTINUITY` and a new `#EXT-X-MAP`;
- produced six H.264/AAC fMP4 segments with an ENDLIST (11.77 s);
- left the source part files unchanged.

The first harness run exposed a fixture issue, not a production finding: independently generated parts had out-of-order `#EXT-X-PROGRAM-DATE-TIME`. Real recorder parts are sequential; with sequential fixture times the output duration is correct. Intel/AMD VAAPI and NVIDIA NVENC rows are **UNVERIFIED** on the macOS host; run `./prototype/live-transcode/run.sh vaapi|nvidia|all` on hardware. The harness is disposable and must not be merged.

**Slice 0 — prototype gate (1-1.5 days, disposable).** Feed a dynamic FFmpeg a live stitched playlist from a fake recording with two parts and a discontinuity; verify the five gate conditions in §2.1. Run the encoder matrix:

- Intel VAAPI: `vainfo` shows iHD, then a short `h264_vaapi` encode with `/dev/dri/renderD128`.
- AMD VAAPI: `vainfo` shows radeonsi, then a short `h264_vaapi` encode.
- NVIDIA NVENC: container started with the NVIDIA runtime/toolkit, then a short `h264_nvenc` encode.

If a vendor's hardware is unavailable, mark that row **UNVERIFIED**; do not claim it passed. Record the exact FFmpeg command, latency, CPU/GPU use, and failure modes. The live-playlist gate is vendor-independent; the encoder matrix is per vendor. If the playlist gate fails, stop and evaluate the second-Streamlink fallback.

**Slice 1 — GPU image and probe (1-1.5 days).** Build the amd64 variant with the VAAPI drivers; add the NVIDIA runtime env and Compose examples; add the encoder probe. Verify `vainfo` and a small VAAPI encode under UID 1654 with `/dev/dri`; verify `h264_nvenc` with the NVIDIA toolkit; verify the default image still builds and reports unavailable.

**Slice 2 — live transcode job and coordinator (2-3 days).** `ILiveTranscoder`/`LiveTranscodeJob`, vendor-aware `TranscodeArgs`, start on first media, stop before finalize, max concurrency, failure isolation, fake-FFmpeg tests.

**Slice 3 — persistence and playback (2-3 days).** `proxy.json`, `SessionStore` operations, recovery, `RecordingSummary`/library, proxy URL, player preference, live status, UI.

**Slice 4 — settings, retention, disk, hardening (2 days).** Options/validation, skip/queue policy, disk priority, source-delete opt-in (if approved), logs/metrics, manual Intel/AMD/NVIDIA matrix.

**Slice 5 — docs and review (1 day).** ADR, transcode contract, testing contract, deploy docs, full suite/format/code review.

Rough total after the prototype: **1.5-2 weeks.** The prototype result decides whether the live-playlist input or the second-Streamlink fallback is the implementation base.

## 8. Acceptance criteria and open decisions

Acceptance:

- Source recording is byte-identical in behavior with the proxy disabled or failed.
- A session produces a playable live proxy and a persistent H.264/AAC archive; the source remains playable/exportable.
- A working VAAPI (Intel/AMD) or NVENC (NVIDIA) encoder is used; a missing GPU/toolkit/driver/permission disables the proxy and never fails a recording.
- Proxy failure is visible in status/logs and never becomes a source `RecorderExited`.
- Max concurrency and disk pressure are enforced; the source wins.
- The default image remains multi-arch and GPU-free; the gpu image is documented as amd64-only.
- Recovery handles an interrupted proxy without touching source parts.

Open decisions:

1. **Source retention:** keep both (recommended) or offer auto-delete after proxy success.
2. **Quality target:** 1080p/6 Mbps default; confirm whether 1440p or a lower bitrate is wanted.
3. **Transcode policy:** always create a proxy, or only when the source is HEVC/AV1 or above the target bitrate. Always is simpler; needed-only saves GPU/storage.
4. **Max concurrency:** default 1; measure the target GPU before raising it.
5. **Fallback:** if the live-playlist prototype fails, accept the second-Streamlink bandwidth cost or fall back to post-recording transcode.
6. **Hardware decode:** software decode + VAAPI/NVENC encode baseline; add hardware decode only if the CPU cannot keep up.
7. **Encoder priority:** auto prefers NVENC, then VAAPI. Confirm for hosts with both an Intel iGPU and an NVIDIA dGPU.
8. **Vendor coverage:** Intel iHD (Gen8+), optional i965; AMD Mesa; NVIDIA NVENC. Jetson/arm64 is out of scope.
9. **NVIDIA capabilities:** start with `video,utility`; add `compute` only if NVDEC/CUDA interop needs it.

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
- [linuxserver/docker-ffmpeg](https://github.com/linuxserver/docker-ffmpeg) — multi-vendor driver/build reference: FFmpeg configured with `--enable-vaapi --enable-nvenc --enable-nvdec --enable-cuvid --enable-libvpl`; Intel iHD/i965/Mesa compiled in; NVIDIA driver left to the Container Toolkit; `NVIDIA_DRIVER_CAPABILITIES`/`NVIDIA_VISIBLE_DEVICES`.
- [NVIDIA Container Toolkit](https://github.com/NVIDIA/nvidia-container-toolkit) — host driver injection for NVIDIA NVENC/NVDEC.
- [Ubuntu 24.04 ffmpeg](https://packages.ubuntu.com/noble/ffmpeg) — package used for the first gpu image; verified to include `h264_vaapi` and `h264_nvenc`.
- FFmpeg VAAPI/NVENC encoder and HLS demuxer docs; Docker Compose `devices`/`group_add`; Intel media driver — see the feasibility memo's source list.
