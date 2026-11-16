The primary component: a 16:9 HLS player with a title strip and a control bar. It is a JS-owned island.

## Build it from

Keep `VideoPlayer.razor` rendering once, and keep `VideoPlayer.razor.js` owning hls.js and every control. **Do not use MudBlazor components inside it.** Restyle the existing markup:

- Frame: `player-background`, `radius-lg`, 16:9, **as wide as its column**, with its height capped at the viewport minus the app bar, the gutter and `player-peek` (desktop). In theater mode it spans the full width of the view with square corners; below `Breakpoint.Md` it is edge to edge, square, and sticky on phones. When the height cap applies, the `<video>` letterboxes (`object-fit: contain`) inside the frame.
- Title strip (top) and control bar (bottom, `control-bar-height` 48px) on a flat `player-scrim`. The title is `subtitle1` in `player-text` with the streamer below in `caption`. A REC `StatusChip` sits on the right while live.
- Seek: a 4px track at 24% `player-text`, buffered range `player-buffered`, played range and 14px knob `player-accent`, chapter ticks in `player-text`, and the live-edge tick in `rec`. The hover time is in `timecode`.
- Controls: 40px round buttons (`player-button`) with inline SVG from `Icons.Material.Rounded.*`: Play/Pause, Replay10, Forward10, volume with a 64px bar, `timecode` "54:40 / 1:02:13", "Live edge" with a `rec` dot (only while behind a live capture), then speed and quality pills, stats (pressed = `player-accent`), PiP, theater and fullscreen. A `Chat` toggle sits beside the stats toggle when the recording has chat, mirroring the `C` shortcut.
- Menus (speed) take the `MudMenu` shape (`radius-md`, `elevation-8`) on `player-surface` with `player-text`. The checked item is marked with `Check` in `player-accent`.

## States

Loading is a spinner in `player-accent`. A live capture without segments shows "Waiting for the first segment…". An error shows the error icon in `error`, the message, a filled Retry and a text "Back to library". The centre play button appears only before the first play.

## Rules

- Overlays fade in over 200ms and hide after 3s idle while playing. Keyboard focus shows them.
- The focus ring inside the player is `player-accent` (today the same magenta as `primary`). Keep the player on its own tokens, so the island never depends on the page theme.
- Never put a gradient behind the controls.
