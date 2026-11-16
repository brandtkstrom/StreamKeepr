"Stats for nerds": a collapsible panel under Recording details with playback (video) and network statistics side by side, and the stream's viewer count over time below them. A JS-owned island.

## Build it from

Keep `StatsPanel.razor` rendering once, and keep `StatsPanel.razor.js` owning values, sparklines, the viewer chart and the open state. Restyle the existing markup to match a MudExpansionPanel:

- **Header** (48px): the `QueryStats` icon, "Stats for nerds" in `h6`, a live hint in `caption` ("48.6 Mb/s · 28.4 s buffer · 1,312 viewers"), and a switch-styled toggle (`role="switch"`), drawn like `MudSwitch Color.Primary`.
- **Video** (`Videocam` in `primary`) and **Network** (`NetworkCheck` in `secondary`), side by side, headed in `subtitle1`. Each has two tiles on `surface-raised` (`radius-md`, `pa-3`): the label in `overline`, the value in `stat-hero` with its unit in `caption`, and a 32px sparkline below. Under the tiles goes a key/value list (`caption` keys, `stat-value` values, URLs ellipsised).
- **Stream** (`Groups` in `tertiary`), full width below a `divider`, with two parts:
  - **A tile** (220px): "Viewers at 54:40" in `overline`, the count at the playhead in `stat-hero`, then Now, Peak (with its time) and Average as key/value rows.
  - **The viewer chart** (on `surface-raised`, `radius-md`, about 120px of plot): viewer count across the recording's whole timeline, so x is recording time (0:00 → the duration, or the live edge while recording), not wall-clock time.
- **Footer**: a text button, "Copy stats". It copies the viewer figures too.

## The viewer chart

- One series in `tertiary`: a 2px line over an 18% `tertiary` area. The y axis has three gridlines (0, half, max) in `divider`, labelled in `caption` with compact numbers (0 · 1k · 2k). The x axis has four or five timecode labels in `caption`.
- The **playhead** is a dashed `primary` line with a knob on the series and a `primary` tag carrying its timecode. It moves with playback, and the tile's "Viewers at …" value follows it.
- While recording, the series ends at the **live edge** with a `rec` dot, and the chart grows to the right as samples arrive.
- Hovering shows a tooltip ("38:00 · 1,658 viewers"). Clicking or dragging seeks the player to that time. The chart is `role="img"` with a one-sentence summary as its label, and it keeps a visually hidden sample table like the sparklines.
- Draw it as SVG in `StatsPanel.razor.js` (or `sparkline.js`), like the sparklines. MudBlazor's `MudChart` would re-render inside the island.
- With no samples (a recording made before sampling existed, or a broadcast with none), the Stream section shows a single `caption` line instead: "No viewer data for this recording."

## Data this needs

StreamKeepr does not record viewer counts today. The chart needs the recorder to sample the stream's viewer count (Twitch Get Streams `viewer_count`) during capture, for example once a minute, and to store the samples with the session as (offset from session start, count). Those samples are the whole input: peak, average and "at the playhead" are derived from them.

## Rules

- Video sparklines are `primary`, network sparklines `secondary`, and the viewer series `tertiary`: the colour of the stream itself, shared with chapters. One series per chart, and labels never take the series colour.
- Each sparkline keeps its visually hidden sample table for screen readers.
- Below `Breakpoint.Md` the panel is the **Stats** tab under the player (`MudTabPanel Icon="@Icons.Material.Rounded.QueryStats"`), without its own header or toggle. Video and Network stack, then their tiles stack. The Stream tile moves above its chart, and the chart keeps the full width.
