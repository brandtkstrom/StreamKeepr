# StatusChip

Small uppercase chips that state a recording's condition in words, with colour as a second cue.

- **Rec** — `rec` fill (Dracula Red), `on-rec` text, pulsing dot (static under reduced motion). Capture in progress.
- **Processing** — `tertiary-container` / `on-tertiary-container` (Cyan). The playlist is being finalised or remuxed.
- **Partial** — `warning-container` / `on-warning-container` (Orange). The capture has gaps (dropped connection, missed segments).
- **Saved** — outlined; used in menus and filters only. In library rows a saved recording shows no chip at all.
- Failures (e.g. "Twitch session expired") use `error-container` / `on-error-container` with a ⚠ icon.
