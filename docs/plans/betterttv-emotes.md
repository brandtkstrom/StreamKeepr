---
title: BetterTTV emotes in chat replay
version: 1.0
date_created: 2026-11-14
last_updated: 2026-11-14
owner: @brandtkstrom
tags: [chat, emotes, frontend, backend, integration]
---

Status: accepted (2026-11-14). Binding: yes for the interfaces, data contracts, and acceptance criteria; implementation freedom elsewhere. Issue: #164.

# Introduction

Chat replay renders Twitch emotes from the IRC `emotes` tag but shows BetterTTV (BTTV) emotes as plain text. This specification defines a server-side provider seam, one aggregated catalog endpoint, and client-side matching/rendering so BTTV global and channel emotes appear as static images during replay. The seam is intended to accept 7TV later without changing the endpoint, response shape, or client matcher.

## 1. Purpose & Scope

**In scope:**

- BTTV global, channel, and shared emotes in recorded chat replay.
- One public read-only catalog endpoint, `GET /api/recordings/{id}/emotes`.
- Server-side channel-ID resolution and catalog caching.
- Client-side matching of BTTV codes in message text outside Twitch emote ranges.
- Static image rendering with text fallback.
- A feature flag, off by default.
- Contract, ADR, test, and research updates.

**Out of scope:**

- Live chat. The application has no live-chat surface.
- 7TV implementation. The provider seam must accept it later.
- FFZ.
- Personal BTTV emotes (they require per-viewer identity).
- Capture-time catalog snapshots or image bytes.
- Server-side image proxying.
- Animated emotes, modifier/zero-width stacking, reply-quote emote rendering, badges, and click-through.
- Changes to the chat log or session-store schema.

**Assumptions:**

- The deployment is the private-home-use posture in `README.md`, `SECURITY.md`, and `docs/hosting-contract.md`.
- The operator accepts browser image loads from `cdn.betterttv.net`, including viewer IP and user agent disclosure.
- The operator accepts replay-time catalog drift.

## 2. Definitions

- **Emote**: a chat token that renders as an inline image, identified by its text code.
- **Twitch emote**: an emote Twitch identifies in the IRC `emotes` tag.
- **Third-party emote**: an emote resolved by matching message text against a provider catalog.
- **Emote provider**: a service that publishes emote catalogs for Twitch channels, such as BetterTTV or 7TV.
- **Emote catalog**: a provider's global and channel-specific set of emote codes and image references.
- **Catalog entry**: one normalized provider emote: provider, provider emote ID, code, animated flag, and static image URLs.
- **Native range**: a code-point range in a chat message that Twitch tagged as an emote.

See `GLOSSARY.md` for the canonical definitions.

## 3. Requirements, Constraints & Guidelines

### 3.1 Endpoint and provider seam

- **REQ-001**: The server MUST expose `GET /api/recordings/{id}/emotes` as a public, read-only endpoint with `Cache-Control: no-store`.
- **REQ-002**: The endpoint MUST return `404` for an unknown recording.
- **REQ-003**: The endpoint MUST NOT call any provider when `Chat:Emotes:Bttv:Enabled` is `false`; it MUST return `200` with `available:false` and an empty `entries` array.
- **REQ-004**: The endpoint MUST aggregate enabled providers behind an internal `IEmoteProvider` seam. BetterTTV is the only implementation in this change.
- **REQ-005**: The endpoint MUST resolve the recording's channel login to a numeric Twitch user ID from `ChannelStore` first and `IStreamPoller.GetUserAsync` second. It MUST cache the resolution for five minutes. If the ID cannot be resolved, it MUST skip channel catalogs and still serve global entries.
- **REQ-006**: The endpoint MUST order entries by provider registration order, then channel before global within a provider. BetterTTV MUST place shared emotes before channel emotes within the channel scope so shared codes win duplicates, matching BetterTTV's own client.
- **REQ-007**: The endpoint MUST dedupe entries by exact, case-sensitive `code`; the first entry wins.
- **REQ-008**: The endpoint MUST drop modifier entries (BTTV `modifier:true`), restricted entries (no viewer identity exists), and entries with invalid IDs, empty codes, or codes longer than 128 characters. The BTTV provider MUST match `code`, never `codeOriginal`.
- **REQ-009**: The endpoint MUST build static image `src`/`srcset` URLs from validated provider emote IDs and fixed provider templates. It MUST NOT echo a provider-supplied URL.
- **REQ-010**: The endpoint MUST cap each provider catalog at 2,000 entries and each provider HTTP response body at 2 MiB; excess or invalid entries are dropped.
- **REQ-011**: The endpoint MUST return an empty catalog with `available:false` on provider failure when no cached catalog exists; it MUST return stale cached entries with `stale:true` when a refresh fails.
- **REQ-012**: The endpoint MUST bound provider work (including channel-ID resolution) to three seconds. On timeout it MUST serve cached or global entries and continue.
- **REQ-013**: Global catalogs MUST be cached once per provider; channel catalogs MUST be cached per provider and channel ID. Both use a five-minute TTL. Concurrent requests for the same key MUST share one refresh (single-flight).

