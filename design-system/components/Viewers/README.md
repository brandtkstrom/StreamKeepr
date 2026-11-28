"Viewers": the stream's viewer count across the whole recording, as a collapsible panel under Chapters (or under the title block when there are no chapters). The body is one flat plane: a readout band with the count at the playhead, then a full-width signal. It is the Stream section of the old Stats for nerds panel, which became an overlay inside the player (`StatsPanel`); the chart stays on the page because it is too big to lay over the video and its click-to-seek would fight the player's own clicks.

## Build it from

The chart is drawn by `wwwroot/js/viewer-chart.js` and fed from `GET /api/recordings/{id}/viewers` (once, or every 60 s while live), as built in #211. The panel's own island owns that wiring; nothing about the samples changes. Replace the 220px tile beside a chart card with the readout and signal below.

- **Header** (48px, a real disclosure button like `Chapters`): `Groups` in `tertiary` (the stream's colour), "Viewers" in `h6` (the page's `h2`), the recording's peak in `caption` ("peak 1,658 at 38:00"), then the chevron.
  - The hint is the **collapsed summary**, so it is hidden while the panel is open (`[aria-expanded="true"] .hint { display: none }`); the readout carries the peak. It reports the peak only and does not follow the playhead.
  - It is expanded by default and remembers its open state per viewer in `UiPreferences`, in its own `viewers` slot (the Statistics overlay keeps the `stats` slot).
- **Body** (one flat plane, `space-4` padding; no raised tile or nested cards):
  - **Readout band**: "Viewers at 54:40" in `overline` and the count at the playhead in `stat-hero`, all tabular. Beside it, one `space-8` away, three figures: Now (Last once the recording is complete), Average, and Peak with its time in `caption` ("1,658 at 38:00"). Labels are `overline`, values `stat-value`, all tabular. The hero keeps a 144px column so the figures do not shift as the timecode gains an hour. Below 560px of panel width (a container query, like the player's stats card) the figures take the full width under the hero and spread across it above a `divider` rule.
  - **Signal**: full width below the band, drawn on the panel's `surface`; no separate chart card. The plot is inset 40px on the left for the y labels and 26px below for the x labels.
- **Hooks** (`data-sk`): `viewer-readout` on the readout; `viewer-at` and `viewer-count` in the hero; `viewer-now-label`, `viewer-now`, `viewer-average`, `viewer-peak`, `viewer-peak-at` in the figures; `viewer-chart`, `viewer-svg`, `tip-viewers`, `table-viewers`, `rows-viewers` as now. `viewer-peak` carries the count only; `viewer-peak-at` carries "at 38:00". The header hint is written on render (and on each live refresh), never on `sk:playhead`.

## The chart

Unchanged in data and interaction from #211: one `tertiary` series (2px line over an 18% area), three gridlines, compact y labels, four or five timecode x labels, the dashed `primary` playhead with its tag and knob, the `rec` dot at the live edge while recording, a hover tooltip, click or drag to seek, `role="img"` with a one-sentence label and a visually hidden sample table.

- **Peak ring.** `drawViewerChart` adds a hollow 9px `tertiary` ring at the first maximum sample, in the overlay before the playhead. It is presentational; the readout gives its value and time. When the playhead or the live edge sits on the peak, they draw over it.
- **Chapter starts.** One 2px × 12px `tertiary` tick sits on the baseline at each chapter's `StartSeconds`, like the seek bar's chapter ticks. The 0:00 chapter is the axis start and is skipped (the seek bar does the same). Add the recording's chapter start seconds to the island's payload; `drawViewerChart` filters them to `0 < at < domain` and positions them by `at / domain`. The ticks are presentational: the Chapters panel is the accessible list, and they carry no text or tooltip. Draw them after the series and before the peak ring and playhead.
- The chart is on `surface` in the panel (and on the page ground in the Stats tab), not `surface-raised`; the playhead knob, rec dot and peak ring take a 2px halo in the chart's own ground.

## Rules

- **Hidden without samples.** A recording with no viewer samples renders no panel, like `Chapters`; the old "No viewer data for this recording." line stays gone.
- **One live reading.** Only the readout's label and count follow the playhead. The header hint and the three figures change when the samples change; a live refresh can raise the peak.
- The count at the playhead also shows in the Statistics overlay's Stream row, so a viewer in fullscreen still sees it.
- **Labels never take the series colour.** Only the line, area, peak ring and chapter ticks are `tertiary`; the hero, labels, values and axis labels use the text tokens. The `Groups` icon is the one tertiary mark outside the plot.
- Below `Breakpoint.Md` there is no Viewers panel: the readout and chart sit at the bottom of the **Stats** tab, under Video and Network, at full width. The tab has no header or hint. On a narrow tab the figures take the full width under the hero, spread across it above a `divider` rule.
- The chart keeps its `role="img"` label and visually hidden table. The readout updates quietly, with no live region; the disclosure button's accessible name gains the peak while collapsed.
- **No inner scroll.** The panel grows with its content; pin MudCollapse's wrapper to `overflow-y: hidden`. It otherwise becomes an unfocusable scroll region once the readout and chart are a subpixel taller. The page scrolls, not the panel.
- The chart has no entrance animation; the tooltip keeps its 120ms fade, removed under `prefers-reduced-motion`.
