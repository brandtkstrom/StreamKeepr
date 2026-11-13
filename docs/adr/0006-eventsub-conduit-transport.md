# EventSub conduit transport behind a flag

## Status

Accepted (2026-11-13). Amends ADR-0003 for conduit mode only.

## Context

EventSub currently uses a user-token WebSocket. Its cost budget is 10, so only about five watched channels get full push coverage, and push stops whenever no admin is signed in. The spike in issue #131 (`docs/research/eventsub-conduits-spike.md`) found that conduits are generally available, use the app access token `TwitchTokenProvider` already holds, need no scopes for `stream.online`/`stream.offline`, and raise the budget to about 10,000 (about 5,000 channels). Several Twitch behaviours are not documented and still need a live prototype: the actual cost budget, how a shard behaves on `session_reconnect`, and whether a 409 matches across transports.

## Decision

- `Twitch:EventSubTransport` selects `WebSocket` (default) or `Conduit`. Polling stays the safety net in both modes, and coverage stays all-or-nothing.
- Conduit mode uses one conduit with one WebSocket shard (shard `0`). The conduit ID and the client ID that owns it are saved in `/recordings/.streamkeepr/eventsub-conduit.json`. Subscription IDs are not saved; Twitch lists them by conduit.
- On each session welcome, shard 0 is bound to the socket first (Twitch allows 10 s), and then the conduit's subscriptions are reconciled with the watched channels. On a reconnect handoff, the shard is bound again before the old socket closes.
- **Admin sign-in and logout do not affect conduit push.** ADR-0003's "EventSub runs only while a token is available" applies to WebSocket mode only. `IAdminTokenStore` still backs sign-in, logout, and session validation.
- The `No admin token` push reason stays and is reachable only in WebSocket mode.
- Stream Keepr never deletes a conduit it did not create. In WebSocket mode, a saved conduit owned by the current client ID is deleted at startup (rollback). A conduit saved under a different client ID is forgotten and expires on Twitch's side.

## Consequences

- Push no longer needs an admin sign-in, and capacity is bounded by the app-token budget rather than 10.
- Stream Keepr now owns Twitch-side state that outlives the process. A lost state file leaks a conduit until Twitch deletes it, 72 hours after its shard disconnected, and five leaks block conduit creation.
- With one shard, every reconnect gap can drop notifications; polling covers the gap. A second shard and `message_id` dedupe are deferred.
- Subscription creates now share the app-token rate bucket with the poller.
- The flag defaults to `WebSocket` until the owner has run the live prototype and observed conduit mode on a real instance (spike memo §5, rollout).
