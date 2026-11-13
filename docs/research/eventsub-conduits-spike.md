# Spike: EventSub conduits for Stream Keepr

Status: research memo, decision pending (2026-11-13)
Issue: #131 "Spike: research EventSub conduit requirements and StreamKeepr viability"
Binding: no. This memo informs a decision; it changes no contract. Adoption would amend `docs/twitch-auth-contract.md` and ADR-0003, which needs owner approval.

**Method.** Facts about Twitch come only from the official Twitch developer docs, the docs changelog, and one Twitch Developer Forums announcement. All were fetched on 2026-11-13; links are in §7. No live Twitch API was called and no credentials were used. Code was read with plain `read`/`grep`: no Serena MCP tools were available in this session (the `MCP_DOCKER` server failed to connect). Repository citations are `path:line` at `55b8bee`.

## 1. Summary and recommendation

**Recommendation: GO, conditional on a timeboxed prototype (≤ 1 day) and two owner decisions.** Conduits are generally available, use the app access token that `TwitchTokenProvider` already obtains, and support `stream.online`/`stream.offline`. They raise the push ceiling from **5 channels** (user-token WebSocket, cost budget 10) to about **5,000 channels** (app-token cost budget 10,000, documented only by example) with both events subscribed. They also decouple push from admin sign-in.

Conditions:

1. **Prototype confirms four facts the docs leave open** (§6.2): the app can create a conduit; the app-token `max_total_cost` returned by Twitch; that a WebSocket shard is not capped by the user-token WebSocket limits (300 subscriptions, cost 10, 3 connections); and what a shard does after `session_reconnect`.
2. **Owner decision: push no longer depends on admin sign-in.** Today logout stops EventSub (`docs/twitch-auth-contract.md:72`, `:167`). With conduits, push continues after logout. This changes contract §5, §7, §11 and ADR-0003, so it is a high-impact change under AGENTS.md.
3. **Owner decision: the UI reason `No admin token`** (`src/StreamKeepr/Infrastructure/Twitch/PollSchedule.cs:30`, `docs/backend-ui-contract.md:94`) is never emitted in conduit mode. Keep it for WebSocket mode or retire it.
4. **Ship behind a transport flag** (`WebSocket` default → `Conduit`), with polling unchanged as the safety net. Conduits do not replay events (§3.6).

If the owner watches 5 or fewer channels, the capacity gain is irrelevant. The remaining benefit is push without an admin sign-in, weighed against new persistent Twitch-side state (a conduit and subscriptions that outlive the process).

## 2. Availability and auth

