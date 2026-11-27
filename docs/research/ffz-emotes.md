# Research memo: FrankerFaceZ (FFZ) emotes in chat replay

Status: research memo, 2026-11-26; probes 2026-11-27 UTC (2026-11-26 local).
Issue: #345, child of #344. Research only; no production code, configuration, contracts, ADRs, tests, or dependencies changed.
Binding: no. ADR 0011 wins and excludes FFZ, so adoption needs an addendum.

**Method.** Credential-free `curl` probes to the FFZ API, CDN, and website; v1 Swagger; client v4.83.2 at `be6c12d`; Terms and Privacy Policy. No token, cookie, or authenticated call. Samples: Global catalog, xQc `71092938`, 30 popular logins (28 returned data); Rubius (99 emotes) is the large channel. Repository facts are from `origin/main` at `2368ece`.

## 1. Verdict

**Technical: GO.** Endpoints are unauthenticated, shapes normalize, and no endpoint, schema, matcher, or session change is needed; only a numeric-ID rule, 1x/2x/4x image-set construction, a config switch, and docs.

**Product: conditional GO.** FFZ's Global catalog is small (11 non-modifier emotes; 9 after the `hidden` policy in §4); channel catalogs (up to 99; KEKW/OMEGALUL) are the value. Add only if the owner accepts the artwork terms and `cdn.frankerfacez.com`; otherwise NO-GO.

## 2. API surface

`GET /v1/set/global`, `GET /v1/room/id/{twitchUserId}`, and `GET /v1/room/{login}` are unauthenticated and documented. `71092938` and `/room/xqc` returned identical 996-byte `200` payloads with `application/json`, `cache-control: private,must-revalidate,stale-if-error=60`, `etag`, `x-ffz-cache: hit`, and `ratelimit-limit: 120`. Trimmed room shape:

```json
{"room":{"twitch_id":71092938,"id":"xqc","is_group":false,"set":166907},
 "sets":{"166907":{"title":"Channel: xQc","emoticons":[
  {"id":246878,"name":"WideHard","public":true,"hidden":false,"modifier":false,
   "urls":{"1":"...","2":"...","4":"..."}}]}}}
```

Missing room: `404` `{"status":404,"error":"Not Found","message":"No such room"}` (59 bytes, also `/room/id/1` and `/999999999`). A room with no emotes is `200`: shroud (`37402112`) returned 379 bytes, one set, `emoticons: []`.

`/set/global` returned `200`, **20,223 bytes**, `application/json`, `cache-control: private,must-revalidate,stale-if-error=60`, `etag`, `x-ffz-cache: hit`, `ratelimit-limit: 120`. `default_sets` `[3,1539687]`; `sets` had three keys: 3 "Global Emotes" (11), 1532818 "Subwoofer Emote Effects" (8 modifiers, user-scoped), 1539687 "Emote Effects" (4 modifiers). Only 3 and 1539687 are default; 1532818 appears under `users` (hundreds of IDs). `/set/global/ids` returns the same sets/defaults with `user_ids`.

Rooms carry `twitch_id`, `id` (login), `is_group`, and `set`; `sets` maps ID to contents. All 28 returned rooms had one set titled `Channel: <name>` and no Global set ID. FFZ's client loads `room.set` plus every `sets` entry (`room.js:374-380`), so normalize all values. `is_group` was always false; no group route is documented, so ignore it.

## 3. Payload shape

```json
{"default_sets":[3,1539687],"sets":{
 "3":{"id":3,"_type":1,"title":"Global Emotes","emoticons":[
  {"id":9,"name":"ZrehplaR","public":false,"hidden":true,"modifier":false,"modifier_flags":0,
   "urls":{"1":".../9/1","2":".../9/2","4":".../9/4"}}]},
 "1539687":{"id":1539687,"_type":2,"title":"Emote Effects","emoticons":[
  {"id":723890,"name":"ffzW","modifier":true,"modifier_flags":9}]}},
 "users":{"1532818":[123456,...]}}
```

Room shape mirrors it: `room{twitch_id,id,is_group,set}`, `sets{id,_type,title,emoticons}`; an animated emote adds `"animated":{"1":".../animated/1","2":"...","4":"..."}`, while static emotes omit it.

