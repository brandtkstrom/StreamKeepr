Inline and transient feedback: alerts, snackbars, linear progress and tooltips.

## Build it from

- **`MudAlert Variant="Variant.Text" Dense="true"`**: a 14% tint of the severity colour, the icon in that colour, and the text in `text-primary`. Uses: "Live updates paused" (`Warning`, with a Retry text button, replacing `ReconnectNotice`), the plain-HTTP note on Settings (`Info`), and a channel that could not be added (`Error`).
- **Snackbars**: `ISnackbar.Add(...)` at `Defaults.Classes.Position.BottomLeft`, one visible at a time, about 4s. Use them for confirmations of something already done ("Playlist URL copied", "Recording deleted"; it has no Undo, because the delete dialog already says the removal is permanent). The ground is `surface` with a `lines-default` outline and `elevation-8`.
- **`MudProgressLinear Size.Small`**: `primary` for playback-related progress, `secondary` for exports, and `warning` for the disk-usage bar in the drawer footer under 10% free.
- **`MudTooltip`**: restyled to `tooltip-background` / `tooltip-text`, with `caption` type and `radius-sm`. Use it for disabled reasons and exact values ("Recording disk: 1.2 TB free of 4 TB").

## Rules

- Errors are never only a snackbar: put the error next to the thing that failed.
- Text in an alert never takes the severity colour.
