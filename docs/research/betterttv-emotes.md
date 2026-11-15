# Research memo: BetterTTV emotes in chat replay

Status: research memo, 2026-11-14.
Issue: #164, opened 2026-11-14 after this research. At research time `gh issue list --search emote --state all` returned no results. #153 is the chat-replay feature issue, and its scope does not mention third-party emotes. The v1 deferral is explicit at `docs/research/twitch-chat-replay.md:577`.
Binding: no. This memo informs a decision and changes nothing. Adoption of the recommended slice would amend `docs/backend-ui-contract.md`, `docs/hosting-contract.md`, `docs/frontend-foundation-contract.md`, `docs/testing-contract.md`, and `docs/runtime-contract.md`. It would not amend `docs/session-store-contract.md`; only the deferred capture-time snapshot (Option C) would. An ADR/decision-digest entry for the third-party CDN dependency would also be warranted.

Decision: accepted in issue #164 and `docs/adr/0011-third-party-emotes-provider-seam.md` as a provider seam with an aggregated endpoint, replay-time catalogs, static-only images, and the feature off by default. The implementation spec is `docs/plans/betterttv-emotes.md`. Where this memo and the ADR differ, the ADR wins; the memo remains non-binding research.

**Method.** BetterTTV facts come from the official API documentation, the official `night/betterttv` client source, the BetterTTV Terms of Service and Privacy Policy, and live `curl -i` requests to `api.betterttv.net` and `cdn.betterttv.net`. Twitch user-ID resolution comes from the official Helix Get Users reference. Web-platform behavior comes from MDN. All web sources were retrieved on **2026-11-14**; links are in §12. No credentials were used, no authenticated BTTV endpoint was called, and no app was run. Repository citations are `path:line` from the working tree at HEAD `67df980`. Serena's C# language server returned only the namespace for the inspected C# files, so C# was read with targeted `read` calls; Markdown and JS were read natively (reported per `AGENTS.md`).

## 1. Summary and recommendation

**Verdict: feasible with caveats. Recommendation: GO with a same-origin server catalog endpoint plus client-side matching/rendering (Option B-lite), static images only, BetterTTV global plus channel emotes, and no capture-time snapshot or image proxy in v1.**

The pane already has the hard parts: it stores Twitch-native emote ranges as Unicode code points, renders them as `<img>` with `srcset`, and is fetch-injectable for tests. BTTV exposes an unauthenticated global catalog and a per-channel catalog keyed by numeric Twitch user ID; simple cross-origin GETs are allowed. Its CDN serves immutable 1x/2x/3x WebP/PNG/GIF assets. The missing piece is not rendering; it is **channel identity**. BTTV does not accept a login, and `RecordingView` carries only `StreamerLogin`, not the numeric ID.

Conditions:

1. **Owner accepts third-party CDN image loads.** Browser-side BTTV images reveal each viewer's IP and user agent to NightDev's CDN. A server-side proxy would hide that but is a much larger change. Recommended mitigation: `referrerpolicy="no-referrer"`, feature flag off by default, and a documented caveat.
2. **Owner accepts replay-time catalog drift.** Channel emote sets change and emotes are renamed or removed. Rendering the current catalog against an old recording is approximate. A capture-time snapshot (Option C) is the only way to preserve the historical mapping, but it is deferred; it still does not preserve image bytes.
3. **Static images only in v1.** Animated BTTV WebP/GIF assets are 0.9–3.8 MB each in the measured sample, versus 1–11 KB for static images. The pane rebuilds its 300-row DOM on visible-set changes, so animated images would restart repeatedly and cost far too much.
4. **Keep BTTV optional and non-gating.** A BTTV failure must never affect chat text, native emotes, recording, or health checks.

## 2. What the repository already fixes

