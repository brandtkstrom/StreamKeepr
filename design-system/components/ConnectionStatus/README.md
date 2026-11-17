Settings › Connection status: the health of the Twitch EventSub connection and the polling fallback, as a key/value table.

## Build it from

`MudPaper` (`sk-panel`, `pa-4`) titled "Connection status" in `h6` after a `Wifi` icon, holding a `MudSimpleTable Dense` (or a `<dl>`) of 40px rows divided by `divider`. Keys are in `caption` / `text-secondary`, and values in `stat-value` or `body2`:

Service health (`StatusChip` Healthy / Degraded / Unhealthy plus a detail in `caption`), Transport, Connection (Connected / Reconnecting / Disconnected), Coverage ("3 of 4 channels", Partial when short), Last message, Last poll (with "last successful 2 min ago" when the last poll failed), Twitch API round trip, and Poll interval.

When coverage is partial, a `MudAlert Severity.Info Dense` follows (with the `Schedule` icon): "All channels are polled every 15 s until every channel is covered."

## Rules

- Times are relative ("12 s ago"). Intervals are "Every 60 s".
- The panel renders nothing when signed out.

## As built (#179)

- The round-trip key reads "Twitch API", as before.
- The partial-coverage alert has `role="note"` rather than MudAlert's default `alert`, so it is not announced on every render.
