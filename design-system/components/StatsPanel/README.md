"Stats for nerds": a compact card laid over the top-left of the video, inside the player, with playback (video) and network figures and the viewer count at the playhead. Because it lives in the player it shows in theater mode and in fullscreen, where a panel under the player never could. The viewer chart is too big to lay over the video, so it moved to its own panel under Chapters (`Viewers`).

## Build it from

Keep `StatsPanel.razor.js` owning the values, the sparklines and the open state, but render its markup **inside the player's frame** (the element that goes fullscreen), after the title strip and before the control bar. It stays a JS-owned island; no MudBlazor components inside it. The viewer-chart wiring (`viewer-chart.js`, the `/viewers` fetch) moves to `Viewers`; the overlay only reads the count at the playhead from the same samples.

- **Opening it:** the player's existing stats button (`QueryStats`, pressed = `player-accent`) and the `I` shortcut toggle it, and so does the card's own close button. The open state is remembered per viewer in `UiPreferences`, as the old switch was. The panel's switch-styled header goes away.
- **Card:** 340px wide (at most the player's width minus `space-3` each side), on `player-surface` at 88% with a light blur, `radius-md`, `elevation-8`, text in `player-text`. Its height is capped above the control bar, and its body scrolls.
- **Header** (40px): "Stats for nerds" in `subtitle2`, then two 32px player buttons: `ContentCopy` ("Copy stats", the same v1 snapshot as before, viewers included) and `Close`.
- **Sections**, each headed in `overline` (`text-secondary`) after an 8px swatch in the series colour:
  - **Video** (`primary`): Bitrate and Frame rate as `stat-value` figures with a 20px sparkline under each, then Resolution, Codecs, Dropped frames and Decoder as key/value rows.
  - **Network** (`secondary`): Bandwidth and Buffer with sparklines, then Last segment, Segments (count and errors) and Playlist (ellipsised; full URL in its `title`).
  - **Stream** (`tertiary`): one row, "Viewers at 54:40 · 1,489 · peak 1,658". Left out when the recording has no viewer samples.
- Key/value rows: keys in `caption` `text-secondary`, values in `stat-value`, tabular figures.

## Placement

- Top-left, `space-3` in from the frame. While the player's overlays show, the card sits between the title strip and the seek bar; when they hide after 3 s idle, it slides up to `space-3` from the top and may grow down to `space-3` from the bottom (200 ms, no motion under reduced motion). It never hides with the overlays: it stays until closed.
- It sits above the video and below the menus (speed), so an open menu draws over it.
- Clicks on the card never reach the video, so they don't play or pause it.
- In a player narrower than 560px (a small window with chat open, phone landscape in fullscreen) the card takes half the width, at least 220px, and drops its sparklines.

## Rules

- Video sparklines are `primary`, network sparklines `secondary`, the stream row's swatch `tertiary`. Labels never take the series colour.
- Each sparkline keeps its visually hidden sample table for screen readers. The card is a `region` labelled "Stats for nerds"; values update quietly (no live region), as before.
- Below `Breakpoint.Md` (not fullscreen) the overlay is not used: the **Stats** tab under the player keeps the full layout (Video and Network stacked, then the viewer tile and chart). The player's stats button stays hidden there, as now. In fullscreen on a phone, `I` and the tab's state don't apply; the overlay can be opened from the player only once the deferred `MoreVert` menu exists.