Fields: `id` is an integer (validate as positive ASCII digits before it becomes a path segment); `name` is the exact-case code (3–16 ASCII chars, no spaces/punctuation in 521 sampled entries). `public` means freely addable elsewhere, not chat visibility. `hidden` hides an emote from menus; FFZ still matches it in chat but replaces the code with `???` in the token text (`emotes.js:2378`, `tokenizers.jsx:1901`). `modifier`/`modifier_flags` are effect data v1 skips (flags cover Hidden, FlipX/Y, GrowX, Rainbow, HyperRed/Shake, Cursed, Jam, Bounce). `animated` and `urls` are DPI-scale URL maps: `animated` is not a boolean and is absent on static emotes; `urls` always has `1`, may have `2`/`4`. `height`/`width` are 1x dimensions, `status` is always 1, and `owner`/`artist` are metadata.

## 4. Set selection and filtering

| Topic | v1 decision |
|---|---|
| Global sets | Read `default_sets` only; ignore user-scoped and non-default sets. |
| Modifiers | Skip `modifier:true`; do not implement `modifier_flags` effects. |
| `public` | Include; it controls addability elsewhere, not chat visibility. |
| `hidden` | Skip in v1; FFZ matches them but masks the code as `???`. |
| Duplicates | First-wins by exact code; channel scope before Global catalog. |

- **Use `default_sets` only.** FFZ's client treats them as globally usable and non-default sets as sub-sets or user-scoped (`emotes.js:2165-2208,1790-1799`). `sets` also contained 1532818, a user-scoped effect set under `users`; exposing it to everyone would be wrong. Read each `default_sets` ID present in `sets`, ignore `users`/`user_ids`, skip a missing default set. Empty `default_sets` is an empty Global catalog, not a failure.
- **Skip modifiers.** All 12 effect entries have `modifier:true`; Global set 3 has none. After modifier filtering, FFZ's Global catalog contributes **11 entries**; skipping the two `hidden` easter eggs leaves **9**. Do not implement `modifier_flags` effects in v1; they require stacking on a target emote.
- **Do not filter `public`; skip `hidden` in v1.** All 11 Global catalog entries are `public:false`, and two sampled channel entries were also false (`nmpDab` 235700, `YEP` 446565). `public` controls addability elsewhere, not rendering; filtering it would empty Global set 3. Two Global catalog easter eggs are `hidden:true` (`ZrehplaR` 9, `ZliL` 28138); FFZ matches them but masks the code as `???` in chat. `EmoteCatalogEntry` carries only the code, so including them would expose the real code in `alt`/`title`. Skipping them is the closest parity with FFZ and drops only two easter eggs; including them is an owner decision.
- **Scopes and duplicates.** Channel sets exclude Global sets; the service merges the channel scope before the Global catalog, matching FFZ (`emotes.js:1790-1799`). In 28 rooms/510 channel entries, no duplicate IDs appeared, but exact duplicate codes did: TheGrefg `PepeLaugh` (64785, 263833) and Hasanabi `Weirdge` (675552, 411818); Rubius has case-distinct `pepeLaugh`/`PepeLaugh`. First-wins dedupe absorbs the exact pairs; do not assert per-set uniqueness.

## 5. Image CDN

| Test | Result |
|---|---|
| KEKW `/emote/381875/{1,2,4}` | 200 PNG, 2,452/7,367/22,830 bytes, 32×32/64×63/128×126; `max-age=86400`, ETag, `ACAO:*`, `cf-cache-status: HIT` |
| OMEGALUL `/128054/1`, KEKL `/407009/1`, animated `/724419/{1,2,4}` still | 200 PNG, 2,434/2,502 bytes; stills 1,990/4,255/892 at 37×32/74×64/148×128; same headers |
| Animated `/724419/animated/{1,2,4}`; unknown `/999999999/1` | 200 WebP, 7,732/11,222/14,378 at 37×32/74×64/148×128; unknown 404 JSON 60 bytes, `max-age=86400` |