| Question | Answer | Source |
|---|---|---|
| Available to any app, or gated? | Generally available since **2024-01-25**. No docs page or the launch announcement mentions an approval or eligibility gate. **To verify:** the prototype's first `POST /helix/eventsub/conduits` with this app's client credentials returns `200`. A `401` means a token or client problem, and a `429` means the 5-conduit cap. | [changelog][changelog], [announcement][announce], [Create Conduits][api-create] |
| Token type | An app access token is required to create conduits, manage shards, and create, list, and delete conduit subscriptions. A user token must not be used. | [conduit guide §Authorizing][c-auth], [Create EventSub Subscription][api-sub-create], [Delete][api-sub-delete] |
| Does `TwitchTokenProvider` already do client credentials? | **Yes.** It posts `grant_type=client_credentials` (`src/StreamKeepr/Infrastructure/Twitch/TwitchTokenProvider.cs:33-38`), caches the token, refreshes at `max(60 s, expires_in − 300 s)` (`:51`), and `Invalidate(stale)` drops a rejected token (`:62-66`). The poller already uses it with a 401 → invalidate → retry-once rule (`src/StreamKeepr/Infrastructure/Twitch/TwitchStreamPoller.cs:191-195`). `ClientSecret` is already required config (`src/StreamKeepr/Configuration/TwitchOptions.cs:11`). | code |
| Scopes | **None needed.** `stream.online` and `stream.offline` say "No authorization required." The client-credentials flow takes no scope parameter. | [stream.online][ref-online], [stream.offline][ref-offline], [client credentials][oauth-cc] |
| 401 / refresh | App tokens **cannot be refreshed**. On a 401, get a new token through the same flow. That is exactly `Invalidate` plus `GetAsync`. | [refresh tokens][refresh], [auth §tokens don't last forever][auth-expiry] |
| Hourly validation | The docs require validation "when it starts and on an hourly basis" for apps that maintain an OAuth session. They do not say whether this covers app tokens. **UNKNOWN (low risk):** the poller already makes app-token Helix calls today without `/validate`; a conduit adds nothing new here. | [validate tokens][validate] |
| `conduit` transport for `stream.online`/`offline` | **Yes.** Both list `webhook`, `websocket`, and `conduit` as transport methods, and the request example includes `"method": "conduit"` with `conduit_id`. | [stream reference][ref-stream] |
| 5 enabled conduits / 20,000 shards | Confirmed: "five enabled conduits with a maximum of 20,000 shards per conduit", subject to change. `shard_count` is 1–20,000. Creating a sixth returns `429`. | [conduit guide §Limits][c-limits], [Create Conduits][api-create] |
| What counts as an "enabled" conduit? | **Not defined.** The only clue: a conduit whose shards are all disabled "will remain enabled" until it is deleted after 72 h. So a leaked conduit counts against the cap for up to 72 h after its last shard drops, and until explicitly deleted if any shard is enabled. **UNKNOWN:** whether the cap counts all conduits that exist or only some. Resolution: the prototype creates and deletes conduits and reads `Get Conduits`. | [conduit guide §Handling disabled shards][c-disabled] |

Docs inconsistency to note: the guide says reducing `shard_count` **disables** the highest shards ([§Scaling][c-scaling]); the API reference says it **deletes** them ([Update Conduits][api-update]). This does not matter for a single-shard design.

## 3. Capacity, cost, and delivery

### 3.1 Cost rules

- Conduit subscriptions "use the same cost model EventSub has today" ([§Limits][c-limits]).
- A subscription type that names a user but needs no authorization from that user, such as `stream.online`, **costs 1**. It **costs 0** if that user has authorized the app ([manage §Subscription limits][m-limits]).
- In Stream Keepr, watched channels are other people's broadcasters, so assume **unauthorized: 2 per channel** for both events. The admin's own channel may be free, because the admin has authorized the app with no scopes. **UNKNOWN (minor):** whether a zero-scope authorization counts. The docs' worked example uses a user who granted a scope.
- App-token `max_total_cost`: **10,000 appears only in example responses**, not as a stated rule. The same page states WebSocket `max_total_cost` is 10 and does not reconcile the two ([manage §Subscription limits][m-limits], [§WebSocket limits][m-ws]). **UNKNOWN:** the actual value for this client ID. Resolution: read `max_total_cost` from the first conduit `Create EventSub Subscription` response ([API][api-sub-create]).
- Twitch allows **3 subscriptions per `type` + `condition`** per client ([manage §Subscription limits][m-limits]). Two Stream Keepr instances sharing one client ID would each subscribe, which is still within the cap.

### 3.2 Capacity arithmetic

Let `U` be the number of unauthorized watched channels, `A` the number of authorized ones, and `B` the cost budget.

| Scenario | Formula | User-token WebSocket (B = 10) | App token + conduit (B = 10,000, assumed) |
|---|---|---|---|
| Both events, all unauthorized | `2U ≤ B` | U ≤ **5** | U ≤ **5,000** |
| `stream.online` only, all unauthorized | `U ≤ B` | U ≤ 10 | U ≤ 10,000 |
| Both events, mixed | `2U + 0·A ≤ B` | U ≤ 5, A unbounded by cost | U ≤ 5,000, A unbounded by cost |
| Example: A = 1 (admin), U = 40 | `2·40 = 80` | 80 > 10 → 35 channels poll | 80 ≤ 10,000 → full coverage |

Dropping `stream.offline` is **not recommended**. Contract §4 defines coverage as both subscriptions (`docs/twitch-auth-contract.md:53`), and offline push is what shortens the gap before a recording finalizes.

### 3.3 Per-connection limits

The 300 enabled subscriptions per connection and 3 WebSocket connections limits are stated **per user token (client ID, user ID)** ([WS §Subscription limits][ws-limits], [manage §WebSocket limits][m-ws]). The conduit pages never mention them ([§Limits][c-limits], [conduits API][api-conduits]).

- **UNKNOWN: does 300 per connection apply to a WebSocket shard?** If it did, the cap would be 150 channels per shard (both events), and 5,000 channels would need 34 shards. Hashing would still distribute them unevenly. For realistic Stream Keepr sizes (tens of channels), the answer does not change the design: one shard is enough.
- **UNKNOWN: does the 3-connection limit apply to shard sockets?** With 1–2 shards this is irrelevant. Keep `shard_count ≤ 3` until it is measured.
- Resolution: the prototype subscribes more than 300 subscriptions (e.g. 160 channels × 2, cost 320) on a 1-shard conduit and checks for `429` and shard status. This step needs a real client ID and costs budget. It is optional if no deployment expects more than 150 channels.

### 3.4 Helix rate limits

- One token bucket for app-token requests per **client ID**, and one per client ID per user for user tokens. A `429` comes with `Ratelimit-Reset`. The points-per-minute figure is **not stated**: 800 appears only as an example header ([API guide §How it works][rl-how], [§Keeping track][rl-track]).
- There are no documented per-endpoint limits for conduit, shard, or subscription endpoints. Endpoints can return `429` "for reasons unrelated to the general rate limit bucket" ([§How it works][rl-how]). Known non-bucket 429s: the 5-conduit cap ([Create Conduits][api-create]), and the cost, duplicate, and WebSocket limits on subscription create ([API][api-sub-create]).
- Update Conduit Shards accepts at most 100 shards per request ([API][api-shards-update]). There is no batch subscription create: N channels cost 2N requests.
- **Behavior change:** today, subscription creates draw on the admin's user bucket. After migration they share the **app bucket with the poller** (`TwitchStreamPoller` uses `TwitchTokenProvider`). Polling is 1 request per 100 logins per tick (`docs/twitch-auth-contract.md:36`), so this is negligible at Stream Keepr scale. The client must still distinguish a bucket 429 (wait for `Ratelimit-Reset`) from a cap 429 (do not retry; mark uncovered).
- **UNKNOWN:** the actual app bucket size. Resolution: log `Ratelimit-Limit` in the prototype.

### 3.5 Shard topology

- **Minimum viable:** 1 conduit, 1 WebSocket shard (`shard_count = 1`, shard id `"0"`). Shard IDs start at 0 ([§Creating a conduit][c-create]).
- **Recommended once proven: 2 shards (two WebSockets).** A notification for a disabled shard is resent to *another* shard, and dropped only if that shard is also disabled ([§Failure scenarios][c-failure]). With one shard, every reconnect or keepalive gap drops events. With two, one socket covers the other's reconnect. Both share a process, so a restart still drops events, and polling covers that.
- Webhook shards are out of scope. They need a public HTTPS callback on port 443 ([Update Conduit Shards][api-shards-update]), which a self-hosted LAN install usually lacks.

### 3.6 Delivery semantics

| Topic | Documented behavior | Source |
|---|---|---|
| Event → shard mapping | "a hashing mechanism to determine which shard notifications for a particular channel ID will be delivered on". The mapping is per broadcaster and opaque. | [§Adding subscriptions][c-subs] |
| Disabled shard | Resent once to another shard. If that shard is also disabled, the event is **dropped**. An active webhook shard's failure is not resent. | [§Failure scenarios][c-failure] |
| Ordering | **Not documented** for EventSub or conduits. **UNKNOWN.** A resend can reorder `online`/`offline` for one channel. The existing 10 s burst poll after a notification (`EventSubClient.cs:439`) confirms state through Helix. | [EventSub overview][es-dupes] |
| Duplicates | Delivery is at-least-once, and a resent message keeps its `message_id`. Twitch suggests rejecting a `message_id` already seen or a `message_timestamp` older than 10 minutes. | [EventSub §Handling duplicate events][es-dupes] |
| Replay | **None.** "No replay of events that are lost" while reconnecting. The conduit guide documents dropped notifications and no replay mechanism. | [WS guide][ws], [§Failure scenarios][c-failure] |
| `conduit.shard.disabled` | Version 1. Requires an app token, condition `client_id` (required) and `conduit_id` (optional). The event carries `conduit_id`, `shard_id`, `status`, and `transport`. **UNKNOWN:** its cost, and whether it can be delivered over the conduit it reports on. With one shard it cannot reach us when that shard is down, so it adds little until there are 2 shards. | [conduit.shard.disabled][ref-shard-disabled] |
| Shard recovery | Update the shard with a new WebSocket session ID. Subscriptions do not need to be recreated. | [§Handling disabled shards][c-disabled] |
| Welcome → shard association | The client has **10 seconds** after `session_welcome` to assign the session to a shard, or Twitch closes the connection. | [§Assigning transports][c-assign] |
| 72-hour deletion | If all shards stay disabled and none is reactivated within 72 h, EventSub deletes the conduit. Its subscriptions become `conduit_deleted`. | [§Handling disabled shards][c-disabled], [manage §status][m-status] |
| Message formats | Notifications and revocations look the same on any shard. `payload.subscription.transport` is `{"method":"conduit","conduit_id":…}`. WebSocket framing (`session_welcome`, `session_keepalive`, `session_reconnect`, `notification`, `revocation`, close codes 4000–4007) is unchanged. Revocation statuses are `user_removed`, `authorization_revoked`, `version_removed`, and `notification_failures_exceeded` (webhook only). | [§Event notifications][c-notif], [§Revoking][c-revoke], [WS guide][ws] |
| Subscriptions after shard socket replacement | They **survive**. Recovery works "without needing to recreate every subscription". They are disabled only when the conduit is deleted. | [§Handling disabled shards][c-disabled], [§Adding subscriptions][c-subs] |
| `session_reconnect` on a shard | **Not documented.** For plain WebSockets, the new URL "carries the same subscriptions", and the client keeps the old socket open until the new one sends its welcome. **UNKNOWN:** whether the shard follows the reconnect automatically or needs a new `PATCH …/conduits/shards`. Resolution: in the prototype, call `Get Conduit Shards` before and after a reconnect. Defensive design: after any welcome, compare the shard's `session_id` with ours and re-PATCH within 10 s if they differ. | [WS §Reconnect message][ws-reconnect], [Get Conduit Shards][api-shards-get] |
| Clean shutdown | Nothing is required. Closing the socket disables the shard, and the conduit and subscriptions persist for 72 h ([§Handling disabled shards][c-disabled]). To remove all Twitch-side state, call `DELETE /helix/eventsub/conduits?id=` (`204`). Subscriptions then disable, possibly with delay ([Delete Conduit][api-delete]). | |

**What polling must still cover** (unchanged from contract §4): startup until reconciliation finishes, any shard-disabled window, every channel without both subscriptions, and dropped or out-of-order events. Keep the all-or-nothing `IsPushConnected` rule, the 300 s push interval, and the 10 s burst.

## 4. Stream Keepr fit

### 4.1 What changes in `EventSubClient`

| Today (user-token WebSocket) | Conduit mode |
|---|---|
| Token from `IAdminTokenStore.GetAsync` (`EventSubClient.cs:48`). No token → `NoAdminToken` and wait (`:70-95`). | Token from `TwitchTokenProvider.GetAsync`. It is always obtainable while `ClientSecret` is valid. An auth outage maps to `Reconnecting`. |
| On welcome, subscribe each channel with `transport = websocket, session_id` (`:231-247`, `:328`). | On welcome, `PATCH /conduits/shards` (shard 0 → session_id) **within 10 s**, then *reconcile*: list subscriptions with the `conduit_id` filter ([Get Subscriptions][api-sub-get]), create missing ones with `transport = conduit, conduit_id`, and delete subscriptions for channels no longer watched. |
| `EventSubTransport(method, session_id)` (`TwitchModels.cs:46`). | The DTO needs `conduit_id`. Conduit and shard DTOs are added to `TwitchJsonContext`. |
| Socket loss disables every subscription. Each session re-subscribes everything. | Subscriptions persist on the conduit. Reconnect only re-associates the shard. Restarts are cheap and no re-subscribe storm hits the budget. |
| `session_reconnect`: throw, dispose the old socket, connect the new URL (`:256-265`, `:170`). **Finding:** this closes the old socket *before* the new welcome, against Twitch's guidance ([WS §Reconnect][ws-reconnect]). The spike reported it without fixing it; it has since been fixed in PR #141. | Same handoff, done correctly: keep the old socket until the new welcome, then verify the shard association. |
| `revocation` → restart the whole session (`:267-271`). | Revocation affects one subscription. Mark that channel uncovered (`PartialCoverage`) and do not restart. Re-create it at the next reconcile; `user_removed` will keep failing and stay polling-only. |
| Channel add/remove or token change → restart the session (`:103-104`). | Channel change → reconcile only (create or delete the delta). `IAdminTokenStore.Changed` is no longer an input. |
| `409` counts as active (`:343-344`, contract §5). | **Risk:** the 409 body names the existing subscription ID ([API][api-sub-create]). **UNKNOWN:** whether "matching" includes the transport. If not, a leftover subscription on an old conduit or a WebSocket would look active while delivering elsewhere. Resolution: on 409, look up that ID and accept it only if `transport.conduit_id` equals ours, otherwise delete it and re-create. |
| 401/429/5xx → `Failed` → restart with backoff (`:346-356`). | 401 → `Invalidate` and retry once, matching the poller. Bucket 429 → wait for `Ratelimit-Reset`. Cap 429 (cost, duplicate, 5 conduits) → mark uncovered, no tight retry. Create Conduit 429 → surface an "EventSub conduit limit reached" log. 5xx → backoff. Backoff (`:147-150`) and keepalive (`:180-191`) are unchanged. |
| No `message_id` dedupe (`:377-440`). | Add a small bounded `message_id` set, shared across shards (10-minute window per [Twitch guidance][es-dupes]). This is needed once there are 2 shards, because resends cross shards. |

`IEventSubClient.RunAsync` (`IEventSubClient.cs:6`) can stay as it is, so `DvrSupervisor` and `MockEventSubClient` are unaffected.

### 4.2 Admin-token dependency

- **EventSub would no longer need `IAdminTokenStore`.** Contract §1 says "The admin token is used **only** for EventSub subscriptions" (`docs/twitch-auth-contract.md:17`).
- `IAdminTokenStore` would still back:
  - admin sign-in, which saves the token (`src/StreamKeepr/Infrastructure/Twitch/AdminAuth.cs:309`);
  - logout, which clears it (`AdminAuth.cs:128-129`);
  - `TwitchAdminSessionValidator`, which validates the admin session against `/oauth2/validate` every 30 minutes (`src/StreamKeepr/Infrastructure/Twitch/TwitchAdminSessionValidator.cs:21-31`, `src/StreamKeepr/Program.cs:179`).
- Removing the store or changing what sign-in saves is a **separate decision**. Do not bundle it with this migration. ADR-0003's "EventSub runs only while a token is available" (`docs/adr/0003-admin-token-auth.md:13`) would need a superseding note.
- The admin sign-in comment "cannot drive EventSub" (`AdminAuth.cs:314-315`) becomes stale.

### 4.3 Persistence and cleanup

- **Persist only the conduit ID**, e.g. `/recordings/.streamkeepr/eventsub-conduit.json`, next to `admin-tokens.json` (contract §7). Record the `ClientId` it was created under; the ID is not a secret.
- Shard assignments are always shard 0 and 1, so they need no persistence.
- Subscription IDs are derivable: list them with the `conduit_id` filter ([API][api-sub-get]). Do not store copies (AGENTS.md §2.2).
- **On startup:**
  1. Load the conduit ID.
  2. `GET /conduits` and check that it exists. It is gone after 72 h of downtime.
  3. If it is missing or belongs to a different `ClientId`, create a new conduit and persist it.
  4. Connect, PATCH the shard, reconcile.
- **Orphans:** `Get Conduits` lists every conduit for the client ID, and conduits carry no name or tag ([API][api-get]). Never auto-delete conduits Stream Keepr did not create: another instance (dev and prod) may share the client ID. Leaked conduits from a lost state file expire 72 h after their shards drop. Until then they count toward the cap of 5.
- **Logout:** no EventSub effect in conduit mode (owner decision 2, §1).
- **Shutdown:** close the sockets only, so a restart within 72 h resumes without re-subscribing.
- **Transport flag switched back to WebSocket:** delete our conduit (`204`) and the state file.
- **Credential change:**
  - Same `ClientId` with a new secret: nothing to do.
  - New `ClientId`: the old conduit can't be deleted with new credentials. Log it, drop the state file, and let the old conduit expire.

### 4.4 Coverage, health, and UI mapping

- **Coverage:** `IsPushConnected` stays all-or-nothing. Its definition becomes "every watched channel has enabled `stream.online` **and** `stream.offline` on our conduit, **and** shard 0 is enabled". Contract §4/§5 wording changes; the rule does not.
- **`PushStatus` reasons** (`PollSchedule.cs:27-34`):
  - `EventSub connected`, `Partial coverage`, `EventSub reconnecting`, and `Polling only` map one-to-one.
  - `No admin token` is unreachable in conduit mode (owner decision 3).
  - The UI shows `push.Reason` (`src/StreamKeepr/Ui/ChannelsView.cs:161`). Reason strings are pinned by `tests/StreamKeepr.Tests/Twitch/PollScheduleTests.cs:124-128`.
- **Health and intervals:** no change. `DvrHealthCheck` already keys off `IsPushConnected` (`src/StreamKeepr/Hosting/DvrHealthCheck.cs:30-32`), and the poll intervals stay in `TwitchOptions` (`TwitchOptions.cs:25-27`).

### 4.5 Test seams

- **Reuse:** `IEventSubSocketFactory`/`IEventSubSocket` (`IEventSubClient.cs:10-21`) for shard sockets, and the `HttpMessageHandler` fakes and `FakeEventSubSocket` already in `tests/StreamKeepr.Tests/Twitch/EventSubClientTests.cs:448`, `:516-534`. `FakeTimeProvider` covers the 10 s association window and backoff.
- **Add:**
  - a fake Helix conduit API covering conduits, shards, and subscriptions, with stateful `conduit_id`-filtered listing, 409 with an ID, 429 variants, and `404` for a deleted conduit;
  - a fake conduit state store.
- **Testable offline:** reconcile create/delete deltas, 409 ownership check, 401 invalidate/retry, a missing conduit after a restart, shard re-PATCH on a session mismatch, revocation → partial coverage, `message_id` dedupe, and the transport flag.
- **Not testable without real credentials:** the real `max_total_cost`, a possible approval gate, per-shard limits, reconnect behavior on shards, resend/drop timing and ordering, hashing, 409 matching semantics, and bucket size. Prototype only; never in CI.

## 5. Migration outline

**Minimum first slice** (one PR behind `Twitch:EventSubTransport = Conduit`, default `WebSocket`):

- One conduit and one WebSocket shard.
- Persist the conduit ID, associate the shard on welcome, reconcile both events for watched channels.
- 409 ownership check and 401 retry-once.
- Coverage semantics unchanged.
- Deferred: second shard, `conduit.shard.disabled`, dedupe beyond a simple set, UI reason changes.

| Area | Files |
|---|---|
| Push client | `src/StreamKeepr/Infrastructure/Twitch/EventSubClient.cs`. Split into transport-agnostic coverage and routing, plus a WebSocket session and a conduit session. |
| Helix conduit API (new) | e.g. `src/StreamKeepr/Infrastructure/Twitch/EventSubConduitApi.cs`, using `TwitchTokenProvider` and the `TwitchHttpClients.Helix` client |
| Conduit state (new) | e.g. `src/StreamKeepr/Infrastructure/Twitch/ConduitStateStore.cs` |
| DTOs | `TwitchModels.cs:37-48`, `TwitchJsonContext.cs:13` |
| Config and wiring | `TwitchOptions.cs` (transport flag), `Program.cs:122-125` |
| Status | `PollSchedule.cs` (only if the reasons change) |
| Unaffected | `MockEventSubClient.cs`, `DvrHealthCheck.cs`, `DvrSupervisor` |
| Docs | `docs/twitch-auth-contract.md` §1, §5, §7, §11, §12; `docs/backend-ui-contract.md:94`; ADR-0003 superseding note or a new ADR-0006 |
| Tests | `tests/StreamKeepr.Tests/Twitch/EventSubClientTests.cs` plus new conduit tests |

**Effort** (rough): prototype 0.5–1 day; first slice 3–4 days including tests and contract edits; hardening (2 shards, dedupe, shard-status checks) 1–2 days. **Total about 1–1.5 weeks.**

**Rollout:**

1. Prototype against a throwaway client ID.
2. Merge the slice with the flag off.
3. The owner enables it on their instance and watches `Partial coverage` / `EventSub connected` and the logs for a week.
4. Flip the default in a later release.
5. Retire WebSocket mode only after a release with no regressions. Its removal is a separate change.

**Rollback:** set the flag back to `WebSocket`. On startup in WebSocket mode, delete the persisted conduit if one exists, which disables its subscriptions and frees the cost budget. If that call fails, the conduit expires 72 h after its shards drop. Polling covers every gap, so rollback carries no recording-integrity risk.

**If no-go:** the blocking constraint would be one of the prototype findings: an approval gate, an app-token budget far below 10,000, or a per-shard cap of cost 10. The fallback would be to keep the WebSocket with polling and document the 5-channel push ceiling. Webhook shards (public HTTPS) are the only other push path that scales.

## 6. Risks, unknowns, prototype plan

### 6.1 Risks

- **Persistent Twitch-side state.** Conduits and subscriptions outlive the process. Losing the state file leaks a conduit for up to 72 h, and 5 leaks block creation (`429`).
- **Shared client ID** across instances shares the 5-conduit cap, the cost budget, and `Get Conduits` results. Never delete conduits we did not create.
- **Single shard drops events** during every reconnect. Polling hides the gap, but push latency degrades to the poll interval during reconnects.
- **409 misread** as coverage when the matching subscription belongs to a different transport (§4.1).
- **Contract and ADR churn.** Push stops depending on sign-in, which is an auth-model change.
- **Limits "subject to change"** ([§Limits][c-limits]).

### 6.2 UNKNOWN items and how to resolve each

| # | Unknown | Resolution |
|---|---|---|
| U1 | Approval or eligibility gate for this app | `POST /helix/eventsub/conduits {shard_count:1}` → expect `200` |
| U2 | App-token `max_total_cost` (10,000 by example only) | Read it from the first conduit subscription response |
| U3 | 300 subscriptions per connection on a WebSocket shard | Subscribe more than 300 on one shard. Optional, costs budget |
| U4 | 3-connection limit on shard sockets | Create a 4-shard conduit and associate 4 sockets. Optional |
| U5 | Shard behavior on `session_reconnect` | Wait for a natural reconnect or recycle the connection. Compare `Get Conduit Shards` `session_id` before and after |
| U6 | Whether 409 matching includes the transport | Create a WebSocket subscription, then the same type and condition on the conduit. Observe 202 vs 409 |
| U7 | Meaning of "enabled conduit" for the cap of 5 | Create a conduit, let its shard drop, then create more. Read `Get Conduits` |
| U8 | Notification ordering and cross-shard resend timing | Not cheaply measurable. Design for it with dedupe and the Helix-confirming burst |
| U9 | `conduit.shard.disabled` cost and same-conduit delivery | Subscribe it on a 2-shard conduit, drop one socket, observe |
| U10 | App bucket size for Helix | Log `Ratelimit-Limit` on conduit calls |
| U11 | Hourly `/validate` for app tokens | Docs ambiguous. Same posture as the poller today, no action |
| U12 | Whether a zero-scope authorization makes the admin's own channel cost 0 | Read `cost` on that subscription |

**Prototype (≤ 1 day, disposable, outside `src/`):** a throwaway console app with a dedicated test client ID, never the production app or real admin credentials. Run U1, U2, U5, U6, U7, U10, and U12 in one pass. U3, U4, and U9 are optional. Delete the conduit at the end. Record the measured values in this memo before the slice starts.

## 7. Sources

All fetched on 2026-11-13. The old `eventsub-subscription-types` and `eventsub-reference` URLs now redirect to `/docs/eventsub/reference/`. `https://dev.twitch.tv/docs/product-lifecycle/` returned 404.

- Handling conduit events: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/
- Conduits API reference: https://dev.twitch.tv/docs/api/reference/conduits
- EventSub API reference: https://dev.twitch.tv/docs/api/reference/eventsub
- Managing subscriptions: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/
- Handling WebSocket events: https://dev.twitch.tv/docs/eventsub/handling-websocket-events/
- Stream subscription types: https://dev.twitch.tv/docs/eventsub/reference/stream
- Conduit subscription types: https://dev.twitch.tv/docs/eventsub/reference/conduit
- EventSub overview: https://dev.twitch.tv/docs/eventsub/
- Authentication: https://dev.twitch.tv/docs/authentication/
- Getting tokens (client credentials): https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#client-credentials-grant-flow
- Refreshing tokens: https://dev.twitch.tv/docs/authentication/refresh-tokens/
- Validating tokens: https://dev.twitch.tv/docs/authentication/validate-tokens/
- API guide (rate limits): https://dev.twitch.tv/docs/api/guide/#twitch-rate-limits
- Docs changelog: https://dev.twitch.tv/docs/change-log/ (2024-01-25 conduits GA; 2023-07-17 WebSocket 100 → 300; 2026-11-10 conduit reference updates)
- Launch announcement (Twitch Developer Forums, 2024-01-25): https://discuss.dev.twitch.com/t/available-today-twitch-chat-on-eventsub-an-api-for-sending-chat-and-the-conduit-transport-method-for-eventsub/54596

[c-create]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#creating-a-conduit
[c-assign]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#assigning-transports-to-conduits
[c-subs]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#adding-subscriptions-to-conduits
[c-notif]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#event-notifications
[c-revoke]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#revoking-your-subscription
[c-scaling]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#scaling-conduits
[c-failure]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#failure-scenarios-and-cleanup
[c-disabled]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#handling-disabled-shards
[c-auth]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#authorizing-conduits
[c-limits]: https://dev.twitch.tv/docs/eventsub/handling-conduit-events/#limits
[api-conduits]: https://dev.twitch.tv/docs/api/reference/conduits
[api-get]: https://dev.twitch.tv/docs/api/reference/conduits#get-conduits
[api-create]: https://dev.twitch.tv/docs/api/reference/conduits#create-conduits
[api-update]: https://dev.twitch.tv/docs/api/reference/conduits#update-conduits
[api-delete]: https://dev.twitch.tv/docs/api/reference/conduits#delete-conduit
[api-shards-get]: https://dev.twitch.tv/docs/api/reference/conduits#get-conduit-shards
[api-shards-update]: https://dev.twitch.tv/docs/api/reference/conduits#update-conduit-shards
[api-sub-create]: https://dev.twitch.tv/docs/api/reference/eventsub#create-eventsub-subscription
[api-sub-delete]: https://dev.twitch.tv/docs/api/reference/eventsub#delete-eventsub-subscription
[api-sub-get]: https://dev.twitch.tv/docs/api/reference/eventsub#get-eventsub-subscriptions
[m-limits]: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/#subscription-limits
[m-ws]: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/#websocket-limits
[m-status]: https://dev.twitch.tv/docs/eventsub/manage-subscriptions/#filtering-the-list-by-status
[ws]: https://dev.twitch.tv/docs/eventsub/handling-websocket-events/
[ws-limits]: https://dev.twitch.tv/docs/eventsub/handling-websocket-events/#subscription-limits
[ws-reconnect]: https://dev.twitch.tv/docs/eventsub/handling-websocket-events/#reconnect-message
[ref-stream]: https://dev.twitch.tv/docs/eventsub/reference/stream
[ref-online]: https://dev.twitch.tv/docs/eventsub/reference/stream#stream-online
[ref-offline]: https://dev.twitch.tv/docs/eventsub/reference/stream#stream-offline
[ref-shard-disabled]: https://dev.twitch.tv/docs/eventsub/reference/conduit#conduit-shard-disabled
[es-dupes]: https://dev.twitch.tv/docs/eventsub/#handling-duplicate-events
[auth-expiry]: https://dev.twitch.tv/docs/authentication/#tokens-dont-last-forever
[oauth-cc]: https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#client-credentials-grant-flow
[refresh]: https://dev.twitch.tv/docs/authentication/refresh-tokens/
[validate]: https://dev.twitch.tv/docs/authentication/validate-tokens/#who-must-validate-tokens
[rl-how]: https://dev.twitch.tv/docs/api/guide/#how-it-works
[rl-track]: https://dev.twitch.tv/docs/api/guide/#keeping-track-of-your-usage
[changelog]: https://dev.twitch.tv/docs/change-log/
[announce]: https://discuss.dev.twitch.com/t/available-today-twitch-chat-on-eventsub-an-api-for-sending-chat-and-the-conduit-transport-method-for-eventsub/54596
