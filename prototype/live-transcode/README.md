# Disposable live-transcode prototype (#147)

**Not production code. Do not merge. Delete after #147.**

This harness tests the premise in `docs/research/live-transcode-vaapi.md`:

- feed a dynamic FFmpeg an app-style stitched live playlist;
- handle a part boundary with `#EXT-X-DISCONTINUITY` and a new `#EXT-X-MAP`;
- produce a playable H.264/AAC fMP4 HLS proxy with an ENDLIST on graceful stop;
- verify the source part files are unchanged;
- probe Intel/AMD VAAPI and NVIDIA NVENC.

## Run

```bash
# Software gate on any host; vendor rows are UNVERIFIED.
./run.sh none

# Intel/AMD Linux host with /dev/dri/renderD128.
./run.sh vaapi

# NVIDIA host with the driver and NVIDIA Container Toolkit.
./run.sh nvidia

# Both device paths exposed; the harness still tests each encoder.
./run.sh all
```

Results are written to `results/summary.md` (gitignored). The harness exits nonzero only when the software gate fails, a probed GPU encoder fails its gate, or the source files changed. A vendor with no device is `UNVERIFIED`, not a failure.

## What it does not do

- It does not run the Stream Keepr app, `SessionStore`, or `LiveIndexEndpoint`.
- The fixture playlist is a Python mirror of `PlaylistStitcher`; production code is unchanged.
- It checks output duration/segments, not precise live-edge latency.
- The image is a disposable Ubuntu 24.04 + FFmpeg 6.1.1 probe, not the production `gpu` image.
- NVIDIA drivers are host-injected by the toolkit; the image only contains FFmpeg/VAAPI packages.

## Files

- `Dockerfile` — Ubuntu 24.04, FFmpeg 6.1.1, VAAPI packages, Python.
- `inside.sh` — fixture generation, encoder probes, live-playlist gate.
- `stitch.py` — disposable part-stitching mirror.
- `run.sh` — host launcher and GPU flag handling.
