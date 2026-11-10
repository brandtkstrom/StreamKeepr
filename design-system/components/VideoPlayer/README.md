# VideoPlayer

The primary component: a 16:9 HLS player with an overlay title strip and a control bar.

- Frame: `player-bg` (pure black), `radius-md`, full width of the player column. Never put UI chrome outside the frame that would shrink it.
- Overlays sit on `player-scrim` and fade after 3s idle (`.idle`); keyboard focus or pointer movement brings them back.
- Seek bar: track at 24% white, buffered range `secondary`, played `primary`, knob appears on hover/drag, hover timecode tooltip on `surface-bright`. For captures still being written, the live edge is a `rec` tick and the **Live edge** button jumps to it.
- Control order, left → right: play/pause, back 10, forward 10, volume (slider reveals on hover), timecode (`timecode` style, current / total), Live edge (only while recording) · spacer · speed, quality (HLS variants with bitrates), stats toggle, picture-in-picture, theater, fullscreen.
- The stats toggle (`query_stats`) and the panel's switch are the same state — keep them in sync.
- Keyboard: Space/K play-pause, J/L ±10s, ←/→ ±5s on the seek bar, F fullscreen, T theater, I stats.

**Consumer provides:** the HLS master playlist URL served by the backend, `durationSec`, `isRecording`, saved resume position.
