# Third-party emotes via a server-side provider seam

## Status

Accepted (2026-11-14). Scoped to the private-home-use project posture. Tracked in issue #164.

## Context

Chat replay already renders Twitch emotes from the IRC `emotes` tag: the parser stores code-point ranges, and `ChatPane.razor.js` renders them from `static-cdn.jtvnw.net`. BetterTTV emotes are not tagged. They are plain text codes resolved against a provider catalog. BetterTTV publishes an unauthenticated global catalog and a channel catalog keyed by numeric Twitch user ID; its CDN serves immutable static and animated assets. Channel identity exists only server-side, and animated assets are large. The research memo (`docs/research/betterttv-emotes.md`) records the API/CDN probes, matching semantics, privacy, and terms.

## Decision

- Add a server-side `IEmoteProvider` seam. BetterTTV ships now; 7TV is the expected second provider. FFZ is not in scope.
- Serve one public aggregated endpoint, `GET /api/recordings/{id}/emotes`, returning `{v, available, stale, fetchedAt, entries}`. The server resolves the channel, fans out to enabled providers, orders providers by registration and channel before global within each provider (shared before channel in BetterTTV's channel scope), dedupes by exact case-sensitive code, drops modifiers and restricted entries, and returns static `src`/`srcset` URLs. The client never learns provider URL patterns.
- Resolve the numeric Twitch user ID at replay through `IEmoteChannelResolver`: `ChannelStore` first, then Helix `GetUserAsync`; memoize either result, including an unresolved login, for five minutes. If unresolved, serve global emotes only. Do not add a session field.
- Use replay-time catalogs. Do not snapshot catalogs or image bytes with the session.
- Match only text outside Twitch emote ranges, using BetterTTV's semantics: case-sensitive, space-delimited tokens, punctuation part of the code, first entry wins. Reply quotes remain plain text.
- Render static images only. Skip modifier/zero-width emotes in v1. Load images from the provider CDN with `referrerpolicy="no-referrer"` and no `crossorigin` attribute; do not proxy images. `crossorigin="anonymous"` was evaluated and rejected because the BetterTTV CDN sends no `Access-Control-Allow-Origin`, which would fail the image load. (7TV's CDN does send `Access-Control-Allow-Origin: *`; the addendum records the difference, and both providers stay on ordinary no-CORS loads.)
- Gate the feature with `Chat:Emotes:Bttv:Enabled=false`. Start global catalog refreshes before channel-ID resolution so a slow resolver cannot keep a cold global catalog from being fetched, then fetch channel scopes once an ID is known. Cache global and channel catalogs independently for five minutes, bound all provider work to three seconds, serve stale data on failure, and return `200 {available:false}` rather than an error. A global or channel failure preserves the other scope's catalog. Emote failures never affect chat capture, replay, or health.

## Considered options

- **Browser-direct catalog fetch.** Rejected because the numeric channel ID and the central cache/kill switch are server-side.
- **Per-provider routes with client-side merge.** Rejected because it puts provider precedence and dedupe in the pane and makes adding 7TV a client change.
- **Capture-time snapshot.** Rejected for v1 because replay-time drift was accepted and it adds capture-time coupling without preserving image bytes.
- **Server image proxy.** Rejected for v1 because it adds bandwidth, disk, and abuse surface; viewer IP disclosure is accepted for the private-home deployment.
- **Animated emotes.** Rejected for v1 because measured assets are 0.9–3.8 MB and the pane rebuilds its DOM, restarting animations.

## Consequences

- StreamKeepr gains runtime dependencies on `api.betterttv.net` (server) and `cdn.betterttv.net` (browser). The browser discloses the viewer's IP and user agent to NightDev. The BetterTTV terms are broad; private-home, non-commercial use is accepted, not legally cleared.
- Old recordings render the current channel catalog: renamed, reused, or deleted codes drift to another emote or to text.
- Adding 7TV is a provider implementation plus registration; the endpoint, response shape, client matcher, and tests remain. (The addendum below records what this understated.)
- No session-store or chat-log schema change.
- Third-party rendering is optional; chat text and Twitch emotes remain the fallback.
- Provider image requests are ordinary no-CORS `<img>` loads. The pane sends no explicit credentials, but the browser may attach any provider cookies it already holds; that is an accepted residual risk for the private-home deployment. `crossorigin="anonymous"` would prevent the cookies but breaks against the BetterTTV CDN, which sends no `Access-Control-Allow-Origin`. 7TV's CDN does send `*`, but the pane stays on the same no-CORS load for both providers rather than splitting the behaviour.

## Addendum: 7TV as the second provider (2026-11-24)

Status: accepted, tracked in issue #272. This addendum records what shipping the second provider changed; it does not reopen the decision above.

- **7TV ships behind `Chat:Emotes:SevenTv:Enabled=false`.** It is registered after BetterTTV, so registration order gives BetterTTV precedence for a shared code and no ordering code was needed.
- **API v3 is the only usable read surface.** `GET /v3/emote-sets/global` and `GET /v3/users/twitch/{id}` answer unauthenticated and without deprecation signals on `api.7tv.app` (the documented host, which this change uses) and on `7tv.io`. The v4 REST surface answers "route not found" for both catalog routes, and GQL v4 is not a REST catalog. Accepting v3 is a deliberate, reversible dependency, documented in `docs/research/7tv-emotes.md`.
- **The response-body cap rose from 2 MiB to 4 MiB for every provider.** A full 7TV channel catalog measured 2,380,529 bytes for 969 emotes, because each entry carries a deep owner/connection graph. The cap stayed a single shared constant rather than becoming per-provider; the BetterTTV bound is looser as a result, which is accepted because both bodies are still untrusted input bounded before parsing.
- **Static images are provider-shaped.** 7TV names a static frame `{size}_static.webp` and only publishes it for animated emotes; a non-animated emote has no `_static` file. BetterTTV's helper therefore stopped pretending to be provider-neutral and each provider builds its own URLs.
- **Provider emote IDs are provider-shaped.** The shared usability check now accepts BetterTTV's 24-hex form and 7TV's 26-character ULID; a single shape would have silently dropped every emote from one provider.
- **Owner accepted the `cdn.7tv.app` dependency and 7TV's terms and artwork rights** under the same private-home, non-commercial posture as BetterTTV in the decision above. The browser discloses the viewer's IP and user agent to 7TV's CDN; no image proxy was added. Not legal advice.
- **Animation is opt-in, not static-only.** `Chat:Emotes:Animated` (default `false`) makes both providers serve the animated asset instead of the static first frame, superseding the "Render static images only" decision above when it is enabled. The default keeps the original rationale: BetterTTV's animated assets measured 0.9–3.8 MB each, while 7TV's animated assets measure 13.6 KB at 1x and 65 KB at 3x. The pane rebuilds its log — clearing it with `replaceChildren()` — when a new log arrives, when the shell key changes, or when it has no rows, so an animation restarts on each rebuild; that is accepted as a known consequence of enabling the switch, not a defect. The switch is off by default so an existing deployment changes nothing.
