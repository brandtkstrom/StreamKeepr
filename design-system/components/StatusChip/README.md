Small uppercase chips that state a recording's or a channel's condition in words, with colour as the second cue.

## Build it from

`MudChip T="string" Size="Size.Small" Class="sk-status sk-status-{kind}"` with an `Icon`, the `overline` type, and a `tint-opacity` (14%) fill of the kind's colour. **The label stays `text-primary`; only the icon takes the colour.** Every chip reads the same way: word first, colour second.

| Kind | Colour | Icon | Label |
|---|---|---|---|
| Rec | filled `rec`, label `rec-contrast-text` | pulsing dot | REC |
| Reconnecting | `warning` | `Sync` | RECONNECTING |
| Processing | `secondary` | `HourglassTop` | PROCESSING |
| Partial | `warning` | `Warning` | PARTIAL |
| Saved | neutral (8% `text-primary`), icon `text-secondary` | `Check` | SAVED |
| Error | `error` | `Warning` | ERROR, or the specific text ("Twitch session expired") |

Settings reuse the shape for Healthy (`success`, `CheckCircle`), Connected, Push or Polling (`warning`, `Schedule`), and Paused (neutral, `Pause`).

## Rules

- REC is the only filled chip, and it is the only place `rec` fills anything.
- One chip per row. Never use a chip as a button.
- Under `prefers-reduced-motion`, the REC dot stops pulsing.

## As built (#179)

The Settings success shape is its own kind, `Healthy` (`data-status="healthy"`, icon `check_circle`), used for Healthy, Connected, full coverage and Push. Polling is the Partial kind with the `schedule` icon. Paused and Off stay neutral (the Saved shape with `pause` / `remove`).
