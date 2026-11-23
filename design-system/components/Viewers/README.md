"Viewers": the stream's viewer count across the whole recording, as a collapsible panel under Chapters (or under the title block when there are no chapters). It is the Stream section of the old Stats for nerds panel, which became an overlay inside the player (`StatsPanel`); the chart stays on the page because it is too big to lay over the video and its click-to-seek would fight the player's own clicks.

## Build it from

The chart is drawn by `wwwroot/js/viewer-chart.js` and fed from `GET /api/recordings/{id}/viewers` (once, or every 60 s while live), as built in #211. Move that wiring out of `StatsPanel.razor.js` into this panel's own island; nothing about the chart itself changes.

- **Header** (48px, a real disclosure button like `Chapters`): `Groups` in `action-default` (like every panel header), "Viewers" in `h6` (the page's `h2`), a live hint in `caption` ("1,489 at 54:40 · peak 1,658"), then the chevron. It is expanded by default and remembers its open state per viewer in `UiPreferences`, in its own `viewers` slot (the Statistics overlay keeps the `stats` slot).
- **Body**: the 220px tile ("Viewers at 54:40" in `overline`, the count at the playhead in `stat-hero`, then Now, Peak with its time, and Average) beside the chart on `surface-raised`.

## The chart

Unchanged from #211: one `tertiary` series (2px line over an 18% area), three gridlines, compact y labels, four or five timecode x labels, the dashed `primary` playhead with its tag, the `rec` dot at the live edge while recording, a hover tooltip, click or drag to seek, `role="img"` with a one-sentence label and a visually hidden sample table.

## Rules

- **Hidden without samples.** A recording with no viewer samples (made before sampling existed, or a broadcast with none) renders no panel, like `Chapters`; the old "No viewer data for this recording." line goes away.
- The count at the playhead also shows in the Stats overlay's Stream row, so a viewer in fullscreen still sees it.
- Below `Breakpoint.Md` there is no Viewers panel: the chart sits at the bottom of the **Stats** tab, under Video and Network, at full width with the tile above it.