### 3.2 Matching and rendering

- **REQ-014**: The client MUST match only text outside Twitch native ranges. It MUST NOT concatenate text across a native range.
- **REQ-015**: Matching MUST be case-sensitive and operate on space-delimited tokens. Punctuation is part of the token. The client MUST preserve every character, including repeated, leading, and trailing spaces.
- **REQ-016**: The client MUST use the first catalog entry for a code; the server-provided order is the precedence.
- **REQ-017**: The client MUST render a matched token as an `<img>` with the server-provided `src` and `srcset`, `alt` and `title` set to the code, `loading="lazy"`, `decoding="async"`, and `referrerpolicy="no-referrer"`.
- **REQ-018**: On image load error, the client MUST replace that image with the code as text and MUST NOT retry.
- **REQ-019**: The client MUST fetch the catalog once per recording load, at the first moment the pane is open and `emotesEnabled` is true. A failed fetch MUST leave text rendering for that recording and MUST NOT retry per message.
- **REQ-020**: The reply-quote preview MUST remain plain text.

### 3.3 Configuration and isolation

- **REQ-021**: Add `Chat:Emotes:Bttv:Enabled`, default `false`. The cache TTL (300 s) and timeout (3 s) are constants, not configuration.
- **REQ-022**: `ChatPane.razor` MUST include `emotesEnabled` in the pane load payload. The value is true when any provider is enabled.
- **REQ-023**: Emote catalog and image failures MUST NOT affect chat capture, chat replay, recording, `/health/live`, or `/health/ready`.
- **REQ-024**: The server MUST log one warning per failed refresh with provider ID, channel ID, and HTTP status. It MUST NOT log response bodies.
- **REQ-025**: Do not add an attribution label or a user-visible failure notice for third-party emotes. Failures fall back to text and are diagnosed from logs.

### 3.4 Security and privacy

- **SEC-001**: Provider responses are untrusted input. Validate IDs, codes, and sizes; use `textContent` and attributes, never `innerHTML`.
- **SEC-002**: Server requests to providers MUST be credential-free: no cookies, no `Authorization`, no admin or chat tokens.
- **SEC-003**: Outbound URLs MUST be fixed provider endpoints. The only variable path segments are a validated numeric channel ID and a validated provider emote ID.
- **SEC-004**: Browser image requests MUST use `referrerpolicy="no-referrer"`. The client MUST NOT send BTTV cookies or credentials.
- **SEC-005**: The client MUST accept image URLs only when they are absolute `https:` URLs.

### 3.5 Constraints

- **CON-001**: Do not change `StoredChatMessage`, `StoredChatMeta`, `SessionManifest`, or the chat JSONL schema.
- **CON-002**: Do not add a database.
- **CON-003**: Do not add a new NuGet package for this feature.
- **CON-004**: Do not add an admin UI toggle. Configuration is the switch.
- **CON-005**: Do not proxy or cache image bytes.

## 4. Interfaces & Data Contracts

### 4.1 Endpoint

`GET /api/recordings/{id}/emotes`

Response `200 application/json`:

```json
{
  "v": 1,
  "available": true,
  "stale": false,
  "fetchedAt": "2026-11-14T12:34:56Z",
  "entries": [
    {
      "provider": "bttv",
      "id": "5ff36d0a95e8e11f4f6f0a1c",
      "code": "KEKW",
      "animated": false,
      "src": "https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/1x.webp",
      "srcset": "https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/1x.webp 1x, https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/2x.webp 2x, https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/3x.webp 3x"
    }
  ]
}
```

