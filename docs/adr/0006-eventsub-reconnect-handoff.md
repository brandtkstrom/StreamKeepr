# Keep the old EventSub socket until the new welcome

## Status

Accepted

## Context

Twitch's EventSub WebSocket `session_reconnect` message means the edge server is being swapped. Twitch sends the new URL 30 seconds before closing the old connection, says not to close the old connection until the new connection's `session_welcome`, and states that the new connection inherits the old subscriptions. EventSub has no replay: anything delivered to a closed socket is lost. The previous client treated `session_reconnect` as a restart, disposed the old socket, connected to the new URL, and re-created every subscription, creating a coverage gap and redundant 409-prone requests.

## Decision

On `session_reconnect`, create and connect the replacement socket while the old socket stays open. Continue reading and routing notifications from the old socket until the replacement sends `session_welcome`; then adopt the replacement, close the old socket, and continue. Do not re-create subscriptions on the reconnect welcome: the replacement connection already carries them. If the handoff cannot complete within its budget, close both sockets and fall back to a fresh base-URL session with normal backoff and resubscription. If the channel set or admin token changes during the handoff, cancel both and start a fresh base session so a stale subscription set is never adopted. Deduplicate overlapping notifications by `metadata.message_id`.

## Considered options

- **Close the old socket first (previous behavior).** Simple, but loses the event stream during the gap and discards the subscription transfer, forcing a full resubscribe.
- **Ignore the reconnect URL and reconnect from the base URL.** Same resubscribe cost and a longer gap; loses Twitch's edge handoff.
- **Keep the old socket and skip dedupe.** The overlap can deliver the same notification twice; duplicate offline observations can advance the reducer's two-offline finalization.

## Consequences

- `EventSubClient` owns two sockets during the handoff and must serialize message handling across both.
- The bounded dedupe set of recent `metadata.message_id` values becomes part of the EventSub contract.
- A successful handoff preserves push status without a backoff delay; a failed handoff reconnects from the base URL with the normal exponential backoff.
- Channel/token changes and revocations take precedence over the handoff; the client aborts to a fresh base session rather than adopting a stale subscription set.
- Tests need per-socket message control to prove the old socket is drained before it is closed and that no subscription POSTs occur on the reconnect welcome.
