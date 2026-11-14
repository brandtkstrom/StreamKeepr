# Stream Keepr Twitch and Admin Auth Contract

Status: accepted (2026-11-10); amended 2026-11-13 for the EventSub conduit transport (ADR-0007)
Source: Implementation plan Part 1, Phase 3; Part 2 F6/F10; `docs/domain-model.md`; `docs/runtime-contract.md`; ADR-0003; ADR-0007.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope and ownership

- `TwitchTokenProvider` owns the app access token used for Helix.
- `TwitchStreamPoller` owns Helix observations and the `/channels` and `/users` lookups.
- `PollSchedule` owns the tick interval, burst, nudge, and push coverage.
- `EventSubClient` owns the EventSub WebSocket loop, keepalive, reconnect handoff, backoff, and notification routing.
- `IEventSubBinding` owns how a session authenticates and subscribes: `WebSocketEventSubBinding` (user token) or `ConduitEventSubBinding` (app token and conduit), chosen by `Twitch:EventSubTransport`.
- `EventSubConduitApi` owns the Helix conduit, shard, and conduit subscription calls; `IConduitStateStore` owns the saved conduit ID.
- `IAdminTokenStore` owns the admin's Twitch user token, encrypted at rest.
- `Chat:TwitchUserOAuthToken` is an optional, separate user token owned by chat capture; it is never the admin token or the Streamlink recorder token.
- The cookie auth handler owns the browser admin session.
- The supervisor owns channel add/seed profile resolution and feeds observations to the coordinators.

The admin token is used **only** for EventSub subscriptions in WebSocket mode. Conduit mode uses the app token and never reads the admin token. Recording uses the Twitch app client credentials; subscriber-only streams use a separate Streamlink browser token. Chat capture reads neither the admin token nor the recorder token.

## 2. App token provider

```csharp
public sealed class TwitchTokenProvider(
    IHttpClientFactory httpFactory, IOptions<TwitchOptions> options, TimeProvider clock)
```
- Client-credentials grant against `https://id.twitch.tv/oauth2/token` using the named `twitch-auth` client.
- One immutable `CachedToken(Value, RefreshAt)` behind one volatile field; a semaphore double-check.
- Refresh at `max(60 s, expires_in - 300 s)`.
- `Invalidate(staleToken)` clears only if the cached value matches.
- A 401 from Helix invalidates and retries once; a second 401 becomes `Unknown`.
- No failure cache; a Twitch auth outage surfaces as `Unknown` through the poller.
- `TimeProvider` everywhere; tests use `FakeTimeProvider`.
- Never log the client secret or access token.

### 2.1 Optional chat token

- `Chat:TwitchUserOAuthToken` is a dedicated user access token with `chat:read`. `Chat:TwitchLogin` is required with it, and its shape is validated at startup; no network token call is made at startup.
- The IRC connection validates the token. On an authentication failure, log the reason without the token, switch to anonymous capture for the life of the process, and reconnect.
- Store it through user-secrets or `/run/secrets`; never log the token or a raw `PASS` line.
- Do not add `chat:read` to the admin OAuth flow. That would couple chat capture to admin sign-in and change this contract.

## 3. Poller and Helix

- One `/helix/streams` request per 100 logins; `first=100`.
- `type == "live"` and a numeric, at-most-20-digit stream ID become `Live`; a login absent from the response is `Offline`.
- Any HTTP/auth/network failure for a batch becomes `Unknown` for every login in that batch. Unknown never finalizes a recording.
- `started_at` is the session start; titles and games come from `/streams`.
- `GetChannelInfoAsync` uses `/helix/channels` for current title/game after an EventSub notification.
- `GetUserAsync(login)` uses `/helix/users?login=` and returns `UserId`, display name, and avatar. The supervisor calls it when a channel is added or seeded; a UI add fails if the login is unknown; a seeded failure logs and stays polling-only.
- 401: invalidate the app token and retry once. 429/5xx: standard resilience handles retries; exhausted requests become `Unknown`.
- `IsNumericId` accepts only digits, length 1–20.