| Fact | Evidence |
|---|---|
| The pane renders native emotes from stored code-point ranges in `appendText`, using `https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/{1.0,2.0,3.0}`. It validates the native ID and sets `alt`, `title`, `loading="lazy"`, and `decoding="async"`. | `src/StreamKeepr/Components/Features/Chat/ChatPane.razor.js:446-484`, especially `:465-473` |
| The pane caps rendered rows at 300, polls every 500 ms, and rebuilds the log with `replaceChildren()` when the visible message set changes. | `ChatPane.razor.js:11,18,353-386` |
| The module is fetch-injectable: `createChatPane(root, { fetch, storage })`; the existing tests already exercise native emote rendering. | `ChatPane.razor.js:76-80`; `tests/js/chat-pane.test.js:95-116` |
| The IRC parser converts the `emotes` tag into `ChatEmote(id, ranges)` and stores ranges as Unicode code points. ACTION messages subtract an 8-code-point prefix offset. | `src/StreamKeepr/Infrastructure/Twitch/Chat/TwitchChatParser.cs:14-15,51-61,196-242` |
| Persisted messages carry `text` plus native `emotes: [{id, ranges:[{from,to}]}]`; there is no third-party catalog in the session. | `src/StreamKeepr/Infrastructure/Storage/ChatStorageModels.cs:5-33` |
| The public chat endpoint is windowed (default 300, max 1000; max 600 s) and `no-store`. | `src/StreamKeepr/Hosting/ChatEndpoint.cs:12-24`; `docs/backend-ui-contract.md:127-132` |
| A session has a channel login in `StoredChatMeta.Channel`; `RecordingView` has `StreamerLogin`. Neither carries the numeric Twitch ID. | `ChatStorageModels.cs:41-59`; `src/StreamKeepr/Ui/LibraryViewModels.cs:26-42` |
| The numeric ID is persisted only in the watched-channel catalog (`.streamkeepr/channels.json`), not per session. Helix `/users?login=` can resolve it at replay time. | `src/StreamKeepr/Infrastructure/Storage/ChannelStore.cs:9-14,130-152`; `src/StreamKeepr/Infrastructure/Twitch/TwitchStreamPoller.cs:112-125` |
| v1 deliberately deferred third-party emotes. This memo revisits that decision. | `docs/research/twitch-chat-replay.md:577` |

## 3. BetterTTV API surface

### 3.1 Endpoints, authentication, and key

| Endpoint | Purpose | Authentication |
|---|---|---|
| `GET https://api.betterttv.net/3/cached/emotes/global` | All global emotes; returns an array of emote objects. | None. Official docs: "No key is needed, and any origin can call it." |
| `GET https://api.betterttv.net/3/cached/users/twitch/{providerId}` | Channel catalog; returns `{id, bots, avatar, channelEmotes, sharedEmotes}`. | None for this cached endpoint. |
| `GET https://api.betterttv.net/3/emotes/{id}` | Individual emote lookup. | Returned `403 {"message":"unauthorized"}`; not usable without auth. |

`{providerId}` is the **numeric Twitch user ID**, not the login. Probes: `twitch/71092938`, `twitch/22484632`, and `twitch/37402112` returned `200`; `twitch/xqc`, `twitch/forsen`, and `twitch/shroud` returned `404 {"message":"user not found"}`. There is no public bulk lookup or login-to-ID endpoint in the documented cached surface.

### 3.2 Observed HTTP behavior

| Request | Status | Key headers / result |
|---|---|---|
| `GET /3/cached/emotes/global` | `200` | `content-type: application/json`; 9,485 bytes; 65 emotes; `cache-control: max-age=300`, `stale-while-revalidate=600`; no `Access-Control-Allow-Origin` without an `Origin` request header. |
| Same GET with `Origin: http://192.168.1.10:8080` | `200` | `access-control-allow-origin: http://192.168.1.10:8080`; `access-control-allow-credentials: true`; `vary: Origin`. A LAN-origin browser fetch is allowed. |
| `OPTIONS` preflight with `Access-Control-Request-Method: GET` | `403` | Preflight is not supported. A direct `fetch` must remain a simple GET with no non-safelisted headers. |
| `GET /3/cached/users/twitch/121059319` | `200` | 64,223 bytes; 107 `channelEmotes` + 249 `sharedEmotes`; same `max-age=300` caching. |
| `GET /3/cached/users/twitch/xqc` | `404` | `{"message":"user not found"}`; confirms the login is not the key. |

A valid numeric ID can also return `200` with empty `channelEmotes`/`sharedEmotes` (e.g. `71092938`); treat that as a channel with no BTTV profile, not a failure.

No REST rate limit is published in the API documentation, and no `RateLimit-*` headers appeared in the probes. Treat the limit as **unverified** and cache aggressively: global and per-channel catalogs change on a five-minute cache boundary, not per message. The API reflects the origin and sets `Access-Control-Allow-Credentials: true`; keep requests credential-free (no cookies, `credentials: "omit"` if the browser ever calls it directly).

