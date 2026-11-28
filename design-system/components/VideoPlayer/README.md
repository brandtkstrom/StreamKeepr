The primary component: a 16:9 HLS player with a title strip and a control bar. It is a JS-owned island.

## Build it from

Keep `VideoPlayer.razor` rendering once, and keep `VideoPlayer.razor.js` owning hls.js and every control. **Do not use MudBlazor components inside it.** Restyle the existing markup:

- Frame: `player-background`, `radius-lg`, 16:9, **as wide as its column**, with its height capped at the viewport minus the app bar, the top and bottom gutters and `player-peek` (desktop). In theater mode it spans the view up to the open chat drawer's divider, with square corners; below `Breakpoint.Md` it is edge to edge, square, and sticky on phones. When the height cap applies, the `<video>` letterboxes (`object-fit: contain`) inside the frame.
- Title strip (top) and control bar (bottom, `control-bar-height` 48px) on a flat `player-scrim`. The title is `subtitle1` in `player-text`; below it the streamer sits in `caption` and, when the recording has chapters, the chapter at the playhead follows a 14px `tertiary` `Toc` icon. At every width the chapter line shares the streamer's line and both truncate as needed. A REC `StatusChip` sits on the right while live.
- Centre Play/Replay is the paused state's one strong fill: a `player-accent` circle with the glyph in `player-background`.
- Seek: a 28px interaction band holding a 4px track at 24% `player-text`, buffered range `player-buffered`, played range and 14px knob `player-accent`, 12px chapter notches in `tertiary` (the stream's colour, as in Statistics), and a 22px live-edge tick in `rec`. The hover tooltip is two lines on `player-surface`: `timecode` then the chapter title in `caption`/`tertiary`.
- Controls: 40px round buttons (`player-button`) with inline SVG from `Icons.Material.Rounded.*`: Play/Pause, Replay10, Forward10, volume with a 64px bar, `timecode` "54:40 / 1:02:13", **Jump to live** with a `rec` dot (Recording/Reconnecting only; its label becomes “Live” at ≤520px), then the speed pill, stats (pressed = `player-accent`; it opens the Statistics overlay, `StatsPanel`, in the top-left of the frame), PiP, theater and fullscreen. There is no quality control: the recorder captures one source. A `Chat` toggle sits beside the stats toggle when the recording has chat, mirroring the `C` shortcut. The chat pane has no toggle of its own; the drawer's header Close control also closes it, and closing from inside the pane returns focus to this button. Picture-in-picture shows `PictureInPictureAlt`, and `PictureInPicture` while in PiP, because Material Rounded has no "off" glyph.
- Menus (speed) take the `MudMenu` shape (`radius-md`, `elevation-8`) on `player-surface` with `player-text`. The checked item is marked with `Check` in `player-accent`.

## States

Loading is a spinner in `player-accent`. A live capture without segments shows "Waiting for the first segment…". An error shows the error icon in `error`, the message, a filled Retry and a text "Back to library". The centre play button appears only before the first play. *As built in #180 it still returns on every pause (player contract §5), as it did before the restyle.*

## Rules

- Overlays fade in over 200ms and hide after 3s idle while playing. Keyboard focus shows them. The Statistics card is the exception: it stays until closed, and only moves up when the title strip hides.
- Layers, bottom to top: video, title strip and control bar, Statistics, menus.
- The focus ring inside the player is `player-accent` (today the same magenta as `primary`). Keep the player on its own tokens, so the island never depends on the page theme.
- Never put a gradient behind the controls.