## 4. Poll schedule and push coverage

| Signal | Meaning |
|---|---|
| `normal` | `PollIntervalSeconds`, default 30 s |
| `withPush` | `PollIntervalWithEventSubSeconds`, default 300 s |
| `burst` | 10 s ticks for 3 minutes after a notification |
| `nudge` | A channel add/resume wakes the poller immediately; coalesced |
| `IsPushConnected` | **All** watched channels have active `stream.online` and `stream.offline` subscriptions |

- Poller interval: burst 10 s; otherwise `IsPushConnected ? withPush : normal`.
- Any channel recording or waiting to retry forces the short interval.
- Partial coverage is not “push connected”: if any watched channel lacks a subscription, poll every 30 s.
- `LastPollAt` updates after each tick; health uses it. `Polled(succeeded, roundTrip)` also stamps `LastSuccessfulPollAt` with the same instant when every `/streams` batch succeeded (or there was nothing to poll), and records `LastApiRoundTrip` when Helix was called. `EventSubMessageReceived()` records `LastEventSubMessageAt` for every EventSub message, keepalives included. `CurrentInterval` is the interval the last wait chose. None of these raise `Changed`.
- `SetPushStatus(PushStatus)` updates coverage and carries the reason: `EventSub connected`, `No admin token`, `Partial coverage`, `EventSub reconnecting`, or `Polling only`. A status from `PushStatus.FromCoverage(coveredLogins, watched)` also carries `CoveredLogins` and `Watched`, so the UI can show per-channel push and “N of M”. Statuses compare by state, watched count, and covered set.

## 5. EventSub

`Twitch:EventSubTransport` is `WebSocket` (default) or `Conduit`. Both modes share the socket loop: keepalive, the reconnect handoff, backoff, notification routing and burst, and all-or-nothing coverage. §5.1 applies to WebSocket mode; §5.2 lists what differs in Conduit mode.

### 5.1 WebSocket mode

