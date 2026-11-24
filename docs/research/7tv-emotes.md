# Research memo: 7TV emotes in chat replay

Status: research memo, 2026-11-24.
Issue: #272, opened 2026-11-23. This memo settles the API questions that issue left open; the binding decisions are in `docs/adr/0011-third-party-emotes-provider-seam.md` (addendum) and the implementation brief on the issue.
Binding: no. Where this memo and the ADR differ, the ADR wins.

**Method.** 7TV facts come from live, credential-free `GET`/`HEAD` requests to `api.7tv.app`, `7tv.io`, and `cdn.7tv.app` on **2026-11-24**, plus the 7TV API documentation page. Repository facts come from the working tree at the implementation branch. No 7TV account, token, or cookie was used. Probes use Twitch user ID `71092938` (xQc) as the large-channel sample and the global emote set as the small one.

Conditions and decisions recorded here were accepted by the owner on 2026-11-24: the `cdn.7tv.app` browser dependency, 7TV's terms and artwork rights under the private-home, non-commercial posture, and API v3 as the read surface.

## 1. Verdict

**Feasible under the existing seam.** Both read endpoints answer unauthenticated, the catalog shapes fit the existing normalized entry, and no endpoint, response shape, client matcher, or schema change is needed. Three provider-specific details drive the implementation: the static file naming, the emote-ID shape, and the payload size.

## 2. API surface

| Endpoint | Purpose | Authentication |
|---|---|---|
| `GET https://api.7tv.app/v3/emote-sets/global` | Global catalog | None |
| `GET https://api.7tv.app/v3/users/twitch/{twitchUserId}` | Channel; returns `emote_set_id` and usually the inline `emote_set` | None |
| `GET https://api.7tv.app/v3/emote-sets/{setId}` | One emote set by ID; the fallback when the user payload omits `emote_set` | None |

`{twitchUserId}` is the numeric Twitch user ID, not the login. `71092938` returned `200`. `1` returned `404 {"status":"Not Found","error_code":12000,"error":"user not found"}`.

The docs name `https://api.7tv.app/` as the API URL. The same v3 routes also answer on `https://7tv.io/` with identical payloads; this change uses the documented host and no other v3 host is depended on.

### 2.1 v4 is not a usable read surface

The documentation lists V3, V4, GQL, and EventAPI. What the v4 REST surface actually answers:

| Request | Result |
|---|---|
| `GET /v4/emote-sets/global` | `404 route not found` |
| `GET /v4/emote-sets/{id}` | `404 route not found` |
| `GET /v4/emotes/{id}` | `404 route not found` |
| `GET /v4/users/{id}` | `404 route not found` |
| `GET /v4/users/twitch/{id}` | `404 route not found` |
| `GET /v4/emotes` | `405` (route exists, method not allowed) |

V4 GQL exists and is documented, but it is not a REST catalog and its schema is described as unfinished. After the v3 checks: no deprecation, sunset, or warning header appeared; `x-ratelimit-global-limit: 5000` with a 45-second window was the only governance signal. **v3 is the only read surface that serves global and channel catalogs, and it works today.**

## 3. Observed payloads

| Request | Result |
|---|---|
| `GET /v3/emote-sets/global` | `200`, 45 emotes, 117,986 bytes |
| `GET /v3/users/twitch/71092938` | `200`, 969 emotes in the inline set, **2,380,529 bytes** |
| `GET /v3/emote-sets/01FE9DRF000009TR6M9N941CYW` | `200`, the same set without the user envelope |

Sizes are the reason the shared 2 MiB response cap had to change: the largest sampled channel exceeds it by 13%. Each entry carries a deep graph (`owner`, `owner.connections`, tag lists), so the channel payload is roughly 2.4 KB per emote.

### 3.1 Shape that matters

```json
{
  "id": "71092938",
  "emote_set_id": "01FE9DRF000009TR6M9N941CYW",
  "emote_set": {
    "id": "01FE9DRF000009TR6M9N941CYW",
    "emotes": [
      {
        "id": "01G3WEGZN0000ET2J0MQP5YJ0G",
        "name": "GAMBA",
        "flags": 0,
        "data": {
          "id": "01G3WEGZN0000ET2J0MQP5YJ0G",
          "name": "GAMBA",
          "flags": 0,
          "animated": true,
          "state": ["LISTED"]
        }
      }
    ]
  }
}
```

- **`name` at the set-entry level is the code viewers type.** It differs from `data.name` for renamed emotes: 154 of the 969 entries in the sampled channel, and 4 of the 45 global entries (`(7TV)` for `7tvM`, `sevenTV` for `7tv`, `nanaAYAYA` for `AYAYA`, `7Cinema` for `Cinema`). Reading the nested name would break roughly one emote in six.
- **`flags` means different things at the two levels.** A set entry's bit 0 is zero-width (28 of 969 channel entries, 3 of 45 global). The nested emote's flags only ever carried `0` or `256` (animated); bit 0 never appeared there. Zero-width detection must read the set entry.
- **`animated` lives in `data`.** It decides which image file name is valid: the still frame of an animated emote, or the animated asset itself.
- **`state` (`LISTED`, `PERSONAL`, `NO_PERSONAL`) is a listing state, not a viewer restriction.** 676 channel entries were `PERSONAL` and 81 `NO_PERSONAL`; the sampled sets were usable. Unlike BetterTTV's `restrictions`, no entry is dropped for having a state.
- No entry had a null `data`, an empty name, or a space in the name. Names were 2–77 characters, ASCII only. The cap in force is 128 characters.
- One set can list the **same emote ID twice** under one code (3 cases: `Listening`, `o7`, `retardJAM`). Aggregated first-wins dedupe absorbs it; nothing should assert per-set uniqueness.

