# Third-party emotes via a server-side provider seam

## Status

Accepted (2026-11-14). Scoped to the private-home-use project posture. Tracked in issue #164.

## Context

Chat replay already renders Twitch emotes from the IRC `emotes` tag: the parser stores code-point ranges, and `ChatPane.razor.js` renders them from `static-cdn.jtvnw.net`. BetterTTV emotes are not tagged. They are plain text codes resolved against a provider catalog. BetterTTV publishes an unauthenticated global catalog and a channel catalog keyed by numeric Twitch user ID; its CDN serves immutable static and animated assets. Channel identity exists only server-side, and animated assets are large. The research memo (`docs/research/betterttv-emotes.md`) records the API/CDN probes, matching semantics, privacy, and terms.

## Decision

- Add a server-side `IEmoteProvider` seam. BetterTTV ships now; 7TV is the expected second provider. FFZ is not in scope.
- Serve one public aggregated endpoint, `GET /api/recordings/{id}/emotes`, returning `{v, available, stale, fetchedAt, entries}`. The server resolves the channel, fans out to enabled providers, orders providers by registration and channel before global within each provider (shared before channel in BetterTTV's channel scope), dedupes by exact case-sensitive code, drops modifiers and restricted entries, and returns static `src`/`srcset` URLs. The client never learns provider URL patterns.
- Resolve the numeric Twitch user ID at replay from `ChannelStore`, then Helix `GetUserAsync`; cache the result. If unresolved, serve global emotes only. Do not add a session field.
- Use replay-time catalogs. Do not snapshot catalogs or image bytes with the session.
- Match only text outside Twitch emote ranges, using BetterTTV's semantics: case-sensitive, space-delimited tokens, punctuation part of the code, first entry wins. Reply quotes remain plain text.
- Render static images only. Skip modifier/zero-width emotes in v1. Load images from the provider CDN with `referrerpolicy="no-referrer"`; do not proxy images.
- Gate the feature with `Chat:Emotes:Bttv:Enabled=false`. Cache global and channel catalogs independently for five minutes, bound provider work to three seconds, serve stale data on failure, and return `200 {available:false}` rather than an error. A global or channel failure preserves the other scope's catalog. Emote failures never affect chat capture, replay, or health.

## Considered options

- **Browser-direct catalog fetch.** Rejected because the numeric channel ID and the central cache/kill switch are server-side.
- **Per-provider routes with client-side merge.** Rejected because it puts provider precedence and dedupe in the pane and makes adding 7TV a client change.
- **Capture-time snapshot.** Rejected for v1 because replay-time drift was accepted and it adds capture-time coupling without preserving image bytes.
- **Server image proxy.** Rejected for v1 because it adds bandwidth, disk, and abuse surface; viewer IP disclosure is accepted for the private-home deployment.
- **Animated emotes.** Rejected for v1 because measured assets are 0.9–3.8 MB and the pane rebuilds its DOM, restarting animations.

## Consequences

- StreamKeepr gains runtime dependencies on `api.betterttv.net` (server) and `cdn.betterttv.net` (browser). The browser discloses the viewer's IP and user agent to NightDev. The BetterTTV terms are broad; private-home, non-commercial use is accepted, not legally cleared.
- Old recordings render the current channel catalog: renamed, reused, or deleted codes drift to another emote or to text.
- Adding 7TV is a provider implementation plus registration; the endpoint, response shape, client matcher, and tests remain.
- No session-store or chat-log schema change.
- Third-party rendering is optional; chat text and Twitch emotes remain the fallback.