- WebSocket `wss://eventsub.wss.twitch.tv/ws`; a user token is required to create subscriptions, but `stream.online` and `stream.offline` need **no extra scope**.
- No token: `schedule.SetPushStatus(PushStatus.NoAdminToken)`, wait for a token, polling carries the app.
- Open a session only when at least one watched channel is **resolved** (has a `UserId`). With no resolved channel, set `PollingOnly` when no channels are watched or `PartialCoverage` when watched channels remain unresolved, and wait for `SubscriptionsChanged`/`Changed`; do not open a socket.
- On `session_welcome`: subscribe `stream.online` and `stream.offline` for every resolved channel, stopping at the first capacity refusal. `409 Conflict` is active only when a follow-up `GET /helix/eventsub/subscriptions?user_id=` confirms an enabled websocket subscription for the same type and condition on **this** session; otherwise the channel is uncovered. Do not delete another session's subscription.
- `429` is capacity, not a transient failure: stop the subscription pass, keep the session and the subscriptions already created, and leave the remaining channels on polling. Log Twitch's response message so cost, 300-subscription, 3-connection, and duplicate-limit causes are distinguishable. Do not delete a successfully created `stream.online` because its `stream.offline` was refused.
- Coverage becomes true only when every watched channel has both subscriptions. With the WebSocket cost cap, complete coverage is expected only for about five channels (`stream.online` + `stream.offline` cost 2 per unauthorized broadcaster; `max_total_cost` is 10); more watched channels means `PartialCoverage` by design, 30 s polling, and the 90 s health budget. Cover the first N resolved channels in persisted channel order; the rest stay on polling. A channel add/remove or `UserId` change restarts the session and recomputes coverage.
- `401` during a subscription request: force one admin-token refresh and retry the request once. A second `401` is logged as an auth/configuration error and fails the session; do not refresh again. A refresh failure clears the token. The 409 verification GET uses the same one-refresh policy.
- Keepalive silence is `keepalive_timeout + 10 s`. `revocation` logs the subscription type and status and reconnects.
- `session_reconnect`: keep the old socket open, connect the replacement, and continue routing old-socket notifications until the replacement sends `session_welcome`; then adopt it, close the old socket, and do **not** re-create subscriptions (the replacement inherits them). A failed handoff falls back to a fresh base-URL session with normal backoff and resubscription. A channel/token change or revocation during the handoff cancels both and starts a fresh base session. Deduplicate all messages by `metadata.message_id` with a bounded recent-ID set (256, across sessions).
- A notification routes `StreamStatusObserved` to the channel and calls `schedule.Burst(3 minutes)`; polling every 10 s confirms Helix. Only a `stream.online` whose `event.type` is `live` becomes `Live`; other broadcast types (`playlist`, `watch_party`, `premiere`, `rerun`) route `Offline`. Malformed messages are logged and skipped; a per-notification route failure is logged and does not tear down the session.
- Reconnect uses exponential backoff with jitter: 5 s, 10 s, …, capped at 5 minutes, reset after a `session_welcome` with at least one active subscription. A successful in-place reconnect handoff is not a new session and does not back off; a voluntary channel/token restart reconnects immediately.
- Transient token/HTTP failures during subscription are logged; the client reconnects with backoff; polling continues.
- `IAdminTokenStore.Changed` cancels and reconnects the session on sign-in or logout, so push starts/stops promptly.
- `LiveFromNotificationAsync` uses `/channels` for current title/game, with a short timeout; on timeout or failure the Live observation carries empty metadata and the next poll fills it. The thumbnail is saved by the next poll.
- Connect, reconnect-welcome, subscription-request, `/channels`, and socket-close operations are bounded (10 s connect, 30 s reconnect welcome, 10 s subscription request, 5 s `/channels`, 2–5 s close) so a dead peer cannot stall the loop or shutdown.

### 5.2 Conduit mode (ADR-0007)

- The app access token from `TwitchTokenProvider` authorizes every conduit, shard, and subscription call. A 401 invalidates it and retries once. The admin token is never read, and `IAdminTokenStore.Changed` does not restart the session: sign-in and logout do not affect push.
- Before each session: load the saved conduit (§7). Reuse it if `GET /helix/eventsub/conduits` still lists it and it was saved under the current `ClientId`; otherwise create one with `shard_count = 1` and save it. Never delete a conduit Stream Keepr did not create. A 429 on create means the five-conduit limit and is logged as such.
- On `session_welcome`: first bind shard `0` to the session (`PATCH /helix/eventsub/conduits/shards`; Twitch allows 10 s), then reconcile. Reconcile lists the subscriptions on our conduit (all pages, filtered by `conduit_id` and checked client-side), deletes those for unwatched channels, disabled statuses, duplicates, or other types, and creates the missing `stream.online`/`stream.offline` with `transport = {method: conduit, conduit_id}`.
- `409 Conflict` counts as active only if a second listing shows that subscription enabled on our conduit. Any other 4xx, including 429, logs and leaves that channel uncovered. A 401 after the retry, a 5xx, or a network error restarts the session with backoff.
- A channel add/remove or `UserId` change restarts the session, which re-binds the shard and reconciles the delta; subscriptions are not recreated.
- Unlike §5.1, a session opens even with no resolved channel, so reconcile can delete the subscriptions of channels that were removed.
- Subscription creates are not capacity-stopped as in §5.1: each refused channel is skipped and the pass continues.
- `session_reconnect`: the same handoff as §5.1, and the shard is bound to the new session before the old socket closes.
- `revocation` affects one subscription: log it, and if the channel is watched set `Partial coverage`. The session keeps running; the next session re-creates the subscription.
- A failed conduit setup or shard bind restarts the session with backoff; polling continues.
- The real cost budget, the app rate bucket, and shard behaviour on reconnect are unverified until the owner runs the live prototype (spike memo §6.2). Each reconcile logs the `total_cost` and `max_total_cost` that Twitch reports.

