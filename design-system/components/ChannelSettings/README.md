Settings › Channels: the watched Twitch channels with their capture state, push or polling coverage, and the actions for each, plus a field to add one.

## Build it from

- `MudPaper Elevation="0" Outlined="true" Class="sk-panel"` titled "Channels" in `h6` after a `LiveTv` icon.
- A `MudList` (or plain `<ul>`) of rows divided by `divider`: a `MudAvatar` on the channel's channel colour (`channel-*`) (`rec` badge while capturing), the display name in `subtitle1` with the login in `caption`, a state `StatusChip` (Rec, Watching, Paused, Reconnecting), a coverage chip (Push, or Polling with `Schedule`), then text buttons: Stop recording or Pause, or Resume recording (`Color.Primary`), and Remove.
- While a row is busy, swap its buttons for a small spinner and "Removing…" (`role="status"`), and set `aria-busy` on the row.
- Add: a "Add channel" label, `MudTextField Variant.Outlined Margin.Dense` (placeholder "Twitch login", no spellcheck, Enter submits), and a filled "Add" (`Add` icon; "Adding…" with a spinner while busy). Errors go under the field in `error` (`role="alert"`).

## Rules

- Remove and Stop open a `Dialog`: "Remove Copper Moth?" → "Recordings are kept. An open capture is finalized first."
- Every row action's `aria-label` names the channel ("Remove Copper Moth").
- Signed out, show only "Connect Twitch to manage channels" and the Twitch button.