### 3.3 Response schema and trimmed samples

Global:

```json
[{"id":"54fa8f1401e468494b85b537","code":":tf:","imageType":"png","animated":false,"modifier":false},
 {"id":"54fab45f633595ca4c713abc","code":"M&Mjc","imageType":"png","animated":false,"modifier":false,"width":36,"height":30}]
```

Channel (trimmed): the response adds `id`, `bots`, `avatar`, `channelEmotes`, and `sharedEmotes`; a shared emote may carry `codeOriginal` (e.g. `code:"DUUR"`, `codeOriginal:"FeelsTastyMan"`).

| Field | Meaning and use |
|---|---|
| `id` | 24-character hex emote ID; use it to build CDN URLs, never for display. Validate it before use. |
| `code` | The text token chatters type; this is the match key. |
| `codeOriginal` | Present on aliased shared/personal emotes; the original code. **Do not match on it**; BTTV's own client maps `code`. |
| `imageType` | Deprecated; `gif` for animated, otherwise `png`. Use `animated` instead. |
| `animated` | Whether an animated asset exists. |
| `modifier` | Global only; marks a modifier emote (nine codes currently: `w!`, `h!`, `v!`, `z!`, `c!`, `l!`, `r!`, `p!`, `s!`). Do not render standalone. |
| `width`, `height` | Present only when the 1x image is not square. Useful for aspect ratio, not required. |
| `restrictions` | Schema allows an emote-set restriction (e.g. Night subscriber emotes). Current global payload had none; skip restricted entries unless a viewer identity exists. |

## 4. BetterTTV CDN

Official URL patterns from the API docs and the client's `cdn.js`:

| Kind | URL |
|---|---|
| Static or animated WebP | `https://cdn.betterttv.net/emote/{id}/{size}.webp` |
| PNG (still frame for animated) | `https://cdn.betterttv.net/emote/{id}/{size}.png` |
| GIF (animated) | `https://cdn.betterttv.net/emote/{id}/{size}.gif` |
| First frame of an animated emote | `https://cdn.betterttv.net/emote/{id}/static/{size}.webp` |

`size` is `1x`, `2x`, or `3x`.

| Probe | Result |
|---|---|
| Static emote `54fa.../1x.webp` | `200 image/webp`, 1,064 bytes; `cache-control: public,max-age=15552000,immutable` (180 days). |
| Animated emote `566c.../1x.webp` | `200 image/webp`, **920,280 bytes**, 35×35. |
| Animated emote `566c.../2x.png` | `200 image/png`, 10,687 bytes, 70×70 (still). |
| Animated emote `566c.../3x.gif` | `200 image/gif`, **3,774,663 bytes**. |
| Animated emote `566c.../static/2x.webp` | `200 image/webp`, 6,668 bytes (still). |
| `54fa.../static/1x.webp` (static emote) | `404`; the `/static` path exists only for animated emotes. Branch on `animated`. |
| Unknown ID `ffff.../1x.webp` | `404 text/html`, 111 bytes; no cache-control. |

The CDN sent **no `Access-Control-Allow-Origin`** header, including when `Origin` and `Referer` were supplied. That does not block `<img>` display, but it blocks `fetch`, canvas readback, and other CORS-dependent uses. No hotlink protection was observed. Deleted-emote behavior was not directly testable; assume the same `404` path and fall back to text. Cache headers are strong and immutable, so 2x/3x assets are cheap after first load; animated first-load bytes are the problem.

For `srcset`, use `1x/2x/3x` density descriptors. The assets are not always exactly 28/56/84 px (the animated sample is 35/70/140), but the descriptors express intended density. The pane's CSS fixes rendered height at 28 px (`src/StreamKeepr/wwwroot/css/components.css:297`).

## 5. Matching semantics

BTTV's own client is the best primary reference. Its matcher:

- reads only `span[data-a-target="chat-message-text"]` (`src/modules/chat/index.js:147`); it does not select native `<img>` emotes directly, so native emotes remain native in BTTV's own client;
- splits each text node on a single space: `const parts = data.split(' ');` (`:321`), then looks up each part in a case-sensitive `Map` (`:352`);
- stores emotes with `this.emotes.set(code, ...)` in both global and channel modules (`src/modules/emotes/global-emotes.js:39-58`; `channel-emotes.js:52,77,121`);
- builds the channel map from `channelEmotes.concat(sharedEmotes)`, so a later duplicate wins (`channel-emotes.js:115`);
- resolves categories in priority order: personal, BTTV channel, BTTV global, then FFZ/7TV (`src/modules/emotes/index.js:19-29,70-90`);
- renders a modifier only as a transform of an adjacent emote, never as a standalone image (`chat/index.js:346-406`).

Consequences for StreamKeepr:

| Case | Correct rule |
|---|---|
| Case | **Case-sensitive exact match.** A sampled channel's shared set contained both `Brazil` and `BRAZIL`; case-folding would collapse two distinct emotes. |
| Punctuation | Punctuation is part of the token. The global set contains `:tf:`, `M&Mjc`, and `D:`; the nine modifier codes end in `!`. Do not strip punctuation or use a word-boundary regex. |
| Tokenization | Split on spaces, not generic punctuation or Unicode word boundaries; preserve exact spacing when rebuilding. BTTV's own matcher cannot match a code adjacent to punctuation without a space. |
| Adjacent emotes | `KappaKappa` is one token and does not match; `Kappa Kappa` matches twice. There is no longest-match or overlap problem with exact space-delimited tokens. |
| Alias | Match `code`, not `codeOriginal`. |
| Precedence | Twitch native first (protect stored ranges), then BTTV channel (shared overrides channel on duplicate), then BTTV global. |
| Native collision | If a BTTV code equals a Twitch native code, the native range wins because it is rendered from the IRC tag and is never re-tokenized. |
| Segment boundaries | Do not concatenate text across a native emote range. The native emote sat between those characters in the original message, so joining "foo" + native emote + "bar" into "foobar" can create a false token. |
| Modifiers | Defer. Skip `modifier:true` entries so `w!` stays text rather than becoming a lone image. BTTV data has no separate zero-width overlay field; `z!` is the zero-space modifier. `w! Kappa` will render `w!` text plus a normal Kappa, not a wide Kappa. |
| Unicode | Native ranges are code points (`TwitchChatParser` uses `Rune`; the pane uses `Array.from`). Keep that. Never index with `string.length` or `charAt`. Sampled BTTV codes were ASCII, but the tokenizer should still operate on code-point arrays for consistency. |
| XSS | Treat all catalog data as untrusted: validate IDs (`/^[a-f0-9]{24}$/i`), cap code length, and use `textContent`/attributes only. Never build `innerHTML` from `code`. |

## 6. Replay fidelity

BTTV channel emote sets are mutable. The docs' WebSocket events include `emote_create`, `emote_update` (rename or alias change), and `emote_delete`, and explicitly warn that the events do not cover every change. The REST cache is five minutes. A replay-time catalog therefore shows the channel's **current** set, not the set at capture time. The WebSocket is unnecessary for replay; fetch on pane open and accept five-minute staleness.

| Approach | Historical fidelity | Works offline | Scope | Failure mode |
|---|---|---|---|---|
| Replay-time global + channel fetch (recommended) | Current set; old codes may disappear; reused codes may render on old messages | Catalog no; images already require the CDN | Small | BTTV down → text/native only |
| Capture-time snapshot per session (Option C) | Capture-time mapping for future sessions | Catalog yes; images no | Medium/large | Capture-time fetch failure → no BTTV for that session |
| Snapshot plus image bytes | Full historical fidelity | Yes | Much larger | Disk/bandwidth/privacy |

A session-level snapshot is **not warranted for v1**. It would add a capture-time external dependency and a new `chat/` file, and it still cannot guarantee deleted images. It becomes warranted if the owner treats "an old message's emote is now a different emote or plain text" as a defect. If pursued, store `chat/bttv.json` with `{v, channelId, fetchedAt, global, channel}`; fetch best-effort at session open; never gate recording; old sessions fall back to replay-time or no BTTV.

Channel renames are a related limitation. The numeric ID is stable, but a session stores only the old login. `ChannelStore` can resolve the ID while the channel remains watched; Helix can resolve a current login. A removed or renamed channel may lose its channel emotes on old recordings. BTTV **global** emotes still work because they need no ID. Persisting the numeric ID in `chat/meta.json` is a smaller future fix than a full catalog snapshot, but it is still an additive schema change; it is not in the current `RecordingSummary`.