## 6. Admin sign-in

### Allowlist

`AuthOptions.AllowedTwitchLogins` is a list of Twitch logins, normalized to lowercase.

- Non-empty: only those logins may complete sign-in. Any other login is rejected with `context.Fail`, no cookie, and no token saved.
- **Empty: every successfully authenticated Twitch user is an app admin.** This is an explicit opt-in to open administration. Log a startup warning: “AllowedTwitchLogins is empty; any Twitch user can administer this app.” Set the allowlist for any shared or untrusted network.
- Viewing and recording stay open in both modes. The allowlist governs app management actions only; it does not grant Twitch moderator/broadcaster powers.

### OAuth

- `AspNet.Security.OAuth.Twitch` (pinned 10.1.0), same Twitch app as the recorder.
- `CallbackPath = "/signin-twitch"`; the Twitch app redirect URI must match the public origin exactly.
- No additional scopes; `SaveTokens = false`.
- `OnCreatingTicket` resolves the Twitch login, applies the allowlist, and saves access/refresh tokens through `IAdminTokenStore`. A rejected login saves nothing.
- Login: `GET /api/auth/twitch/login?returnUrl=`; accept only local URLs; otherwise `/`. Challenge with the local return URL.
- Logout: `POST /api/auth/logout`, antiforgery-protected form post; sign out, clear the admin token, redirect to a local URL.
- Non-HTTPS requests (except `localhost`) cannot start sign-in; return a clear configuration message. Admin actions remain unavailable until HTTPS is configured.
- `UseForwardedHeaders` must be configured for a reverse proxy so the callback and cookie use the public HTTPS origin.

### Cookie

- Cookie scheme `sk-auth`; `HttpOnly`; `SameSite=Lax`; `SecurePolicy=SameAsRequest` (secure under HTTPS, works on `localhost` HTTP).
- 7-day sliding expiration.
- `LoginPath = "/api/auth/twitch/login"`, `LogoutPath = "/api/auth/logout"`.
- Blazor components read `AuthenticationState`; `ChannelsView`, export, delete, and download endpoints enforce authorization server-side.
- Tokens never go into the cookie or the browser.

## 7. Admin token store

```csharp
public sealed record AdminToken(
    string Login, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public interface IAdminTokenStore
{
    event Action? Changed;
    ValueTask<string?> GetAsync(CancellationToken ct);
    ValueTask<string?> RefreshAsync(CancellationToken ct);
    Task SaveAsync(AdminToken token, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
    Task WaitForTokenAsync(CancellationToken ct);
}
```

- File: `/recordings/.streamkeepr/admin-tokens.json`, encrypted with Data Protection, atomic write, Unix mode `0600`.
- Data Protection keys: `/recordings/.streamkeepr/keys`, persisted before the first token save. Losing the key ring makes existing tokens unreadable; clear and re-authenticate.
- `GetAsync` returns null when no token exists; refreshes near expiry with the refresh token and saves the rotated pair. A refresh failure clears the token and returns null.
- `RefreshAsync` forces a refresh even when the token is not near expiry. No token returns null without an HTTP call. Success persists the rotated pair and does **not** raise `Changed` (otherwise EventSub would cancel the session it is trying to repair). Failure clears the token, raises `Changed` once, and returns null. EventSub uses it once on a subscription `401`; a second `401` is an auth/configuration error, not another refresh.
- `SaveAsync` replaces the stored token; the most recent admin sign-in wins. Log the login, never the token.
- `ClearAsync` deletes the token and raises `Changed`; EventSub reconnects and falls back to polling.
- `WaitForTokenAsync` completes when a token is available; it remains waiting after a clear.
- No token is ever logged, returned to the browser, or written outside the encrypted file.

