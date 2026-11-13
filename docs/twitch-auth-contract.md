# Stream Keepr Twitch and Admin Auth Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 3; Part 2 F6/F10; `docs/domain-model.md`; `docs/runtime-contract.md`; ADR-0003.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope and ownership

- `TwitchTokenProvider` owns the app access token used for Helix.
- `TwitchStreamPoller` owns Helix observations and the `/channels` and `/users` lookups.
- `PollSchedule` owns the tick interval, burst, nudge, and push coverage.
- `EventSubClient` owns the WebSocket, subscriptions, reconnect, and notification routing.
- `IAdminTokenStore` owns the admin's Twitch user token, encrypted at rest.
- The cookie auth handler owns the browser admin session.
- The supervisor owns channel add/seed profile resolution and feeds observations to the coordinators.

The admin token is used **only** for EventSub subscriptions. Recording uses the Twitch app client credentials; subscriber-only streams use a separate Streamlink browser token.

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
- `LastPollAt` updates after each tick; health uses it.
- `SetPushStatus(PushStatus)` updates coverage and carries the reason: `EventSub connected`, `No admin token`, `Partial coverage`, `EventSub reconnecting`, or `Polling only`.

## 5. EventSub

- WebSocket `wss://eventsub.wss.twitch.tv/ws`; a user token is required to create subscriptions, but `stream.online` and `stream.offline` need **no extra scope**.
- No token: `schedule.SetPushStatus(PushStatus.NoAdminToken)`, wait for a token, polling carries the app.
- On `session_welcome`: subscribe `stream.online` and `stream.offline` for every watched channel with a `UserId`.
- `409 Conflict` means the subscription already exists; count it as active. Any other refusal logs and leaves that channel uncovered.
- Coverage becomes true only when every watched channel has both subscriptions. A channel add/remove or `UserId` change restarts the session and recomputes coverage.
- Keepalive silence is `keepalive_timeout + 10 s`. `revocation` logs and reconnects.
- `session_reconnect` hands off without a gap: connect to Twitch’s `reconnect_url` as is, keep reading and routing notifications on the old socket, and close the old socket only when the new one sends `session_welcome`. Subscriptions move with the session, so the new welcome does **not** re-subscribe, and push coverage stays as it was. While the handoff is pending, Twitch’s 30 s reconnect grace replaces the old socket’s keepalive watchdog. If the new socket closes, fails, or sends no welcome within 30 s, both sockets close and a fresh session starts at the base URL and re-subscribes (normal backoff). A `session_reconnect` without a `reconnect_url` restarts the same way.
- A notification routes `StreamStatusObserved` to the channel and calls `schedule.Burst(3 minutes)`; polling every 10 s confirms Helix.
- Reconnect uses exponential backoff with jitter: 5 s, 10 s, …, capped at 5 minutes, reset after a `session_welcome` with at least one active subscription.
- Token/HTTP failures during subscription are logged; the client reconnects with backoff; polling continues.
- `IAdminTokenStore.Changed` cancels and reconnects the session on sign-in or logout, so push starts/stops promptly.
- `LiveFromNotificationAsync` uses `/channels` for current title/game; the thumbnail is saved by the next poll.

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
    Task SaveAsync(AdminToken token, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
    Task WaitForTokenAsync(CancellationToken ct);
}
```

- File: `/recordings/.streamkeepr/admin-tokens.json`, encrypted with Data Protection, atomic write, Unix mode `0600`.
- Data Protection keys: `/recordings/.streamkeepr/keys`, persisted before the first token save. Losing the key ring makes existing tokens unreadable; clear and re-authenticate.
- `GetAsync` returns null when no token exists; refreshes near expiry with the refresh token and saves the rotated pair. A refresh failure clears the token and returns null.
- `SaveAsync` replaces the stored token; the most recent admin sign-in wins. Log the login, never the token.
- `ClearAsync` deletes the token and raises `Changed`; EventSub reconnects and falls back to polling.
- `WaitForTokenAsync` completes when a token is available; it remains waiting after a clear.
- No token is ever logged, returned to the browser, or written outside the encrypted file.

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
5. Start the poller and EventSub; EventSub waits for a token if none exists.

The token store and key persistence are backend prerequisites; sign-in depends on them, not the reverse. This resolves the F6-T1/F10-T7 cycle.

## 11. Failure modes

| Scenario | Behaviour |
|---|---|
| Twitch auth down | Poller observations become `Unknown`; no finalize; retry next tick. |
| Helix 401 | Invalidate app token, retry once, then `Unknown`. |
| Helix 429/5xx | Resilience retries; exhausted requests become `Unknown`. |
| No admin token | EventSub waits; polling covers the app. |
| Subscription refused | Channel stays uncovered; coverage false; poll every 30 s. |
| Partial coverage | Same as refused: global push false. |
| EventSub disconnect | Exponential backoff; polling continues. |
| Token revoked/refresh fails | Clear token; EventSub stops; polling continues. |
| Logout | Clear token; EventSub stops; polling continues. |
| Key ring lost | Tokens unreadable; clear; re-authenticate. |
| Empty allowlist | Any authenticated Twitch user is admin; startup warning. |
| Non-HTTPS sign-in | Clear configuration error; admin actions unavailable. |
| Invalid return URL | Redirect to `/`. |
| Non-allowed login | Reject; no cookie, no token. |
| Multiple admins | Latest sign-in replaces the stored token; EventSub uses the latest. |
| Subscriber token expired | Streamlink fails; recorder error/retry; rotate the secret. |

## 12. Test matrix

Token provider:

- Cache hit; refresh five minutes early; minimum one minute; 401 invalidation; concurrent double-check; FakeTimeProvider.

Poller:

- 100-login batching; live/offline/unknown; non-live `type`; non-numeric ID; `/channels` title/game; `/users` profile; 401 retry; 429/5xx unknown; no finalize on unknown.

Poll schedule:

- 30 s without push; 300 s with full coverage; 30 s while recording; 10 s burst for 3 minutes; nudge coalescing; partial coverage; `LastPollAt`.

EventSub:

- No token → wait; welcome → subscriptions; 409 counts active; refusal → uncovered; coverage all-or-nothing; notification routing and burst; reconnect URL; reconnect handoff keeps the old socket until the new welcome, routes old-socket notifications meanwhile, and does not re-subscribe; handoff close or 30 s timeout falls back to a fresh session; keepalive timeout; revocation; exponential backoff/reset; token changed → reconnect; channel change → restart.

Auth:

- Empty allowlist admits any Twitch login and logs the warning; configured allowlist rejects others; callback path; local-only return URL; cookie flags/lifetime; logout/antiforgery; non-HTTPS error; forwarded headers.

Token store:

- Encrypt/decrypt; atomic write; `0600`; refresh rotation; clear; wait; `Changed`; key-ring loss; multiple sign-ins latest wins; no token leakage.

Subscriber token:

- Streamlink args include the browser token; no logging; missing token path.

Integration:

- Sign in → EventSub coverage → logout → polling; no token at startup → polling; partial refusal → 30 s polling; restart with persisted tokens and keys.

## 13. Binding versus implementation freedom

Binding: app token refresh/invalidate rules, poller batching and Unknown semantics, poll intervals and all-or-nothing coverage, EventSub lifecycle/backoff, OAuth callback and allowlist semantics, cookie posture, token storage/refresh/clear/wait/Changed, Data Protection key location, subscriber-token path, and push-aware liveness.

Freedom: HTTP client wiring, JSON serialization, encryption implementation details, logging, backoff jitter formula, and test harness.

## 14. Related

- ADR-0003: admin-only Twitch sign-in with server-side token storage.
- `docs/runtime-contract.md` for supervisor and channel control.
- `docs/domain-model.md` for `Unknown` and `StreamStatusObserved`.
- `docs/reviews/implementation-plan-amendments.md` (A-05, A-06, A-10, C-03).