## 7. Terms, licensing, and privacy

- **Code license:** the `night/betterttv` repository is MIT-licensed (`LICENSE`), but that covers the extension code, not the emote artwork users upload.
- **Terms of Service:** the ToS says: "Content found on or through this Service are the property of NightDev, LLC or used with permission. You may not distribute, modify, transmit, reuse, download, repost, copy, or use said Content, whether in whole or in part, for commercial purposes or for personal gain, without express advance written permission from us." A private-home, non-commercial deployment is not obviously prohibited, but the clause is broad. No explicit attribution requirement was found in the API docs or ToS. This is a risk to flag, not legal advice.
- **Privacy:** the Privacy Policy says Usage Data may include IP address, browser type, and visit times. Browser-side BTTV image loads necessarily disclose the viewer's IP and user agent to NightDev's CDN, plus the referrer allowed by the browser's policy. `referrerpolicy="no-referrer"` removes the referrer but not IP/UA. A server-side image proxy would hide viewer IPs from the CDN at the cost of server bandwidth, disk cache, and abuse surface.
- **Existing baseline:** the pane already hotlinks `static-cdn.jtvnw.net` for native emotes (`ChatPane.razor.js:467`), so replay already depends on a third-party CDN. BTTV adds a second third party and reveals which BTTV emotes are viewed; it does not create the first outbound dependency.

## 8. Integration options

| | A: browser fetch | B: server catalog endpoint (recommended) | C: capture-time snapshot |
|---|---|---|---|
| Catalog source | Browser → `api.betterttv.net` | StreamKeepr server → BTTV, browser → StreamKeepr | Capture → BTTV, stored in session |
| Numeric ID | Must be added to the pane payload; pane cannot derive it | Server resolves from `ChannelStore`, then Helix | Server resolves at capture |
| CORS | Works for a simple GET (verified), but preflight returns 403 | None; same-origin | None |
| Caching | Browser HTTP cache only | Central 5-min TTL, stale fallback, single-flight | Per-session, permanent |
| Kill switch | Config read by JS; no central enforcement | Endpoint returns empty when disabled | Snapshot presence is the switch |
| Privacy | Viewer IP to BTTV API and CDN | Server IP to BTTV API; viewer IP still to CDN | Server IP to BTTV only at capture; viewer IP to CDN at replay |
| Offline LAN | Catalog fails | Catalog can be stale/empty; chat unaffected | Catalog works; images still fail |
| Old/renamed channels | ID may be unresolvable | ChannelStore/Helix best effort; global always works | Fixed for future sessions; old sessions lack snapshot |
| Scope | Deceptively small; still needs ID plumbing | New endpoint + cache + tests | New file + capture plumbing + contract change |

**Recommendation: Option B-lite.** Add `GET /api/recordings/{id}/emotes/bttv` that resolves the recording's channel login to a numeric ID (`ChannelStore` first, `IStreamPoller.GetUserAsync` fallback), fetches global + channel catalogs server-side with a bounded 3 s timeout (including the Helix lookup) and no credentials, normalizes to `{id, code, animated, modifier, width?, height?}`, and caches global and per-channel entries in memory for five minutes with stale fallback and single-flight. If the ID cannot be resolved, skip the channel request and still return global. The response is `{v, available, stale, fetchedAt, global, channel}` and is `Cache-Control: no-store`; the server cache is the cache, so the kill switch stays authoritative. When disabled or unreachable it returns `200` with `available:false` and empty arrays, never an error that blocks chat. The client fetches it once per recording open through the existing injected `fetch`, then matches only text outside native ranges.

Why not A: the pane has no numeric ID. `RecordingView` carries only `StreamerLogin`, and `StoredChatMeta.Channel` is a login. Adding the ID to the payload is itself a server change, after which A still has per-viewer API calls, no central cache, and no central kill switch. Why not C: it optimizes historical fidelity the owner has not asked for, at the cost of capture-time coupling and a schema change. A global-only client-side spike (no server change) is a valid one-day rendering experiment, but it omits channel emotes and should not be the shipped scope.

Failure behavior for B-lite:

| Failure | Behavior |
|---|---|
| BTTV `404` or `200` with empty arrays (channel has no profile) | Cache an empty channel set for five minutes; global still renders. |
| BTTV `429`/`5xx`/timeout | Serve stale cache if present; otherwise `available:false`; chat and native emotes unaffected. |
| CDN `404` | Replace the failed `<img>` with its code as text; no retry. |
| Offline LAN | Endpoint returns stale/empty; CDN images fail; native CDN images also fail; chat text remains. |
| Schema change / oversized body | Drop invalid entries, cap counts and code lengths, cap response bytes, log once, continue. |
| Rate limiting | Five-minute cache, single-flight, and a bounded per-channel cache make the request volume negligible for a private home. |

## 9. Performance against the existing pane

| Item | Value |
|---|---|
| Global catalog | 65 emotes, 9,485 bytes JSON. |
| Sample channel catalog | 356 emotes, 64,223 bytes JSON. |
| Static image | 1–3 KB at 1x; 2x/3x larger but still small. |
| Animated image | 920 KB at 1x WebP; 3.77 MB at 3x GIF; 6.7 KB static first frame at 2x WebP. |
| Rendered rows | ≤300 (`MAX_ROWS`); the pane rebuilds all rows on visible-set changes (`replaceChildren`). |
| Polling | 500 ms; catalog is fetched once per recording open, not per poll. |
| Initial image load | `loading="lazy"` limits loading to the viewport (roughly 20–30 rows), then scroll expands it. Worst case remains 300 image elements. |

At 300 rows, a static BTTV emote costs 1–11 KB each; a viewport with 30–60 images is comfortably sub-megabyte. Animated emotes are **not viable** at these sizes, especially because every render recreates `<img>` elements and restarts animation. Recommendation: static only; use `/static/{size}.webp` for `animated:true` and `/{size}.webp` for static. Add `loading="lazy"`, `decoding="async"`, and `referrerpolicy="no-referrer"`. Do not add a per-image retry loop. If animated support is ever wanted, gate it behind a separate flag, a hover/click action, `prefers-reduced-motion`, and a visible-emote cap.

## 10. Testability and observability

The existing `fetch` seam makes B-lite testable without a browser or network:

- **Pure matcher.** Extract a pure `matchEmotes(text, nativeRanges, catalog)` helper (or equivalent) and test it in `tests/js/`: exact case (`Brazil` vs `BRAZIL`), `:tf:`/`M&Mjc`/`D:`, adjacent tokens, multiple spaces, leading/trailing spaces, native-range protection, native/BTTV collision, channel-over-global precedence, `codeOriginal` ignored, invalid IDs, action text, and Unicode before an emote.
- **DOM tests in `tests/js/chat-pane.test.js`.** Fake fetch routes the chat URL and catalog URL; assert catalog fetched once per recording, chat still renders when the catalog is `available:false`, BTTV images have the expected `src`/`srcset`/`alt`/lazy/async/referrerpolicy, and a dispatched `error` replaces the image with text. Assert no catalog fetch when the flag is off or the pane is closed.
- **C# unit tests.** `BttvCatalog` with a fake `HttpMessageHandler` and `FakeTimeProvider`: normalization, five-minute expiry, stale fallback, 404 negative cache, 429/5xx/timeout, disabled flag, invalid ID, body-size cap, and no `Authorization`/cookie headers. No real network.
- **C# integration tests.** `WebApplicationFactory` with the fake handler: endpoint shape, unknown recording `404`, disabled/empty `200`, `available:false` on BTTV failure, and cache headers. The Mock profile should serve a fixed catalog so E2E and CI never call BTTV.
- **Observability.** Return `available`, `stale`, `fetchedAt`, and a coarse `reason`; log one warning per failed refresh with channel ID and status, never the response body. Do not add a health check: an optional emote source must not affect readiness.
- **Kill switch.** `Chat:BetterTtv:Enabled=false` by default in `ChatOptions`/runtime config; the endpoint returns empty and the client skips the fetch. If animated support is later added, gate it behind a separate flag.

## 11. Contract amendments and open owner decisions

### 11.1 Contract amendments