### Conduit state

- File: `/recordings/.streamkeepr/eventsub-conduit.json` with `conduit_id` and `client_id`. Not a secret; written atomically with mode `0600` like the other app data.
- A missing or unreadable file means "no conduit": log, create a new one, and let the lost one expire on Twitch's side.
- WebSocket mode at startup (`ConduitRetirementService`): if the file names a conduit under the current `ClientId`, delete it (404 counts as deleted) and the file. If the delete fails, keep the file so the next start retries. A conduit under another `ClientId` is forgotten without a delete.
- Logout and app shutdown do not touch the conduit; a restart within 72 hours resumes it without re-subscribing.

## 8. Subscriber-only streams

- `RecorderOptions.TwitchUserOAuthToken` is a manual secret containing the twitch.tv browser token.
- Streamlink receives it through `--twitch-api-header "Authorization=OAuth <token>"`.
- The token is not the admin app token. Rotation is manual until a Settings entry exists; record the rotation requirement.
- Never log the token; prefer a Docker secret/environment file over a committed value.

## 9. Health signals

`DvrHealthCheck` uses push-aware liveness:

- `IsPushConnected == true`: last poll budget `3 × PollIntervalWithEventSubSeconds` (900 s with defaults).
- `IsPushConnected == false` (including partial coverage): budget `3 × PollIntervalSeconds` (90 s).
- A stale poll, a stuck channel loop, or stalled media progress is unhealthy; retrying/cooldown/Unknown is degraded.

This corrects amendment A-06: a 90 s budget is only valid when polling is the active path.

## 10. Startup order

1. Configure Data Protection keys under `.streamkeepr/keys`.
2. Load admin tokens; if unreadable, clear and log.
3. Configure the OAuth handler and cookie scheme.
4. Load channels and resolve missing profiles through `/helix/users`.
5. Start the poller and EventSub. WebSocket mode waits for a token if none exists; Conduit mode needs only the app token. In WebSocket mode, a leftover conduit is deleted in the background.

The token store and key persistence are backend prerequisites; sign-in depends on them, not the reverse. This resolves the F6-T1/F10-T7 cycle.

## 11. Failure modes

| Scenario | Behaviour |
|---|---|
| Twitch auth down | Poller observations become `Unknown`; no finalize; retry next tick. |
| Helix 401 | Invalidate app token, retry once, then `Unknown`. |
| Helix 429/5xx | Resilience retries; exhausted requests become `Unknown`. |
| No admin token | WebSocket mode: EventSub waits; polling covers the app. Conduit mode: no effect. |
| Subscription refused | Channel stays uncovered; coverage false; poll every 30 s. |
| Partial coverage | Same as refused: global push false. |
| EventSub disconnect | Exponential backoff; polling continues. |
| Token revoked/refresh fails | Clear token; WebSocket-mode EventSub stops; polling continues. Conduit mode: no effect. |
| Logout | Clear token; WebSocket-mode EventSub stops; polling continues. Conduit mode: push continues. |
| Conduit missing after downtime | Create a new conduit, save it, reconcile; polling covers the gap. |
| Five conduits already exist | Conduit create returns 429; log "EventSub conduit limit reached"; back off; polling continues. |
| Conduit shard bind fails | Restart the session with backoff; polling continues. |
| Key ring lost | Tokens unreadable; clear; re-authenticate. |
| Empty allowlist | Any authenticated Twitch user is admin; startup warning. |
| Non-HTTPS sign-in | Clear configuration error; admin actions unavailable. |
| Invalid return URL | Redirect to `/`. |
| Non-allowed login | Reject; no cookie, no token. |
| Multiple admins | Latest sign-in replaces the stored token; WebSocket-mode EventSub uses the latest. |
| Subscriber token expired | Streamlink fails; recorder error/retry; rotate the secret. |

## 12. Test matrix

Token provider:

- Cache hit; refresh five minutes early; minimum one minute; 401 invalidation; concurrent double-check; FakeTimeProvider.

Poller:

- 100-login batching; live/offline/unknown; non-live `type`; non-numeric ID; `/channels` title/game; `/users` profile; 401 retry; 429/5xx unknown; no finalize on unknown.

Poll schedule:

- 30 s without push; 300 s with full coverage; 30 s while recording; 10 s burst for 3 minutes; nudge coalescing; partial coverage; `LastPollAt`.

EventSub:

WebSocket mode:

- No token → wait; no resolved channel → no socket and `PollingOnly`/`PartialCoverage`; welcome → subscriptions; 409 session verification (matching, stale, GET failure, 401); 429 capacity → keep the session and partial coverage, no reconnect; refusal → uncovered; coverage all-or-nothing and the ~5-channel cap.
- Notification routing and burst; non-live `stream.online` → Offline; `/channels` timeout falls back to empty metadata; message-ID dedupe; malformed message and route isolation.
- Reconnect handoff (old socket drained until new welcome, no resubscribe, revocation/channel/token abort, failed handoff base fallback); keepalive timeout; revocation; 401 force-refresh/one retry; connect/subscription/close timeouts; exponential backoff/reset; voluntary restart immediate; token changed → reconnect; channel change → restart.

Conduit mode:

- First start creates and saves a conduit and binds shard 0 before any subscription; restart reuses the saved conduit and creates only what is missing; a conduit Twitch deleted is replaced; one saved under another client ID is left alone; unwatched and disabled subscriptions are deleted; refusal → partial coverage; 409 counts only when the subscription is on our conduit; notification routing; revocation → partial coverage without a reconnect; reconnect rebinds the shard before the old socket closes; shard bind failure restarts; app-token outage backs off without a socket; admin token changes never restart.
- Conduit API: one shard; conduit limit 429; 401 invalidate and retry once; pagination with a client-side conduit filter; shard errors in a 202; 409/4xx/429/5xx outcomes; deletes accept 404.
- Conduit state store and WebSocket-mode retirement.

Auth:

- Empty allowlist admits any Twitch login and logs the warning; configured allowlist rejects others; callback path; local-only return URL; cookie flags/lifetime; logout/antiforgery; non-HTTPS error; forwarded headers.

Token store:

- Encrypt/decrypt; atomic write; `0600`; refresh rotation; forced `RefreshAsync` success (no `Changed`) and failure (clear + `Changed`); no token; clear; wait; `Changed`; key-ring loss; multiple sign-ins latest wins; no token leakage.

Subscriber token:

- Streamlink args include the browser token; no logging; missing token path.

Integration:

- Sign in → EventSub coverage → logout → polling; no token at startup → polling; no resolved channels → polling without a socket; partial refusal/429 → 30 s polling; reconnect handoff without resubscribe; restart with persisted tokens and keys.

## 13. Binding versus implementation freedom

Binding: app token refresh/invalidate rules, poller batching and Unknown semantics, poll intervals and all-or-nothing coverage, EventSub lifecycle/backoff, the transport flag and its WebSocket default, conduit ownership and cleanup rules, OAuth callback and allowlist semantics, cookie posture, token storage/refresh/clear/wait/Changed, Data Protection key location, subscriber-token path, and push-aware liveness.

Freedom: HTTP client wiring, JSON serialization, encryption implementation details, logging, backoff jitter formula, and test harness.

## 14. Related

- ADR-0003: admin-only Twitch sign-in with server-side token storage.
- ADR-0007: EventSub conduit transport behind a flag.
- `docs/research/eventsub-conduits-spike.md`: conduit research, unknowns, and the prototype plan.
- `docs/runtime-contract.md` for supervisor and channel control.
- `docs/domain-model.md` for `Unknown` and `StreamStatusObserved`.
- `docs/reviews/implementation-plan-amendments.md` (A-05, A-06, A-10, C-03).
