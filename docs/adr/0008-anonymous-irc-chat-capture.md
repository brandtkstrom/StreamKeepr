# Anonymous IRC as best-effort chat capture

## Status

Accepted (2026-11-13). Scoped to the private-home-use project posture.

## Context

Stream Keepr records arbitrary public Twitch channels. EventSub `channel.chat.message` is the better documented chat surface, but it requires a user token with `user:read:chat` and, in practice, broadcaster/moderator standing or `channel:bot` authorization from each broadcaster. Stream Keepr cannot obtain that for arbitrary channels without coupling capture to an admin or per-channel authorization.

Twitch IRC over WebSocket is the remaining read path. Current Twitch docs require `PASS oauth:<user-token>` with `chat:read`; anonymous `justinfan<digits>` access is undocumented but still used by Chatterino, tmi.js, `twitch-irc`, and `recent-messages2`. It has no documented limits, no history, and no delivery guarantees.

## Decision

- Use one read-only IRC connection over `wss://irc-ws.chat.twitch.tv:443`, behind `Chat:Enabled=false`.
- Default to anonymous `justinfan<random>` with no `PASS`. If Twitch rejects the handshake, log the failure and retry with backoff; configure the token fallback to restore capture.
- Offer `Chat:TwitchUserOAuthToken` with `chat:read` as a documented fallback. Validate it on the IRC authentication attempt; on failure, log without the token and switch to anonymous capture for the process. Never use the admin token or the Streamlink recorder token.
- Request `twitch.tv/tags` and `twitch.tv/commands`; do not request membership. Capture `PRIVMSG`, `CLEARMSG`, `CLEARCHAT`, `ROOMSTATE`, `NOTICE`, `PING`, and `RECONNECT`.
- Start capture at session open and stop at finalize/removal. One multiplexed connection; at most 100 joined channels; pace joins.
- Treat chat as a live tap: dedupe by message ID, write explicit gaps for disconnects, and never claim a complete transcript.
- Chat failures are isolated: they never affect recording, the DVR state, or `/health/live`. Polling remains the recording safety net.
- EventSub remains the transport for privileged events; it is not the primary chat path.

## Consequences

- Anonymous access can change or disappear without notice. A one-hour setup plus 24-hour soak is required before enabling the feature; the token fallback is the documented escape hatch.
- Anonymous limits and restricted-room behavior remain unknown. The 100-channel cap is per authenticated user and sharding does not bypass it.
- IRC has no history or replay. Messages missed during a reconnect are gone; the chat log shows a gap.
- IRC is legacy in Twitch's guidance. The `IChatSource` seam keeps an eventual EventSub or TCP/TLS transport possible without changing the parser or the log.
