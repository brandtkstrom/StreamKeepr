Settings › Connection status: the health of the Twitch EventSub connection and the polling fallback, as a key/value table.

## Build it from

A `SettingsSection` (outlined `MudPaper`, `sk-panel`, `pa-4`) titled "Connection status" in `h6` after a `Wifi` icon, holding a `MudSimpleTable Dense` of 40px rows divided by `divider`. Keys are row headers (`th scope="row"`) in `caption` / `text-secondary`, and values in `body2`:

Service health (`StatusChip` Healthy / Degraded / Unhealthy plus a detail in `caption`), Transport, Connection (Connected / Reconnecting / Waiting for admin sign-in / Off), Coverage ("3 of 4 channels", Partial when short; only while Connected), Last message, Last poll (with "last successful 2 min ago" when the last poll failed), Twitch API (the round trip), and Poll interval.

When coverage is partial, a `MudAlert Severity.Info Dense` follows (with the `Schedule` icon, `role="note"` so it is not announced as an alert): "All channels are polled every 15 s until every channel is covered."

## Rules

- Times are relative ("12 s ago"). Intervals are "Every 60 s".
- Healthy, Connected and full coverage use the success `Healthy` chip; red is reserved for an unhealthy service; amber means reduced capacity.
- The panel renders nothing when signed out.