- `v`: schema version, `1`.
- `available`: true when at least one enabled provider returned a catalog (including an empty one) or served a stale cache.
- `stale`: true when any served catalog came from a stale cache.
- `fetchedAt`: the earliest successful catalog fetch time among the catalogs used to build the response, or `null` when `available` is false.
- `entries`: ordered, normalized, deduped, static-only catalog entries.
- Unknown recording: `404`. Disabled or failed with no cache: `200` with `available:false`, `stale:false`, `fetchedAt:null`, `entries:[]`.
- `Cache-Control: no-store`.

### 4.2 Provider seam

```csharp
internal interface IEmoteProvider
{
    string Id { get; } // "bttv"
    Task<EmoteProviderCatalog> GetCatalogAsync(EmoteChannel channel, CancellationToken ct);
    EmoteImageSet GetStaticImage(EmoteCatalogEntry entry);
}

internal sealed record EmoteChannel(string Login, string? TwitchUserId);

internal sealed record EmoteProviderCatalog(
    IReadOnlyList<EmoteCatalogEntry> Channel,
    IReadOnlyList<EmoteCatalogEntry> Global);

internal sealed record EmoteCatalogEntry(
    string Id,
    string Code,
    bool Animated,
    bool Modifier);

internal sealed record EmoteImageSet(string Src, string SrcSet);
```

- `Channel` is ordered by the provider; BetterTTV returns shared entries before channel entries.
- `GetStaticImage` returns 1x/2x/3x URLs. For BTTV: `https://cdn.betterttv.net/emote/{id}/{size}.webp` for static emotes and `https://cdn.betterttv.net/emote/{id}/static/{size}.webp` for animated emotes.
- `Modifier` entries are filtered by the aggregation service, not rendered.
- The aggregation service owns caching, single-flight, ordering, dedupe, and URL assembly; providers only fetch and normalize.

### 4.3 Client load payload and matching

- `ChatPane.razor` adds `emotesEnabled` to `ToPayload`.
- `ChatPane.razor.js` stores the flag, fetches the catalog once per recording load when open and enabled, and rebuilds the log when the catalog arrives.
- The matcher receives `(text, nativeRanges, catalogMap)` and emits text nodes and images. It works on Unicode code-point arrays, splits on U+0020, and preserves all characters.
- Native ranges win; the matcher only visits text segments between them.
- The catalog map is built with first-entry-wins semantics.

## 5. Acceptance Criteria

- **AC-001**: Given a recording with a message `hello KEKW` and BTTV enabled with `KEKW` in the catalog, when the pane opens, then the message contains an `<img>` with the BTTV 1x/2x/3x URLs, `alt="KEKW"`, `loading="lazy"`, `decoding="async"`, and `referrerpolicy="no-referrer"`.
- **AC-002**: Given a message with a Twitch native emote range and a BTTV code on either side, when rendered, then the Twitch emote is unchanged and the BTTV code outside the range renders as an image; no token is formed across the native range.
- **AC-003**: Given `Brazil` and `BRAZIL` in the catalog, when both appear in text, then each renders with its own image; matching does not case-fold.
- **AC-004**: Given `:tf:`, `M&Mjc`, or `D:` in text, when present in the catalog, then it matches exactly; punctuation is not stripped.
- **AC-005**: Given `KappaKappa`, when only `Kappa` is in the catalog, then it remains text; `Kappa Kappa` renders two images.
- **AC-006**: Given a modifier code such as `w!`, when enabled, then it renders as text.
- **AC-007**: Given `Chat:Emotes:Bttv:Enabled=false`, when the pane loads, then no catalog request is made and chat renders text plus Twitch emotes.
- **AC-008**: Given a provider failure with no cache, when the pane loads, then the endpoint returns `200` with `available:false`, the pane renders text, and chat remains usable.
- **AC-009**: Given an image `404`, when the image fails, then the code appears as text and no retry occurs.
- **AC-010**: Given an unresolved channel ID, when the catalog is fetched, then global entries render and channel entries are absent.
- **AC-011**: Given an animated BTTV emote, when enabled, then its static first frame renders.
- **AC-012**: Given a catalog response arriving after messages are already visible, when it arrives, then the log re-renders once with the catalog applied.
- **AC-013**: Given the pane's 1000-row cap, when the pane opens, then no more than one catalog request is made per recording load.

## 6. Test Automation Strategy

