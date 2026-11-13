# Spike: VAAPI acceleration for the Streamlink/FFmpeg recording image

Status: research memo, **no-go for the copy-only recorder**; superseded for the product decision by [`live-transcode-vaapi.md`](live-transcode-vaapi.md) (2026-11-13)
Issue: [#144](https://github.com/brandtkstrom/StreamKeepr/issues/144) "Spike: evaluate VAAPI GPU acceleration for the Streamlink/FFmpeg recording image"
Binding: no. This memo informs a decision; it changes no code, image, Compose file, or contract. Scope: research only; no production code, Dockerfile, Compose, or test change.

**Owner update (2026-11-13).** Live transcoding is approved for both playback and storage. The no-go below applies only to adding VAAPI to the copy-only recorder. See [`live-transcode-vaapi.md`](live-transcode-vaapi.md) for the live sidecar design and the GO decision.

**Method.** Repository citations are `path:line` at `7b2da86` (branch `docs/vaapi-streamlink-spike`). I read the issue, `Dockerfile`, `FfmpegArgs.cs`, `ExportService.cs`, `ExportPartProbe.cs`, `docs/recorder-contract.md`, both Compose files, `README.md`, and the implementation plan. I pulled `mwader/static-ffmpeg:9.0.2-1` with Docker 29.9.3 on macOS 27.2 arm64 and ran the FFmpeg checks in §3 on arm64 and amd64; the `/dev/dri` check was on this host. I also pulled `mcr.microsoft.com/dotnet/aspnet:10.0.14` (Ubuntu 24.04.5) and measured the amd64 VAAPI package install under `--platform linux/amd64`. No VAAPI encode was run: this host has no `/dev/dri`, and Docker Desktop's Linux VM rejects `--device /dev/dri`. Web sources were fetched on 2026-11-13; the FFmpeg Trac wiki is behind Anubis, so §5 quotes the Wayback snapshot of 2026-01-22 (page last modified 2024-01-28). No credentials, no Twitch API calls, no production edits.

## 1. Summary and recommendation

**Recommendation: NO-GO for VAAPI in the copy-only recorder.** VAAPI has no workload to accelerate in the current recorder, and the shipped FFmpeg cannot use it even if a GPU were present. The owner has since approved a live transcode sidecar; that is a **GO** in [`live-transcode-vaapi.md`](live-transcode-vaapi.md), which keeps the recorder copy-only.

Three independent blockers:

1. **No decode or encode exists in the recorder or export.** The recorder sends Streamlink's HLS bytes into FFmpeg and remuxes with `-c copy` (`docs/recorder-contract.md:13`, `src/StreamKeepr/Infrastructure/Media/FfmpegArgs.cs:20-22`). MP4 export also stream-copies (`src/StreamKeepr/Infrastructure/Storage/ExportService.cs:434-443`). VAAPI only accelerates decode, filter, or encode stages; the approved live sidecar is a separate transcode path.
2. **The static FFmpeg cannot load VAAPI drivers.** `mwader/static-ffmpeg:9.0.2-1` is a fully static build; upstream says hardware acceleration is not supported because a static binary cannot load system drivers ([static-ffmpeg §Hardware acceleration][static]). Empirically, its `-hwaccels` lists only `qsv`, never `vaapi`; `-init_hw_device vaapi` fails. See §3.
3. **A VAAPI-capable image is a product and support change, not a flag.** It needs a dynamically linked FFmpeg, `libva` plus Intel or Mesa drivers, `vainfo`, `/dev/dri` passthrough, non-root device/group handling, and a per-host kernel/driver matrix. Ubuntu 24.04's FFmpeg is 6.1.1, three major release series older than the pinned 9.0.2 (6.x vs 9.x), and its Intel VAAPI driver is amd64/i386 only while the app publishes `linux/amd64,linux/arm64`. The measured amd64 install of `ffmpeg` + `intel-media-va-driver` (free-kernel) + `vainfo` adds 434 MB; replacing the two static binaries (288 MB together) makes the net increase about 146 MB. The owner has now approved a live transcode sidecar, so this cost is accepted for that path; the reason to keep VAAPI out of the recorder is reliability, not the absence of a feature.

**What would change the decision:** the owner has approved live transcoding for playback and storage; the companion memo records the GO and the prototype gate. A representative-hardware benchmark still decides the live encoder settings, and the separate optional image or worker keeps the default recorder on static FFmpeg and `-c copy`.

## 2. Current pipeline: no decode or encode to accelerate

| Path | What runs | Decode? | Encode? | VAAPI target |
|---|---|---|---|---|
| Recorder | Streamlink `--stdout` → bounded pipe → FFmpeg `pipe:0` → `-c copy` → fMP4 HLS | No | No | **None** |
| Export | FFmpeg reads the stitched HLS index → `-map 0 -c copy` → MP4 `+faststart` | No | No | **None** |
| Mock/test fixture | `MockMedia` generates a 320×180 clip with `libx264` from a lavfi source in Mock mode only | No (generated source) | Yes | Not production; not a reason |
| Thumbnails | No production generator found; Mock writes `thumb.jpg` from a single lavfi frame | One frame | JPEG | Trivial; not a GPU-stack reason |

The recorder contract specifies two independent CliWrap processes per part, Streamlink `--stdout` and FFmpeg `pipe:0`, bridged by a bounded pipe (`docs/recorder-contract.md:13-16`). The FFmpeg argument list maps video and optional audio, then selects the copy encoder (`FfmpegArgs.cs:20-23`). `-bsf:a aac_adtstoasc` is a bitstream filter for MPEG-TS/AAC, not a decode or encode (`FfmpegArgs.cs:23`; contract §4). The HLS muxer writes fMP4 segments; there is no scaling or filtering (`FfmpegArgs.cs:24-32`).

Export builds `-map 0 -c copy` and only adds a metadata tag for HEVC and `+faststart` for the MP4 layout (`ExportService.cs:434-443`). `+faststart` rewrites the file to move the `moov` atom; it does not touch pixels. Before queueing, `ExportPartProbe` verifies that every part has the same codec, profile, size, and pixel format; if they differ, the export is refused rather than transcoded (`ExportPartProbe.cs:14-16`, `ExportService.cs:280-286`).

`MockMedia` is the only production-source file that uses an encoder, and it is test-only: it generates a 320×180 `libx264` clip for the Mock catalog (`src/StreamKeepr/Mock/MockMedia.cs:33-64`). It is not on the recorder or export path. Playback decode happens in the viewer's browser, not in the container, so server-side VAAPI does not help there either.

### Streamlink

Streamlink is "a command-line utility that extracts streams from various services and pipes them into a video player of choice" ([Streamlink CLI][streamlink-cli]). `--stdout` "write[s] stream data to `stdout`" ([`--stdout`][streamlink-stdout]). It downloads HLS segments and writes the bytes; the CLI exposes no decode or hardware-acceleration option.

A separate container that runs Streamlink would not change this: Streamlink still extracts and downloads, and a player, not Streamlink, owns any hardware decode. Stream Keepr attaches FFmpeg `pipe:0`, not a player.

The CLI reference contains no `vaapi`, `hwaccel`, or GPU option; a text search for those terms in the 8.6.3 CLI page returns no matches. The only transcoding-adjacent options are `--ffmpeg-video-transcode` and `--ffmpeg-audio-transcode`, and both say "This only applies to streams which require muxing"; the default is `copy` ([Streamlink CLI][streamlink-cli]). For Twitch HLS, Streamlink's job is extraction/download, not decode.

### Why `-c copy` is CPU-light

FFmpeg's own description is explicit: `-c copy` "selects the `copy` encoder, i.e. streamcopy with no decoding or encoding"; "since there is no decoding or encoding, it is very fast and there is no quality loss"; and "applying filters is obviously also impossible, since filters work on decoded frames" ([FFmpeg streamcopy][ffmpeg-copy]).

| Stage | Copy path | Transcode path |
|---|---|---|
| HTTP/TLS download and HLS parsing | Streamlink (network-bound) | same, plus decode |
| Demux | FFmpeg | FFmpeg |
| Decode | **no** | yes |
| Filter/scale | **no** | yes |
| Encode | **no** | yes |
| Mux and write | FFmpeg (HLS or MP4) | FFmpeg |

The work is therefore O(bytes), not O(pixels): read packets, rewrite container timestamps and indexes, and write them back. Take a 1080p60 stream at 8 Mbps (an illustrative assumption, not a Twitch guarantee): that is about 1 MB/s, or about 15 GB for a four-hour stream. Moving 15 GB through memory and disk is seconds of data movement, not hundreds of seconds of motion estimation. A software H.264 encoder, by contrast, processes 864,000 frames in those four hours and performs motion search, mode decision, transform, and entropy coding on each one. This is a structural bound, not a benchmark: no CPU profile of the copy path exists in the repository. If CPU is actually a problem on a deployment, profile the copy path first; a GPU will not speed up a path that never decodes.

Streamlink's `--stream-segment-threads 2` and `--stream-segment-timeout 20` are download concurrency and network timeouts (`docs/recorder-contract.md:61-62`), not decode settings. The recorder's stall watchdog measures media progress, not CPU (`docs/recorder-contract.md:103-108`).

## 3. The static FFmpeg blocker

### Upstream statement

The `mwader/static-ffmpeg` README has a section titled "Hardware acceleration (VAAPI, QSV, Vulkan and similar) is not supported":

> The binaries are fully static: even the C library is linked in, and they have no dependencies on the system. That is more than what other builds mean by static. There the ffmpeg libraries are linked into the executable, but the C library and the loader still come from the system, so such a build can load drivers.
>
> Hardware acceleration needs a driver that belongs to the system and is loaded when ffmpeg runs. A fully static binary cannot load anything, so the two do not go together. This does not depend on the GPU or its vendor.

Source: [static-ffmpeg README][static], fetched 2026-11-13. The same section says ffmpeg still runs on the hardware, but encodes and decodes in software, and recommends a dynamically linked distribution build for hardware acceleration.

### Empirical check

Commands were run on 2026-11-13 with Docker 29.9.3. The image digest was `sha256:f5d100184884eb3e82b0eb14bb5ebf98da99826ac824989720297fb9ef21c822`; the FFmpeg results in this table were identical on arm64 and amd64. Both init checks used the same `-f lavfi -i color=black:s=64x64:d=0.1 -frames:v 1 -f null -` pipeline.

| Command | Exact result |
|---|---|
| `docker run --rm mwader/static-ffmpeg:9.0.2-1 -hide_banner -hwaccels` | `Hardware acceleration methods:`<br>`qsv` |
| `docker run --rm mwader/static-ffmpeg:9.0.2-1 -hide_banner -version` | `ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers`<br>`built with gcc 15.2.0 (Alpine 15.2.0)`<br>`configuration: ... --enable-libvpl ...` (trimmed for width; the full line lists `--enable-libvpl` and no `--enable-vaapi`) |
| `docker run --rm --entrypoint=/ffmpeg mwader/static-ffmpeg:9.0.2-1 -encoders \| grep -i -E 'vaapi\|qsv'` | No VAAPI line. The qsv encoders visible are `h264_qsv`, `hevc_qsv`, `av1_qsv`, `mjpeg_qsv`, `mpeg2_qsv`, and `vp9_qsv`. |
| `... -init_hw_device vaapi=va -f lavfi -i color=black:s=64x64:d=0.1 -frames:v 1 -f null -` | `Device creation failed: -12.`<br>`Failed to set value 'vaapi=va' for option 'init_hw_device': Out of memory`<br>`Error parsing global options: Out of memory` (exit 244) |
| `... -init_hw_device qsv=hw ...` | `[QSV @ ...] No supported child device type is enabled`<br>`Device creation failed: -38.`<br>`Failed to set value 'qsv=hw' for option 'init_hw_device': Function not implemented` (exit 218) |
| `docker run --rm --device /dev/dri:/dev/dri alpine ls /dev/dri` | `docker: Error response from daemon: error gathering device information while adding custom device "/dev/dri": no such file or directory` |

Interpretation:

- **`-hwaccels` reports build-time components, not working hardware.** FFmpeg documents it as "list all hardware acceleration components enabled in this build"; "actual runtime availability depends on the hardware and its suitable driver being installed" ([FFmpeg `-hwaccels`][ffmpeg-generic]). The static build enables `libvpl` (QSV) but not VAAPI, and the upstream README says its static binaries cannot load the drivers anyway.
- **QSV is not a workaround.** The static build lists QSV encoders, but QSV device creation fails here with "No supported child device type is enabled"; upstream's blanket statement covers QSV as well. A QSV path would need the same dynamic-build and `/dev/dri` work as VAAPI, so it does not change this decision.
- **VAAPI init fails without initializing a device.** The exact error is FFmpeg's generic device-creation failure; the important fact is that the shipped binary cannot initialize VAAPI, regardless of whether a GPU exists.

### Replacement options

| Option | FFmpeg version | VAAPI | Size / maintenance | Assessment |
|---|---|---|---|---|
| Ubuntu 24.04 `ffmpeg` package | 6.1.1-3ubuntu5 | Yes; `-hwaccels` lists `vaapi` (verified amd64) | Measured +434 MB installed for `ffmpeg` + `intel-media-va-driver` (free-kernel) + `vainfo` (about +146 MB net after removing the 288 MB static binaries); distro security updates; three major release series older than 9.0.2 (6.x vs 9.x) | Works, but a product-wide FFmpeg downgrade and a larger image |
| Custom dynamic FFmpeg 9.0.2 + libva | 9.0.2 | Yes, if built with `--enable-vaapi` | New build stage, shared libraries, multi-arch build, CVE tracking; size not measured | Keeps version; highest maintenance |
| Separate VAAPI worker image | 9.0.2 or distro | Yes | Second image/profile; IPC or shared volume; default image unchanged | Best shape **if** a transcode feature exists |
| Keep static FFmpeg | 9.0.2 | No | Current image and current zero-host-coupling | Correct for copy-only recording |

For contrast, the Ubuntu 24.04 amd64 package's `-hwaccels` returned `vdpau`, `cuda`, `vaapi`, `qsv`, `drm`, `opencl`, and `vulkan`; the static build returned only `qsv`.

Codec set: both builds include x264, x265, and dav1d, and both handle the common HLS codecs. The static build additionally carries the FFmpeg 9 codec/filter set and `libvmaf`; Ubuntu's 6.1.1 package is distro-curated. Check the exact set on the chosen build with `ffmpeg -codecs` and `ffmpeg -filters`; do not assume feature parity.

The Dockerfile comment says static FFmpeg is "far smaller than apt's ffmpeg and its dependency tree" (`Dockerfile:32-34`). The margin is smaller than that comment suggests: the two static binaries are 137 MiB (144 MB) each, or 288 MB together, while the measured Ubuntu FFmpeg + Intel driver + `vainfo` install adds 434 MB because it pulls the FFmpeg shared libraries, SDL2, and their dependencies; replacing the static binaries makes the net increase about 146 MB. The static build is still smaller, but the stronger reasons to keep it are the pinned FFmpeg 9.0.2 and no host driver coupling, not raw size.

## 4. What a VAAPI-capable image would require

| Layer | Requirement | Evidence / notes |
|---|---|---|
| FFmpeg | Dynamically linked build with VAAPI enabled | Static README; static `-version` has no `--enable-vaapi`; Ubuntu 6.1.1 lists `vaapi` |
| libva | `libva2`, `libva-drm2` (pulled by FFmpeg/driver) | [libva][libva] implements VA-API with vendor backends |
| Intel driver | `intel-media-va-driver` (iHD) for Gen8+; `i965-va-driver` for older GPUs | [Intel media driver][intel-media]; Ubuntu package is amd64/i386 only |
| Intel full-feature | `intel-media-va-driver-non-free` (multiverse) for the full-feature build | Intel README documents free-kernel vs full-feature builds; Ubuntu nf package is 35.3 MB installed vs 13.3 MB for the free build |
| AMD driver | `mesa-va-drivers` (radeonsi) | FFmpeg VAAPI wiki: Mesa uses UVD/VCE; H.264 encode on GCN, other encode codecs limited |
| Diagnostics | `vainfo` | [libva-utils][libva-utils]: "run `vainfo` to check your media stack environment is correct" |
| Kernel/device | Linux kernel with i915 or amdgpu and a DRM render node, usually `/dev/dri/renderD128` | FFmpeg wiki: use a DRM render node, not an X display |
| Container | `devices: [/dev/dri/renderD128:/dev/dri/renderD128:rw]` | Compose `devices` is `HOST_PATH:CONTAINER_PATH[:CGROUP_PERMISSIONS]` ([Compose][compose-devices]) |
| Non-root access | Add the host `render` GID if the node is group-only; use a numeric GID | Container runs as UID 1654 (`Dockerfile:39-46`); device cgroup access and Unix mode are separate |
| Device cgroup | `devices` adds the allow rule; do not use `privileged` | Docker: containers have no device access by default; `--device` grants specific devices, `rwm` by default ([Docker run][docker-devices]) |
| Host | Linux only for VAAPI; Docker Desktop macOS/Windows and arm64 SBCs are not a supported path | See §6 |

Neither `deploy/compose.yaml` (service at `:16-89`) nor `compose.yaml` (`:4-33`) declares `devices`, `group_add`, or `device_cgroup_rules` today. `README.md:88` describes the image as one non-root service with Streamlink and static FFmpeg/ffprobe and documents no GPU requirement. The fragment below would be new configuration.

Illustrative Compose fragment — **not applied in this spike**:

```yaml
services:
  stream-keepr:
    devices:
      - /dev/dri/renderD128:/dev/dri/renderD128:rw
    # Only if the render node is group-only (for example 0660 root:render):
    group_add:
      - "109"   # host render GID; read it with: stat -c '%g' /dev/dri/renderD128
```

Use the numeric GID, not the name `render`: the host and the container image can assign different GIDs to that name. Pass only the render node, not all of `/dev/dri`; `card0` is for display and is not needed. `rw` is enough; `mknod` is not. Never use `privileged: true` for this.

### Size and maintenance

Measured on 2026-11-13 in `mcr.microsoft.com/dotnet/aspnet:10.0.14` (Ubuntu 24.04.5, amd64 under emulation):

```text
apt-get install --no-install-recommends ffmpeg intel-media-va-driver vainfo
Need to get 126 MB of archives.
After this operation, 434 MB of additional disk space will be used.
```

That is the free-kernel Intel driver. The full-feature `intel-media-va-driver-non-free` is another ~22 MB installed. A custom minimal FFmpeg build could be smaller, but it adds a build pipeline and per-release maintenance; its size was not measured.

The multi-arch problem is structural:

- `intel-media-va-driver` and `intel-media-va-driver-non-free` are published for **amd64 and i386 only** ([Ubuntu package][ubuntu-intel], [non-free package][ubuntu-intel-nf]).
- Stream Keepr publishes `linux/amd64,linux/arm64` (`.github/workflows/publish.yml:65`). An unconditional `apt-get install intel-media-va-driver` breaks the arm64 build.
- Options are an architecture-conditional install (the arm64 image then has no VAAPI) or a separate amd64-only VAAPI image. The latter is cleaner if a transcode feature ever exists; it is overkill for a copy-only recorder.

The FFmpeg version gap is the other maintenance cost: Ubuntu 24.04 ships 6.1.1, while the static pin is 9.0.2. The recorder handles HLS, fMP4, HEVC, AV1, and MPEG-TS/AAC (`docs/recorder-contract.md:33-70`); changing the FFmpeg major version under it requires a full recorder/export regression run. A custom dynamic build of 9.0.2 avoids the downgrade but puts the build, security updates, and multi-arch matrix on this project.

## 5. FFmpeg command shape and limits

### What the options do

| Option | What it does | Why it does nothing with `-c copy` |
|---|---|---|
| `-hwaccel vaapi` | Use VAAPI to **decode** the matching stream(s) | There is no decoder in the copy pipeline |
| `-hwaccel_device /dev/dri/renderD128` | Select the render node for decode | No device is opened |
| `-hwaccel_output_format vaapi` | Keep decoded frames in GPU memory instead of downloading them to system memory | There are no decoded frames |
| `hwupload` | Upload software frames to GPU surfaces; needs a device from `-filter_hw_device` or `-vaapi_device` | There is no filter graph |
| `scale_vaapi` | Resize on the GPU | Filters are impossible in streamcopy |
| `h264_vaapi` / `hevc_vaapi` | Hardware encoders; they accept only VAAPI surfaces | `-c copy` selects the `copy` encoder, not an encoder |
| `-c copy` | Streamcopy: demux and mux without decode, filter, or encode | It bypasses every hardware option above |

Sources: FFmpeg `-hwaccel` and `-hwaccels` ([FFmpeg options][ffmpeg-hwaccel], [generic options][ffmpeg-generic]); VAAPI encoder documentation ([FFmpeg VAAPI encoders][ffmpeg-codecs]); VAAPI wiki ([FFmpeg VAAPI wiki][vaapi-wiki]). `-hwaccel_output_format` is listed by `ffmpeg -h full` as "select output format used with HW accelerated decoding"; the current HTML reference does not define it, but the wiki explains the download behavior.

### Command shape (for a future transcode, not current code)

Zero-copy decode and encode:

```bash
ffmpeg -hwaccel vaapi -hwaccel_device /dev/dri/renderD128 \
  -hwaccel_output_format vaapi -i input.mp4 \
  -vf 'scale_vaapi=w=1280:h=720' \
  -c:v h264_vaapi -b:v 4M -maxrate 5M -bufsize 10M \
  -c:a copy output.mp4
```

Software decode, hardware encode (the common case when the source codec is not hardware-decodable):

```bash
ffmpeg -vaapi_device /dev/dri/renderD128 -i input.mp4 \
  -vf 'format=nv12,hwupload' \
  -c:v h264_vaapi -qp 20 -c:a copy output.mp4
```

`scale_vaapi` is present in FFmpeg's source (`libavfilter/vf_scale_vaapi.c`) and in the VAAPI wiki, but it is missing from the current HTML filter reference. Check the chosen build with `ffmpeg -h filter=scale_vaapi`; if it is absent, a software `scale` plus `hwupload`/`hwdownload` costs a GPU-to-CPU round trip.

### Limits

| Limit | What the documentation says | Practical effect |
|---|---|---|
| Quality mode | "No CRF-like mode is currently supported. The only constant-quality mode is CQP." CBR/VBR are supported; driver behavior varies. | Hardware quality control is coarser than x264 CRF; VBR quality varies with scene complexity |
| Preset | "There is no complete analogue of the `-preset` option." `-compression_level` is a small device-specific integer (not more than 7); higher is faster/worse. | You cannot ask for x264 `slow`-equivalent quality; tuning is per GPU |
| Two-pass / lookahead | "Neither two-pass encoding nor lookahead are supported at all — only local rate control is possible." | Offline encodes cannot spend extra passes to hit a quality target |
| B-frames | `bf` and `b_depth` are exposed, but driver support varies; `vp8_vaapi` explicitly says "B-frames are not supported." | Some GPUs/profiles cannot use B-frames; verify on the target |
| Bit depth | 8-bit is the common path; 10-bit HEVC requires `p010` upload and a supported GPU. The wiki's example says "Kaby Lake or later Intel" for 10-bit HEVC. | 10-bit sources may need software or a newer GPU |
| Codec/profile | H.264 baseline/main/high; HEVC main/main10; AV1 encode only on newer GPUs. Profile and level are passed through; the driver may reject or clamp. | Older iGPUs have no AV1 encode; HEVC 10-bit on Kaby Lake is shader-based, not the fixed-function path |
| Resolution | Maximum resolution and input format are platform-specific (Intel publishes a per-platform table). | 1440p/4K is not universal; check the GPU generation |
| Pixel format | 4:2:0 8-bit is the safe common denominator; 4:2:2/4:4:4 support is rarer. | Exotic source formats may fall back to software |
| Rate control | `rc_mode` supports CQP, CBR, VBR, ICQ, QVBR, and AVBR, but "a given driver may only support a subset." | `vainfo` shows profiles/entrypoints, not rate-control modes; benchmark the modes you intend to use |

Sources: [FFmpeg VAAPI encoders][ffmpeg-codecs], [FFmpeg VAAPI wiki][vaapi-wiki].

### Intel generation support

Intel's media-driver matrix marks `D` = decode, `E` = hardware encode, and `Es` = shader-based encode. Ubuntu's default `intel-media-va-driver` is the **free-kernel** build; the full-feature build is `intel-media-va-driver-non-free`. The free-kernel build exposes fewer encode entrypoints on older generations: Broadwell AVC and Skylake/Kaby HEVC 8-bit encode are shader-based and require the full-feature package; Kaby HEVC 10-bit is hardware encode. Verify with `vainfo` and a test encode, not the table alone.

| Generation | H.264 encode | HEVC 8-bit encode | HEVC 10-bit encode | AV1 encode |
|---|---|---|---|---|
| Broadwell (Gen8) | Full-feature only (shader) | No | No | No |
| Skylake (Gen9) | Yes | Full-feature only (shader) | No | No |
| Kaby/Coffee Lake (Gen9.5) | Yes | Full-feature only (shader) | Yes (hardware) | No |
| Ice Lake/Tiger Lake (Gen11/12) | Yes | Yes | Yes | No |
| DG2/Alchemist and newer | Yes | Yes | Yes | Yes |

Source: [Intel media driver supported platforms and features][intel-media]. The matrix also lists newer platforms (Meteor Lake, Lunar Lake, Battlemage, etc.) with AV1 encode; the table above stops at the generations a typical Stream Keepr host is likely to have. The driver matrix is not an FFmpeg guarantee; the installed driver version, kernel, and `vainfo` output still decide.

**AMD.** VAAPI on AMD goes through Mesa's `radeonsi` driver. The FFmpeg wiki (last modified 2024-01-28) says H.264 encode works on GCN but is "still incomplete", and "no other codecs are supported by Mesa for encode yet." That page may be stale for newest Mesa/VCN hardware, so treat AMD HEVC/AV1 encode as **UNKNOWN** and verify with `vainfo` plus a real encode. AMD is a weaker target than Intel for this use case.

**NVIDIA.** NVIDIA does not use VAAPI. FFmpeg uses CUDA/NVDEC/NVENC (`-hwaccel cuda`, `h264_nvenc`, `hevc_nvenc`) ([FFmpeg options][ffmpeg-hwaccel]) and Docker needs the NVIDIA Container Toolkit and `--gpus` ([NVIDIA Container Toolkit][nvidia-toolkit]). That is a separate integration, out of scope for this spike.

### When software x264/x265 wins

- **Quality per bit at low bitrate.** x264 CRF, psychovisual tuning, and x265 10-bit generally beat hardware encoders for archival or bandwidth-constrained output.
- **Difficult content.** Film grain, noise, heavy motion, and fades expose hardware rate-control weaknesses; software presets and two-pass can spend the bits where they matter.
- **No GPU or no driver.** CI, NAS boxes, VPS hosts, arm64 SBCs, Docker Desktop, and rootless setups may have no usable VAAPI device.
- **Determinism.** A software encode behaves the same on every host; a hardware encode depends on GPU generation, driver version, and kernel.
- **Real-time is not required.** Export and proxy jobs can run slower than real time; hardware's throughput advantage matters most for live transcoding, which Stream Keepr does not do.

Hardware wins when throughput, CPU headroom, or power matters and the codec/profile is supported. For Stream Keepr's recording path, copy wins outright: it is lossless, fastest, and already implemented.

## 6. Host/support matrix and failure modes

| Host | VAAPI possible? | Notes |
|---|---|---|
| Linux + Intel Gen8+ | Yes | Best case: iHD driver, `libva`, `/dev/dri/renderD128`, kernel i915. amd64 only in Ubuntu's package |
| Linux + AMD GCN+ | Yes, but narrower | Mesa `radeonsi`; H.264 encode is the documented path; HEVC/AV1 encode needs verification |
| Linux + NVIDIA | No (VAAPI) | NVENC/CUDA is a separate stack; out of scope |
| Linux arm64 / SBC | Generally no | Intel driver is not packaged for arm64; ARM SoCs typically expose V4L2 M2M, not VAAPI |
| Rootless Docker | **UNKNOWN** | User namespace and device cgroup delegation; Docker's rootless docs do not guarantee `/dev/dri` passthrough. Test `vainfo` on the target |
| Docker Desktop macOS | No | Docker Desktop runs Linux containers in a VM; this host has no `/dev/dri`, and `--device /dev/dri` fails |
| Docker Desktop Windows | NVIDIA/WSL2 only per Docker docs | GPU-PV is documented for NVIDIA; Intel/AMD VAAPI passthrough is not documented. Treat as unsupported |
| Kubernetes / remote Docker | Depends | Needs a device plugin or CDI; out of scope |

Docker Desktop's GPU page says its `--gpus` passthrough is "only available on Windows with the WSL 2 backend" and covers NVIDIA GPU-PV ([Docker Desktop GPU][docker-desktop-gpu]). It does not document Intel/AMD VAAPI passthrough on macOS or Windows. The measured macOS failure is in §3.

### Failure modes and required behavior

| Symptom | Likely cause | What the app must do |
|---|---|---|
| `vainfo`: cannot open `/dev/dri/renderD128` | Device not passed, or no render node | Log once, mark VAAPI unavailable, fall back to copy/software. Never fail a recording |
| `va_openDriver() returns -1` / "unknown libva error" | Missing or wrong driver; `LIBVA_DRIVER_NAME` mismatch | Try the documented driver, then fall back; do not loop |
| `Permission denied` on the render node | Node is group-only and UID 1654 is not in the group | Document `group_add` with the host GID; fall back if still denied |
| `Device creation failed: -12` / `Out of memory` | Static build or unusable driver | This is exactly the current image; do not attempt VAAPI with it |
| GPU hang, process killed, or `EIO` | Kernel/driver bug or unsupported profile | Timeout, kill, retry in software; preserve the original part |
| Output is corrupt or unplayable | Unsupported profile/bit depth/rate control | Probe the output; fall back and log the GPU/driver/profile |
| Works on one host, fails on another | Driver/kernel/GPU generation matrix | Capability probe per host; no global assumption |

**Required behavior if VAAPI is ever added:** probe once at startup (`vainfo` or a tiny `ffmpeg -init_hw_device vaapi` test), cache the result, and treat it as an optional capability. The recorder must remain copy-only and must never depend on a GPU. A future transcode job should fall back to `libx264`/`libx265` when VAAPI is missing, and a missing GPU must never turn into a failed recording. Health should stay live; at most, an optional transcode feature can report degraded readiness.

## 7. If a transcode use case appears: benchmark plan

**Update (2026-11-13):** the owner has approved a live transcode use case for playback and storage. The companion memo is the implementation basis. This file-based plan remains the quality/throughput reference; the live prototype there is the gate.

The plausible use cases, in order of product value:

1. **Browser/export compatibility.** The recorder enables `--twitch-supported-codecs h264,h265,av1` (`docs/recorder-contract.md:65`), and the contract accepts that HEVC/AV1 "may produce codecs not supported by every browser" (`docs/recorder-contract.md:158`). A one-time H.264/AAC derivative would address playback without touching the original parts.
2. **Lower-bitrate proxies** for mobile or remote playback.
3. **Thumbnails** from a video frame. A single-frame decode is cheap relative to video transcoding and does not justify a GPU stack by itself.
4. **Archival re-encode** to a smaller codec. This is lossy and changes the "keep the source" property; it needs a separate product decision.

The live playback/storage use case is now approved (2026-11-13); the companion memo is the implementation basis. If a different use case is proposed, benchmark before choosing VAAPI. A concrete plan:

| Item | Specification |
|---|---|
| Input | A real recorded part from a representative channel, 1080p60 H.264, 10 minutes; repeat with an HEVC or AV1 part if available. If no real part exists, use a synthetic `testsrc2` clip and label it synthetic |
| Host | Intel Gen9+ Linux host with iHD, `/dev/dri/renderD128`; record `uname -r`, GPU model, `vainfo`, `ffmpeg -version`, driver version |
| Variants | (A) current `-c copy` to MP4; (B) `libx264 -preset veryfast -crf 20`; (C) `libx264 -preset medium -crf 18`; (D) `h264_vaapi -rc_mode CQP -qp 20`; (E) `h264_vaapi -b:v 6M -maxrate 6M -bufsize 12M`; optional `hevc_vaapi` and `libx265` |
| Metrics | Wall time; user+sys CPU seconds; peak RSS; GPU busy (`intel_gpu_top` from `intel-gpu-tools`); output bytes/bitrate; VMAF and SSIM against the source; frame count, A/V sync, and playable output |
| Method | Same input, same duration, no network, warm page cache; 3 runs per variant, report median; use `/usr/bin/time -v` and `-benchmark`; verify with `ffprobe`; compute VMAF with the static FFmpeg's `libvmaf` |
| Quality gate | VAAPI must be at least 2× faster in wall time at VMAF within 1 point of `libx264 veryfast`, or meet a hard real-time/CPU budget that software cannot meet |
| Edge cases | 8-bit and 10-bit; 1080p60 and, only if the GPU claims support, 1440p/4K; high-motion and film-grain scenes; a source whose codec is not hardware-decodable |

`-c copy` is the control, not a candidate: it will always be fastest and lossless. The benchmark exists to decide whether a transcode feature should use VAAPI or software, not whether to transcode copy-only recordings.

## 8. Decision

**NO-GO for the copy-only recorder.** Do not add VAAPI, `libva`, GPU drivers, `/dev/dri`, or device configuration to the source recorder. The approved live sidecar is covered by [`live-transcode-vaapi.md`](live-transcode-vaapi.md).

| Question | Answer |
|---|---|
| Is there a decode/encode workload in the recorder? | **No.** Recorder and export are `-c copy`; Streamlink writes HLS bytes |
| Can the current image do VAAPI? | **No.** Static FFmpeg has no `vaapi` hwaccel and cannot load drivers; `-init_hw_device vaapi` fails |
| Is a VAAPI image cheap? | **No.** Dynamic FFmpeg 6.1.1 (or a custom 9.0.2 build), libva + driver + `vainfo`, +434 MB measured, amd64-only Intel driver, host matrix |
| Is there an approved transcode feature? | **Yes, as of 2026-11-13:** live H.264 for playback and storage. It belongs in the sidecar, not the recorder |
| Recommendation | **No-go for the recorder; GO for the live sidecar in the companion memo** |

**Blocking constraint for the recorder:** the copy path has no decode/encode stage, and the current static FFmpeg cannot load VAAPI. The live sidecar addresses that with a separate image and leaves the recorder untouched.

### What would change the decision

The owner has now approved the live playback/storage use case, so the product condition is met. The remaining gates for the live sidecar are the prototype in the companion memo and a hardware benchmark.

- A benchmark on representative hardware showing a real constraint: VAAPI at least 2× faster at equal VMAF, or meeting a hard real-time/CPU budget.
- A separate optional image or worker that keeps the default image on static FFmpeg 9.0.2 and `-c copy`, with no change to the recorder contract.
- Evidence that the target user base has usable Intel/AMD GPUs and `/dev/dri`; today this is unknown.

### If a future recorder-path go is approved: smallest first slice

**Superseded for the live requirement.** The owner approved live transcode; see [`live-transcode-vaapi.md`](live-transcode-vaapi.md) §7. This export-only slice remains the fallback if the live-playlist prototype fails.

- Add an **export-only** transcode job behind an off-by-default flag. Do not touch the recorder, the part format, or the recorder contract.
- Add a `VaapiProbe` that runs once at startup (`vainfo` or a tiny `ffmpeg -init_hw_device vaapi` check) and a `TranscodeService` that uses a dynamic FFmpeg path, `h264_vaapi`, and `libx264` fallback. Write to a temp file and rename atomically; never modify the original parts.
- Keep the default image on static FFmpeg. Ship the VAAPI stack as a separate amd64-only image or an optional Compose profile; do not make `intel-media-va-driver` an unconditional dependency. `RecorderOptions.FfmpegPath` (`src/StreamKeepr/Configuration/RecorderOptions.cs:11`) is already configurable, so a future transcode job can take its own path option without changing the recorder.
- Affected files (hypothetical): `src/StreamKeepr/Infrastructure/Storage/ExportService.cs` or a new `src/StreamKeepr/Infrastructure/Media/TranscodeService.cs`; new `src/StreamKeepr/Configuration/TranscodeOptions.cs`; `src/StreamKeepr/Program.cs` for DI; `Dockerfile` for a separate stage; `deploy/compose.yaml` for a commented VAAPI fragment; export/transcode docs; tests under `tests/StreamKeepr.Tests/`.
- Test seams: an injectable process runner or fake `ffmpeg` script; an injectable VAAPI probe; the existing export queue/probe tests; a fixture with a known HEVC/AV1 part. Test the fallback path by making the probe return false and by making `ffmpeg` fail with a VAAPI error.
- Rollback: turn the flag off and delete derivatives. Original parts and the default image are untouched, so rollback is data-safe.

Do **not** put VAAPI in the recorder path. The recorder's job is to preserve the source with `-c copy`; a GPU dependency there would add a new failure mode to the one path that must never fail.

## 9. UNKNOWN items

| # | Unknown | Why it matters | Resolution |
|---|---|---|---|
| U1 | What share of Stream Keepr hosts have Intel/AMD GPUs and a usable `/dev/dri` | The feature may help only a minority; no telemetry is documented in the repo | Opt-in diagnostics or a user survey; ask for `vainfo` output |
| U2 | Whether rootless Docker can pass `/dev/dri` on target distros | Rootless Docker is a documented Docker mode; device access is not documented | Run `docker run --device /dev/dri ... vainfo` on a rootless host |
| U3 | Real VAAPI quality/throughput on the target GPU | The whole go/no-go for a transcode feature depends on it | Run the §7 benchmark on real hardware |
| U4 | How many recordings are HEVC/AV1 and actually fail to play | This is the main product justification | Probe the library's codecs and collect browser playback errors |
| U5 | Whether Docker Desktop Windows can pass an Intel/AMD GPU for VAAPI | Docker documents NVIDIA/WSL2 only; users may ask | Test on Windows/WSL2 with `vainfo`; likely no |
| U6 | Size/build time of a custom dynamic FFmpeg 9.0.2 image | The 434 MB figure is Ubuntu's package, not a minimal build | Build a prototype image and measure; time-boxed |
| U7 | Whether the free-kernel Intel driver exposes the needed encode profiles | Full-feature vs free-kernel builds differ | Run `vainfo` and a test encode with both packages |
| U8 | Whether an approved transcode feature will exist | Without it, VAAPI is dead code | Product decision; do not build ahead of it |

## 10. Sources

All fetched on 2026-11-13 unless noted. The FFmpeg Trac wiki was fetched through the Wayback Machine because the live site is behind Anubis; the snapshot is 2026-01-22 and the page says it was last modified 2024-01-28.

- Issue #144: https://github.com/brandtkstrom/StreamKeepr/issues/144
- Streamlink CLI reference: https://streamlink.github.io/cli.html
- Streamlink `--stdout`: https://streamlink.github.io/cli.html#cmdoption-stdout
- Streamlink Twitch plugin: https://streamlink.github.io/cli/plugins/twitch.html
- static-ffmpeg README, hardware acceleration section: https://github.com/wader/static-ffmpeg#hardware-acceleration-vaapi-qsv-vulkan-and-similar-is-not-supported
- FFmpeg streamcopy: https://ffmpeg.org/ffmpeg.html#Streamcopy
- FFmpeg advanced video options (`-hwaccel`, `-hwaccel_device`): https://ffmpeg.org/ffmpeg.html#Advanced-Video-options
- FFmpeg generic options (`-hwaccels`): https://ffmpeg.org/ffmpeg.html#Generic-options
- FFmpeg VAAPI encoders: https://ffmpeg.org/ffmpeg-codecs.html#VAAPI-encoders
- FFmpeg `hwupload` filter: https://ffmpeg.org/ffmpeg-filters.html#hwupload
- FFmpeg `scale_vaapi` source: https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vf_scale_vaapi.c
- FFmpeg VAAPI wiki (live): https://trac.ffmpeg.org/wiki/Hardware/VAAPI
- FFmpeg VAAPI wiki (Wayback snapshot used): https://web.archive.org/web/20260122001034/https://trac.ffmpeg.org/wiki/Hardware/VAAPI
- Docker Compose `devices`: https://docs.docker.com/reference/compose-file/services/#devices
- Docker Compose `group_add`: https://docs.docker.com/reference/compose-file/services/#group_add
- Docker Compose `device_cgroup_rules`: https://docs.docker.com/reference/compose-file/services/#device_cgroup_rules
- Docker run device access: https://docs.docker.com/engine/containers/run/#runtime-privilege-and-linux-capabilities
- Docker rootless mode: https://docs.docker.com/engine/security/rootless/
- Docker Desktop GPU support: https://docs.docker.com/desktop/features/gpu/
- Intel media driver: https://github.com/intel/media-driver
- Intel media driver supported platforms: https://github.com/intel/media-driver#supported-platforms
- libva: https://github.com/intel/libva
- libva-utils (`vainfo`): https://github.com/intel/libva-utils
- Ubuntu 24.04 `ffmpeg` package: https://packages.ubuntu.com/noble/ffmpeg
- Ubuntu 24.04 `intel-media-va-driver`: https://packages.ubuntu.com/noble/intel-media-va-driver
- Ubuntu 24.04 `intel-media-va-driver-non-free`: https://packages.ubuntu.com/noble/intel-media-va-driver-non-free
- Ubuntu 24.04 `vainfo`: https://packages.ubuntu.com/noble/vainfo
- NVIDIA Container Toolkit (separate NVENC path): https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html

[static]: https://github.com/wader/static-ffmpeg#hardware-acceleration-vaapi-qsv-vulkan-and-similar-is-not-supported
[streamlink-cli]: https://streamlink.github.io/cli.html
[streamlink-stdout]: https://streamlink.github.io/cli.html#cmdoption-stdout
[ffmpeg-copy]: https://ffmpeg.org/ffmpeg.html#Streamcopy
[ffmpeg-hwaccel]: https://ffmpeg.org/ffmpeg.html#Advanced-Video-options
[ffmpeg-generic]: https://ffmpeg.org/ffmpeg.html#Generic-options
[ffmpeg-codecs]: https://ffmpeg.org/ffmpeg-codecs.html#VAAPI-encoders
[vaapi-wiki]: https://trac.ffmpeg.org/wiki/Hardware/VAAPI
[compose-devices]: https://docs.docker.com/reference/compose-file/services/#devices
[docker-devices]: https://docs.docker.com/engine/containers/run/#runtime-privilege-and-linux-capabilities
[docker-desktop-gpu]: https://docs.docker.com/desktop/features/gpu/
[intel-media]: https://github.com/intel/media-driver
[libva]: https://github.com/intel/libva
[libva-utils]: https://github.com/intel/libva-utils
[ubuntu-intel]: https://packages.ubuntu.com/noble/intel-media-va-driver
[ubuntu-intel-nf]: https://packages.ubuntu.com/noble/intel-media-va-driver-non-free
[nvidia-toolkit]: https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html