### 3.2 Emote IDs

7TV emote and emote-set IDs are 26-character ULIDs. Crockford's base32 alphabet (digits and uppercase minus `I`, `L`, `O`, `U`) is a subset of `[0-9A-Z]`, so the implementation accepts the superset: it cannot reject a real ID, and it still pins length and alphabet so the ID remains one path segment of a fixed template. The 7TV-specific rule rejects a BetterTTV-shaped 24-hex ID, while the shared usability check — which re-runs over every provider's entries — accepts either provider's shape.

## 4. CDN behavior

| Asset | Status | Bytes | Headers |
|---|---|---|---|
| `/emote/{id}/1x.webp` (animated) | `200` | 13,620 | `image/webp`, `access-control-allow-origin: *`, `cache-control: public, max-age=31536000, s-maxage=86400, immutable` |
| `/emote/{id}/2x.webp` (animated) | `200` | 35,988 | same |
| `/emote/{id}/3x.webp` (animated) | `200` | 65,068 | same |
| `/emote/{id}/1x_static.webp` (animated, first frame) | `200` | 890 | same |
| `/emote/{id}/2x_static.webp` | `200` | 2,434 | same |
| `/emote/{id}/3x_static.webp` | `200` | 4,356 | same |
| `/emote/{id}/1x_static.webp` on a non-animated emote | `404` | — | — |

- **Static-first uses the `_static` suffix**, not a path folder. `1x.webp` on an animated emote is the 15-frame animation. A non-animated emote has no `_static` file, so the URL shape is chosen from `animated`.
- The CDN sends `access-control-allow-origin: *`, unlike BetterTTV's CDN. `crossorigin="anonymous"` would therefore work for 7TV, but the pane does not set it: the accepted posture is an ordinary no-CORS `<img>` load, and provider cookies remain an accepted residual risk (ADR 0011).
- The image response is immutable for a year, so a wrong URL is expensive to discover in production; the URL shape is covered by tests instead.
- **Animation is a second URL shape, not a second pipeline.** With `Chat:Emotes:Animated` on, both providers return the animated asset (`1x.webp` for 7TV, the folder-less path for BetterTTV); with it off, 7TV returns the still frame and BetterTTV returns its `static/` path. The pane needed no change. BetterTTV's animated assets are still the 0.9–3.8 MB measured in the sibling memo, which is why the switch defaults to off.

## 5. Cost at replay time

Normalized to the endpoint's entry shape, the sampled large channel with both providers enabled produced **1,027 entries in 390,613 bytes** (≈381 bytes per entry; 971 from 7TV, 56 from BetterTTV after cross-provider dedupe). The pane fetches this once per recording load and builds a code map from it.

Accepted consequence: the fetch grows to roughly 390 KB for the largest channels, and the provider work now includes a body that is up to four times the old cap. Both are bounded by the existing five-minute cache and three-second budget; a timeout degrades to stale or global-only exactly as before. No entry cap was added — a cap would become a correctness bug the first time a legitimate channel exceeded it.

## 6. Terms, licensing, and privacy

- 7TV's API documentation states the API license is Apache 2.0 with Commons Clause; the terms of service and privacy policy are linked from it.
- The browser discloses each viewer's IP and user agent to `cdn.7tv.app`. No server-side image proxy was added, matching the BetterTTV decision and its stated reason (bandwidth, disk, and abuse surface).
- Private-home, non-commercial use is accepted by the owner, not legally cleared. Not legal advice.

## 7. Resulting implementation constraints

1. Read the set-entry `name` and `flags`; read `data.animated`.
2. Follow `emote_set_id` only when `emote_set` is absent, and validate the ID before it becomes a path segment.
3. Build static URLs as `.../{size}.webp` or `.../{size}_static.webp` from `animated`.
4. Accept 26-character `[0-9A-Z]` IDs in the shared usability check alongside BetterTTV's 24 hex.
5. Treat a channel `404` (no profile, or a removed set) as an empty channel catalog; treat a follow-up set failure as a channel-scope failure.
6. Keep the response cap shared at 4 MiB.
7. Serve the animated asset only when `Chat:Emotes:Animated` is enabled, and keep the static rendering as the default.

## 8. Sources

- 7TV API documentation — <https://7tv.app/api/docs>
- Live probes on 2026-11-24: `https://api.7tv.app/v3/emote-sets/global`, `https://api.7tv.app/v3/users/twitch/71092938`, `https://api.7tv.app/v3/emote-sets/01FE9DRF000009TR6M9N941CYW`, and the `cdn.7tv.app` asset paths in §4.
- `docs/research/betterttv-emotes.md` for the sibling provider's probes and the shared seam rationale.
