# StatsPanel

A collapsible "Stats for nerds" panel below the recording details, showing playback (video) and network statistics side by side.

- Toggle with the switch in its header or the player's `query_stats` button; collapsed it is a single 48px row, so the player keeps the space.
- Two columns: **Video** (series colour `primary`) and **Network** (series colour `tertiary`). Each has two stat tiles — label `label-medium`, value `stat-hero` — with a 20-sample sparkline (one series, 2px line, hover crosshair + value tooltip), then a key/value list in `stat-value`.
- Video: bitrate, frame rate, resolution → viewport, codecs, dropped frames, decoder path.
- Network: bandwidth estimate, buffer health, last segment (index · duration · load time), segments loaded, errors and retries, playlist URL (ellipsised, full value in `title`).
- Problems are text in `error` with a ⚠ prefix — never colour alone.
- Values update every 2s; don't animate number changes.

**Consumer provides:** a stats object sampled from the player (hls.js `FRAG_LOADED`, `LEVEL_SWITCHED`, `video.getVideoPlaybackQuality()`), plus the last 20 samples for each sparkline.
