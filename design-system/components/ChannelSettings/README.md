Settings › Channels: a field to add a Twitch channel, then the watched channels with their capture state, push or polling coverage, and the actions for each.

## Build it from

- A `SettingsSection` (outlined `MudPaper`, `sk-panel`) titled "Channels" in `h6` after a `LiveTv` icon.
- Directly under the title, before the list: the Add field (below), set off from the list by a `divider` hairline. Adding is the section's main action, so it never moves down as the list grows.
- A plain `<ul role="list">` of rows divided by `divider`. Not `MudList`: `MudListItem` wraps its content in a `<p>`, which cannot hold the avatar or the buttons. Each row: a 40px `ChannelAvatar` on the channel's channel colour (`channel-*`, decorative) with the `rec` badge while capturing, the display name in `subtitle1` with the login in `caption`, a state `StatusChip` (Rec, Watching, Paused, Reconnecting, Cooling down), a coverage chip (Push as the success `Healthy` chip, or Polling in `warning` with `Schedule`), then text `MudButton`s: Stop recording or Pause channel, or Resume recording (`Color.Primary`), and Remove.
- While a row is busy, swap its buttons for a small spinner and "Removing…" (`role="status"`, hook `data-channel-busy`), and set `aria-busy` on the row. The `rec` badge's `MudBadge` is a `role="status"` region too, so tests find the busy status by its hook.
- Add: `MudTextField Variant.Outlined Margin.Dense` with its own "Add channel" label (placeholder "Add a Twitch channel", no spellcheck, Enter submits), and a filled primary "Add" `MudButton` (`Add` icon; "Adding…" with a spinner while busy). Errors go under the field in `error` (`role="alert"`) and mark the field invalid.

## Rules

- Remove and Stop open a `Dialog`: "Remove Copper Moth?" → "Recordings are kept. An open capture is finalized first."
- Every row action's `aria-label` names the channel ("Remove Copper Moth").
- Signed out, show only "Connect Twitch to manage channels" and the Twitch button.
- The component holds Channels only; Connection status is its sibling in `SettingsSections`.
