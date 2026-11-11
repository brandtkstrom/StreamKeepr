# RecordingDetails

A collapsible card under the stats panel with the recording's title, streamer, category, size and the actions for it.

- Collapsed it shows only "Recording details" and the start date, so it costs 48px.
- Title in `headline-small`; meta row in `body-medium` `on-surface-variant` with dot separators.
- Actions: Export MP4 (tonal), Copy playlist URL (outlined), Delete (icon button; confirm in a dialog on `error-container`). Only one filled button per screen, and it is not here.