- **Shapes:** static `https://cdn.frankerfacez.com/emote/{id}/{1|2|4}` (PNG); animated `.../emote/{id}/animated/{1|2|4}` (WebP). `.webp`/`.gif` on an animated URL returns 200; `.png` returns 400; any extension on a static URL returns 400. Use the API keys as density descriptors (1x/2x/4x, not 3x); dimensions are not exact multiples (KEKW 64×63 at 2x). Rebuild these templates from a validated ID; do not echo the API's absolute URLs.
- **Cache/CORS/hotlinking:** one-day `max-age=86400` with ETag/Last-Modified, not immutable (BTTV 180 days, 7TV one year); `ACAO:*`, `GET, OPTIONS`, no `Set-Cookie`. No-`Referer`, LAN-`Referer`, and `Origin` requests all returned 200; keep the pane's no-CORS `<img>` posture.
- **Deleted emotes (inferred, not verified):** no confirmed deleted ID was available. Early-ID gaps 2, 5, 7, 8, 10, 11 return 404 from both `/v1/emote/{id}` and the CDN, as does an unknown ID. Treat deletion as the 404 path; a recently deleted image or 404 may stay cached for a day. The pane already replaces a failed image with its code and never retries. See the residual gaps in §8.

## 6. Terms, privacy, attribution

- **API use/rate:** the Terms allow "providing FFZ emotes to external applications related to Twitch services" and say FFZ may change or stop the API. Unauthenticated limit is 120 points/IP/min, 1 per endpoint; headers are `ratelimit-limit/-remaining/-reset`, `Retry-After` on 429. The 5-minute cache makes volume negligible; 429 was not exercised.
- **Artwork/privacy:** the Terms forbid "downloading, hosting, modifying, or redistributing content hosted on FFZ servers without explicit permission from both FFZ and the original owner(s) of the content." A browser `<img>` load is literally a download, though not hosting/redistribution; the API clause permits "providing FFZ emotes to external applications related to Twitch services", but it is not a license to the artwork. Accepting the display is an owner decision, not a technical conclusion. The Privacy Policy lists IP/UA, pages, times, devices, and cookies; CDN loads disclose viewer IP/UA to FFZ/Cloudflare. The pane sends no-referrer; no CDN `Set-Cookie` was observed. Not legal advice.
- **Comparison with ADR 0011:** BetterTTV and 7TV were accepted under the same private-home posture: unauthenticated API, server-side catalog, browser CDN, viewer IP/UA disclosure, no image proxy, no-referrer images, and owner acceptance of terms/artwork rights. FFZ adds a third API/CDN dependency with an equivalent privacy exposure. Its artwork clause is stricter than the BTTV/7TV terms: the API clause permits Twitch-related external applications, but the artwork clause forbids downloading/redistributing without permission from both FFZ and the owner. The owner must explicitly accept that incremental risk.
- **Attribution:** none required; the API supplies `owner`/`artist`, but v1 needs no UI (`EmoteCatalogEntry` has no metadata). FFZ's client is Apache-2.0, but that covers code, not artwork or API.

## 7. Fit with the seam

- **Provider/config:** add `FfzEmoteProvider` (`Id="ffz"`), `ChatEmoteOptions.Ffz` + `Chat:Emotes:Ffz:Enabled=false` (`appsettings.json`; the Mock profile keeps its fixed BTTV-only provider), `EmoteHttpClients.Ffz` base `https://api.frankerfacez.com/`; register after 7TV. Registration order gives BTTV > 7TV > FFZ; resolver/store unchanged.
- **Blocking gap:** add `IsValidFfzEmoteId` (positive ASCII digits, 1–20; current IDs are small integers, not a fixed six digits) to `EmoteCatalogRules.IsUsable`; otherwise `Normalize` drops every FFZ entry (`EmoteCatalogRules.cs:51-53`, `EmoteCatalogService.cs:296-301`). The rule repeats `IsValidChannelId`'s numeric shape: extract a private bounded-numeric predicate with two named wrappers rather than reusing `IsValidChannelId`, whose contract is Twitch user IDs.
- **Images:** `ForSizes` hardcodes 1x/2x/3x (`EmoteCatalog.cs:48-50`); construct `new EmoteImageSet(src, srcset)` directly in `FfzEmoteProvider.GetImageSet`, leaving `ForSizes` for BTTV/7TV. No endpoint/pane/layout change: `safeSrcSet` accepts any HTTPS `srcset` (`ChatPane.razor.js:1280-1288`), CSS fixes height at `2em` (`components.css:673`), and `EmotesEnabled` already covers any provider (`ChatPane.razor:209`).
- **Aggregation/normalization:** no service change; it already orders the channel scope before the Global catalog within a provider, preserves provider order, dedupes first-wins by exact code, and applies the 5-min cache, 3-s budget, and stale fallback. The provider must use the shared body reader (`EmoteResponseBody`/`ResponseBodyReader`) for the 4 MiB cap; `EmoteCatalogService` does not enforce it. Parse `default_sets`/`sets` globally and `room`/`sets` per channel; room 404 → empty, Global 404 → failure; skip `modifier:true`; set `Animated` from `animated["1"]`; use available size keys; no set fallback; credential-free.