- **Unit (`tests/StreamKeepr.Tests`)**: provider normalization (global/channel/shared order, invalid IDs, empty codes, modifiers, restricted entries), image URL assembly, cache TTL/stale/single-flight, channel-ID resolution fallback, response caps, and credential-free HTTP.
- **Integration (`tests/StreamKeepr.Tests`)**: `WebApplicationFactory` with a fake `HttpMessageHandler`: endpoint shape, unknown recording `404`, disabled `200`, failure `available:false`, stale cache, `no-store`, global-only fallback.
- **JS (`tests/js/chat-pane.test.js`)**: pure matcher cases from AC-002 through AC-006, and DOM cases for AC-001, AC-007 through AC-013. Use the existing injected `fetch` and `storage` seams.
- **E2E (`tests/StreamKeepr.E2E`)**: Mock profile serves a fixed BTTV catalog; one Playwright journey asserts a third-party emote image and text fallback.
- **No external network** in any test. The Mock profile must not call BTTV.
- **No health check** for this feature.

## 7. Rationale & Context

See `docs/research/betterttv-emotes.md` for the API/CDN probes and matching evidence, and `docs/adr/0011-third-party-emotes-provider-seam.md` for the decision. The key constraints are: BTTV channel catalogs require a numeric Twitch ID that only the server has; the pane already owns chat DOM and uses code-point ranges; animated assets are too large for the pane's 1000-row re-render behavior; and replay-time catalogs are accepted as approximate.

## 8. Dependencies & External Integrations

- **EXT-001**: `https://api.betterttv.net/3/cached/emotes/global` — server-side, unauthenticated global catalog.
- **EXT-002**: `https://api.betterttv.net/3/cached/users/twitch/{providerId}` — server-side, unauthenticated channel catalog; `providerId` is the numeric Twitch user ID.
- **EXT-003**: `https://cdn.betterttv.net/emote/{id}/{size}.webp` and `/static/{size}.webp` — browser-side static images.
- **EXT-004**: Twitch Helix `GET /users?login=` — server-side fallback for the numeric channel ID.
- **COM-001**: BetterTTV terms and privacy policy; private-home, non-commercial use accepted by the owner. Not legal advice.

## 9. Edge Cases

- Provider returns `200` with empty arrays: treat as an available channel with no emotes, not a failure.
- Channel `404`: cache an empty channel catalog for five minutes; global remains.
- Provider `429`/`5xx`/timeout: stale cache if present; otherwise `available:false`.
- Channel renamed or removed: channel entries may disappear; global remains; no schema persistence in v1.
- Deleted emote image: browser image error → code as text.
- Multiple emotes adjacent: only space-delimited tokens match.
- Action messages (`/me`): the stored text already has the ACTION prefix stripped; match normally.
- Unicode before/after an emote: use code-point arrays, never `string.length` or `charAt`.
- Duplicate codes across providers: server order decides; first wins.
- A catalog response that arrives after the pane switches recordings is ignored.
- A catalog response that arrives after the pane is disposed is ignored.

## 10. Validation Criteria

- All ACs have an automated test at the highest applicable layer.
- `dotnet build -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test`, `npm run lint`, and `npm test` pass.
- No test calls `api.betterttv.net` or `cdn.betterttv.net`.
- The feature is off by default and produces no outbound request while off.
- No chat log, session manifest, or media allowlist change.

## 11. Implementation Sequence

1. **Thin end-to-end slice.** Provider seam, aggregated endpoint with BetterTTV global only, `emotesEnabled` in the pane payload, client matcher/static renderer, feature flag, and unit/JS/integration tests.
2. **Complete the feature.** Channel and shared catalogs, channel-ID resolution/cache, Mock/E2E fixture, and the contract/ADR documentation.

Each increment stays behind `Chat:Emotes:Bttv:Enabled` and leaves chat usable when the flag is off.

## 12. Related Specifications / Further Reading

- Issue #164: Render BetterTTV emotes in chat replay
- `docs/research/betterttv-emotes.md`
- `docs/adr/0011-third-party-emotes-provider-seam.md`
- `docs/backend-ui-contract.md` (chat replay and emote catalogs)
- `docs/hosting-contract.md` (endpoint map and outbound dependencies)
- `docs/frontend-foundation-contract.md` (chat pane JavaScript ownership)
- `docs/runtime-contract.md` (`Chat:Emotes:Bttv:Enabled`)
- `docs/testing-contract.md` (test layers)
- `GLOSSARY.md` (emote terms)