| Contract | Change |
|---|---|
| `docs/backend-ui-contract.md` | New public `GET /api/recordings/{id}/emotes/bttv`; response `{v, available, stale, fetchedAt, global, channel}`; `200` with empty arrays when disabled/unreachable; `Cache-Control: no-store`; unknown recording `404`. |
| `docs/hosting-contract.md` | Endpoint-map row; document the outbound HTTPS dependency on `api.betterttv.net` and `cdn.betterttv.net`; no media allowlist change; no `chat/` exposure. |
| `docs/frontend-foundation-contract.md` | `ChatPane.razor.js` additionally owns optional third-party matching/rendering; image attributes; static-first policy; no layout change. |
| `docs/testing-contract.md` | Vitest matcher/DOM tests; xUnit catalog/cache tests with fake HTTP; `WebApplicationFactory` endpoint tests; Mock/E2E catalog fixture; no external network. |
| `docs/runtime-contract.md` | Add `Chat:BetterTtv:Enabled=false`, `CacheSeconds=300`, and `TimeoutSeconds=3` to the `Chat:` table. |
| `docs/session-store-contract.md` | **No change for B-lite.** Option C would add `chat/bttv.json` and define its lifecycle/deletion. |
| `docs/decision-digest.md` / `docs/adr/` | Optional ADR for the third-party CDN/privacy dependency if adopted. |

### 11.2 Open owner decisions

| Decision | Recommendation |
|---|---|
| Accept browser image loads to `cdn.betterttv.net` (viewer IP/UA visible to NightDev), or require a server image proxy? | Accept for the private-home deployment, with `referrerpolicy="no-referrer"` and the feature off by default. Proxy is a separate, larger project. |
| Accept replay-time catalog drift, or fund a capture-time snapshot? | Accept for v1; revisit if old-recording fidelity becomes a reported defect. |
| Default the feature on or off? | Off; owner enables `Chat:BetterTtv:Enabled`. Consistent with the existing `Chat:Enabled=false` posture. |
| Animated emotes? | No in v1; static first frame only. Revisit with a hover/click policy and a cap. |
| Scope: BTTV only, or FFZ/7TV too? | BTTV only; the same server endpoint can add providers later, but each has its own API/CDN and terms. |
| Terms/non-commercial use | Owner confirms the private-home, non-commercial use is acceptable. This memo is not legal advice. |

Deferred beyond v1: modifier stacking (`w!`, `z!`, etc.), animated preferences, BTTV personal emotes (requires per-user lookup/broadcast), FFZ and 7TV, emote tooltips/click-through, image proxy, capture-time snapshots, and numeric-ID persistence in session metadata.

## 12. Sources

All fetched 2026-11-14 unless noted.

1. BetterTTV API documentation: https://betterttv.com/developers/api
2. BetterTTV global emotes endpoint: https://api.betterttv.net/3/cached/emotes/global
3. BetterTTV channel emotes endpoint (sample numeric ID): https://api.betterttv.net/3/cached/users/twitch/121059319
4. BetterTTV individual emote endpoint (403 probe): https://api.betterttv.net/3/emotes/54fa8f1401e468494b85b537
5. BetterTTV chat matcher source: https://github.com/night/betterttv/blob/master/src/modules/chat/index.js
6. BetterTTV global emote source: https://github.com/night/betterttv/blob/master/src/modules/emotes/global-emotes.js
7. BetterTTV channel emote source: https://github.com/night/betterttv/blob/master/src/modules/emotes/channel-emotes.js
8. BetterTTV category priority source: https://github.com/night/betterttv/blob/master/src/modules/emotes/index.js
9. BetterTTV CDN helper source: https://github.com/night/betterttv/blob/master/src/utils/cdn.js
10. BetterTTV emote renderer source: https://github.com/night/betterttv/blob/master/src/modules/emotes/emote.jsx
11. BetterTTV modifier CSS: https://github.com/night/betterttv/blob/master/src/modules/chat/style.css
12. Animated emote sample (static first frame): https://cdn.betterttv.net/emote/566ca38765dbbdab32ec0560/static/1x.webp
13. Static emote sample: https://cdn.betterttv.net/emote/54fa8f1401e468494b85b537/1x.webp
14. BetterTTV Terms of Service: https://betterttv.com/terms
15. BetterTTV Privacy Policy: https://betterttv.com/privacy
16. BetterTTV repository license: https://github.com/night/betterttv/blob/master/LICENSE
17. Twitch Helix Get Users: https://dev.twitch.tv/docs/api/reference/#get-users
18. MDN `<img loading>`: https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/img#loading
19. MDN `<img srcset>`: https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/img#srcset
20. MDN `<img referrerpolicy>`: https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/img#referrerpolicy
21. MDN `Cache-Control`: https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Cache-Control