**Assumptions that do not hold:** FFZ's Global catalog is small, not a large source; `animated` is a URL map, not a boolean; `public` means addable and `hidden` means hide-from-menus plus a `???` chat label (neither filters chat, but hidden is not an exact drop-in); FFZ is 1x/2x/4x, not 3x; the CDN is one-day cacheable, not immutable; channel sets exclude Global sets and `default_sets` is not all of `sets`; FFZ numeric IDs fail `IsUsable`.

## 8. Cost and recommendation

FFZ's Global catalog is ~20 KB; the largest sampled channel is ~52 KB (Rubius, 99 emotes, ~0.5 KB/emote vs 7TV's ~2.4), giving ~110 entries and a ~13 KB endpoint response. Static images are 2–29 KB; sampled animation is 7.7–14.4 KB. Existing cap, cache, budget, and stale fallback cover it. Implementation is one provider (~150–200 lines), validator, image-set construction, config, tests, docs; no new dependency, schema, or pane change. One to two days.

**Recommendation: conditional GO.** Add FFZ third, registered after 7TV, with BTTV > 7TV > FFZ. Keep `Chat:Emotes:Animated=false` (FFZ animation is cheap, but the switch also enables BTTV's large assets). Serve static `urls` by default and `animated` only when on. Apply the §4 filtering table; the only deliberate divergence is skipping `hidden` easter eggs. No proxy, snapshot, per-provider animation switch, or attribution UI in v1. If the artwork terms are unacceptable, NO-GO; the Global catalog alone does not justify the dependency.

**Minimum docs/ADR/contract updates:** (1) ADR 0011 addendum reversing the exclusion and recording dependencies/terms; (2) `backend-ui-contract.md:142` provider list/order and FFZ URL shapes; (3) `runtime-contract.md:36-38` add `Emotes:Ffz:Enabled=false`; (4) `hosting-contract.md:66` add both FFZ hosts; (5) `GLOSSARY.md:144,160` add FFZ and numeric ID shape; (6) `testing-contract.md:15-16,170` add FFZ unit/integration coverage (no Mock/E2E change required); (7) `decision-digest.md` add a row for the FFZ provider decision under #344, cross-linking #345; (8) `README.md` optional-emotes list and enable keys.

**Owner decisions.** Reverse ADR 0011's exclusion? Yes if FFZ channel sets are wanted. Accept `cdn.frankerfacez.com` loads (viewer IP/UA, no referrer, no proxy)? Yes under private-home. Accept artwork terms? Yes only explicitly; otherwise NO-GO; not legal advice. Attribution? None in v1. Hidden easter eggs? Recommend skip in v1. Priority? After BTTV/7TV, animation switch off by default.

**Residual verification gaps.** Deleted-emote behavior is inferred from unknown/early-ID 404s; no confirmed deleted ID was available. The unauthenticated 429 path was not exercised, group rooms were not observed, and BTTV/7TV catalog overlap with FFZ was not measured. The deleted-emote acceptance criterion is therefore partial; the 404 path is the same one the pane already handles.

## 9. Sources

1. FFZ docs: <https://api.frankerfacez.com/docs/>; Swagger: <https://api.frankerfacez.com/v1/swagger.json>.
2. Probes: `/v1/set/global`, `/v1/room/id/71092938`, `/v1/room/xqc`, `/v1/room/id/999999999`; images `/emote/381875/1`, `/emote/724419/animated/1` on `cdn.frankerfacez.com`.
3. FFZ client `be6c12d`: <https://github.com/FrankerFaceZ/FrankerFaceZ/blob/be6c12ddb70e6ebcf09f07bc3789fecc554f8a40/src/modules/chat/emotes.js> (also `tokenizers.jsx`, `room.js`, `LICENSE`); Terms: <https://www.frankerfacez.com/terms>; Privacy: <https://www.frankerfacez.com/privacy>.
4. Sibling memos `docs/research/betterttv-emotes.md`, `docs/research/7tv-emotes.md`; ADR 0011; MDN `srcset`.
