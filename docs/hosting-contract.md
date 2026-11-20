# Stream Keepr Hosting Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 1, Phase 6/8; ADR-0002; ADR-0003; ADR-0005; `docs/runtime-contract.md`; `docs/twitch-auth-contract.md`.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

The hosting layer owns the ASP.NET Core pipeline, media serving, health checks, Data Protection, startup preflight, the Docker image, and Compose. It does not own recording, poller, session-store, or auth logic; those contracts are referenced.

**Support scope:** StreamKeepr is a personal project for private home use. Public hosting, multi-tenant deployment, and third-party distribution are outside the supported scope. The security model assumes a trusted operator and a private home or LAN environment; see `README.md` and `SECURITY.md`. The deployment examples are guidance for that posture, not a public-hosting commitment.

## 2. Host and middleware order

```text
UseForwardedHeaders          // only when a trusted reverse proxy is configured
Telemetry metrics filter    // anonymous app.Use, only when telemetry is enabled
UseSerilogRequestLogging     // one completion event per request (UseStreamKeeprRequestLogging)
UseExceptionHandler          // non-Development
UseAuthentication
UseAuthorization
UseAntiforgery
UseRecordingsMedia           // media allowlist + static files under /media
MapStaticAssets              // hashed wwwroot assets
MapRazorComponents<App>      // Interactive Server
MapRecordingIndex
MapHealthChecks
```

- `AddAuthentication` registers the Twitch OAuth handler and the `sk-auth` cookie; `AddAuthorization` and `AddCascadingAuthenticationState` are required for Blazor.
- `UseForwardedHeaders` trusts only configured proxies/networks. Never accept forwarded headers from arbitrary clients.
- No `UseHttpsRedirection`: TLS terminates at the reverse proxy. Localhost HTTP remains usable.
- `UseRecordingsMedia` runs before static assets so `/media` is governed by its allowlist.
- The exception handler uses the `/Error` page outside Development. Development registers no exception handler by contract: the live index endpoint handles expected request aborts itself (499 with no body), and an unexpected exception remains a 500 there. Do not add `UseExceptionHandler` to Development to hide aborts.
- Request logging (#199) sits after forwarded headers, so the request carries the public scheme and client, and before the exception handler and authentication, so handled 500s log at `Error` and 401/403 stay visible. It is outside media and static assets, so their 404s are logged. New middleware that should be covered by the request event (and its elapsed time) goes after it; middleware whose effect the request event must reflect, but which should not be timed, goes between forwarded headers and request logging.
- Request events log method, path without the query string, status, elapsed time, and `RequestId` (`TraceIdentifier`). Never log the query: `/signin-twitch` carries the OAuth `code` and `state`. Levels: 5xx or an unhandled exception `Error`; 4xx `Warning`, except a 499 on `/media`, which is `Debug` because a player abort is normal; any status below 400 (including 3xx) on `/health`, `/media`, `/_blazor`, `/_framework`, `/_content`, `/api/recordings`, and static-asset extensions `Verbose` (dropped at the shipped minimum); everything else `Information`.
- `AddRazorComponents().AddInteractiveServerComponents()` and `MapStaticAssets()` are registered as in the plan.

## 3. Endpoint map

| Surface | Path | Auth |
|---|---|---|
| Library/player pages | `/`, `/r/{id}` | Public |
| Recording media | `/media/{channel}/{id}/...` | Public |
| Live/final index | `/media/{channel}/{id}/index.m3u8` | Public |
| Progress write | `PUT /api/recordings/{id}/progress` | Public (viewing is open) |
| Chat read | `GET /api/recordings/{id}/chat` | Public (viewing is open) |
| Emote catalog read | `GET /api/recordings/{id}/emotes` | Public (viewing is open) |
| Viewer samples read | `GET /api/recordings/{id}/viewers` | Public (viewing is open) |
| Export download | `GET /api/exports/{exportId}/download` | Signed-in |
| Twitch login | `GET /api/auth/twitch/login?returnUrl=` | Public |
| Twitch callback | `/signin-twitch` | Public |
| Logout | `POST /api/auth/logout` | Signed-in, antiforgery |
| Settings and channel changes | Blazor circuit actions | Signed-in via `ChannelsView`/`AuthenticationState` |
| Health | `/health/live`, `/health/ready` | Public |
| Static assets | hashed `wwwroot` | Public |

The Blazor circuit actions (delete, export start, settings, stop/resume) enforce authorization in the scoped service layer as well as the UI; a disabled button is never the only guard.

Third-party emote rendering adds two runtime dependencies for each enabled provider. With BetterTTV enabled, the server fetches `https://api.betterttv.net` over HTTPS (unauthenticated, five-minute cache, three-second bound), and the browser loads static images from `https://cdn.betterttv.net` with `referrerpolicy="no-referrer"`. Neither changes the media allowlist; `chat/` remains unserved. Adding 7TV later adds its own API/CDN pair and updates this map.

## 4. Media surface

ADR-0002 applies.

- Only `.m3u8`, `.m4s`, `.mp4`, `.jpg`, and `.png` are served. Any other extension returns 404.
- Any decoded path segment starting with `.` returns 404. Reject `..` and encoded traversal before touching the filesystem.
- `session.json`, `admin-tokens.json`, `progress.json`, `.streamkeepr`, `chat/`, exports, and `.tmp` files are never served. Chat is served only by the bounded `/api/recordings/{id}/chat` endpoint, and `viewers.jsonl` only by `/api/recordings/{id}/viewers`.
- `ServeUnknownFileTypes = false`; the explicit content type map supplies:
  - `.m3u8` → `application/vnd.apple.mpegurl`
  - `.m4s` → `video/iso.segment`
  - `.mp4` → `video/mp4`
  - `.jpg` → `image/jpeg`
  - `.png` → `image/png`
- Cache headers: `.m3u8` → `no-cache`; segments, init files, and thumbnails → `public, max-age=31536000, immutable`.
- Static files handle GET/HEAD and Range requests. No directory listing; a missing file is 404, never a 500.
- `index.m3u8`: a Complete session serves the physical finalized file with HEAD and Range. A Recording session serves the immutable snapshot from `LivePlaylistCache`; the request performs no session-folder I/O and takes neither the session manifest lock nor the live refresh gate. `RecordingLibrary` is the authority for Recording/Complete/removed: finalize, reopen, and delete do not invalidate the cache, so a finalized session switches to the index immediately and a deleted session returns 404 immediately; the background refresher prunes on its next tick.
- Live index status policy:
  - Warm Recording snapshot: `200` with the cached body and `Cache-Control: no-cache`.
  - Last-known-good (LKG): the cached snapshot is the LKG. When `TimeProvider.GetUtcNow() - LivePlaylistSnapshot.RefreshedAt` (the cached-at time) is at or above `LivePlaylist:StaleAfterSeconds` (validated 1–3600, default 10), the response is still `200` with `no-cache` plus `X-StreamKeepr-Stale: true`; a fresh snapshot omits the header. The request never waits on a refresh or rebuild for LKG.
  - Cold cache or an unloaded library: `503` with `Retry-After: 1` and `Cache-Control: no-store`. There is no request-time rebuild or disk fallback.
  - A client already aborted: `499` (or no response if the transport has closed) with no body; the request event is `Debug`, never `500`/`Error`.
  - Unknown session or channel mismatch: `404`.
- A live or LKG body never contains `#EXT-X-ENDLIST`; a Complete session must return a playlist with exactly one. `200` and `404` index responses set `Cache-Control: no-cache`, and HEAD reports the same `Content-Length` as GET for fresh and LKG bodies.
- The request path has no I/O, so there is no per-request deadline middleware. The server deadline is the background `LivePlaylist:RefreshTimeoutSeconds` bound on `LivePlaylistService`; a timed-out refresh keeps the LKG while the endpoint keeps serving from the cache without waiting.
- The chat endpoint is public, returns `Cache-Control: no-store`, bounds its window and result limit, and returns 400/404/200-with-`hasChat:false` as defined in `docs/backend-ui-contract.md`.
- The media root is created at startup. An unwritable root fails startup with the UID 1654 guidance.
- Deletion races return 404/410, never a 500.

## 5. Health checks

### `/health/live`

Runs only `DvrHealthCheck`, tagged `live`.

- Push-aware poll budget: `IsPushConnected ? 3 × PollIntervalWithEventSubSeconds : 3 × PollIntervalSeconds` (900 s vs 90 s with defaults).
- Unhealthy: stale poll, a channel loop not processing events, or a recording without media progress for `2 × StallTimeoutSeconds`.
- Degraded: cooling down, waiting to retry, or last observation `Unknown`.
- No disk or library-loaded check.

### `/health/ready`

Runs all checks: DVR, disk, and a `LibraryLoadedHealthCheck` that requires `RecordingLibrary.IsLoaded`.

- False until startup recovery has scanned the disk.
- Disk: unhealthy below `MinFreeGb`, degraded below twice that.
- Readiness is for monitoring and orchestration; Docker’s image healthcheck uses `/health/live`.

Docker healthcheck: `/health/live`, interval 30 s, timeout 5 s, start period 60 s, retries 3. Docker does not restart unhealthy containers by default; the check is informational.

## 6. Data Protection

- `AddDataProtection().PersistKeysToFileSystem(<recordings>/.streamkeepr/keys).SetApplicationName("StreamKeepr")`.
- Directory `0700`, files `0600`; create before auth or token use.
- Keys live on the recordings volume, survive restarts, and are never served (the media allowlist blocks `.streamkeepr`).
- Losing the key ring invalidates cookies, antiforgery tokens, and the encrypted admin token; log it and require re-authentication.
- An unwritable key directory fails startup with a clear message.
- No keys are baked into the image or committed.

## 7. Startup preflight

`ToolPreflight` is the first application hosted service and runs before the supervisor.
When telemetry is enabled, OpenTelemetry inserts its own hosted service at index 0 so it can capture
early telemetry; `ToolPreflight` remains the first application service after it. The metrics-filter
middleware runs before authentication and after forwarded headers, and opts sensitive or
long-lived routes out of HTTP metrics.

- `ffmpeg -version`, `ffprobe -version`, `streamlink --version`; log the first line of each. Missing tools throw a clear `InvalidOperationException` naming the tool.
- Recordings root: create it, write and delete `.write-probe`, and fail with the UID 1654 / named-volume-or-chown message on `UnauthorizedAccessException` or `IOException`.
- Data Protection key directory: create and probe; fail with the same ownership guidance.
- Any failure stops startup; no partial channel start.

## 8. Container

### Dockerfile

- Build stage: `mcr.microsoft.com/dotnet/sdk:10.0.103` (the newest published 10.0.1xx tag; `10.0.114` was never published); copy only `src/StreamKeepr/StreamKeepr.csproj`, restore, copy `src/`, publish Release.
- Runtime stage: `mcr.microsoft.com/dotnet/aspnet:10.0.14`; install `python3`, `python3-venv`, `curl`; create `/opt/streamlink` venv and install the pinned Streamlink; copy static `ffmpeg` and `ffprobe`.
- `ENV PATH="/opt/streamlink/bin:${PATH}"`, `Storage__RecordingsRoot=/recordings`.
- Create `/recordings`, `chown "$APP_UID:$APP_UID"`, `USER $APP_UID`, `EXPOSE 8080`.
- `HEALTHCHECK` as above; `ENTRYPOINT ["dotnet", "StreamKeepr.dll"]`.
- Only `src/` enters the build; tests, secrets, recordings, and docs never enter an image layer.

Pinned versions: .NET SDK `10.0.103`, ASP.NET runtime `10.0.14`, Streamlink `8.6.3`, static FFmpeg/ffprobe `9.0.2-1`, CliWrap `3.10.5`, Resilience `10.11.0`, OAuth `10.1.0`, Serilog.AspNetCore `10.0.0`, OpenTelemetry `1.21.0` (`Extensions.Hosting`, `Exporter.OpenTelemetryProtocol`, `Instrumentation.AspNetCore`, `Instrumentation.Http`). Test-only: `OpenTelemetry.Exporter.InMemory` `1.21.0` and `Microsoft.Extensions.Diagnostics.Testing` `10.11.0`. Update deliberately; Streamlink is the one to watch and the build arg allows a bump.

### `.dockerignore`

`**/bin/`, `**/obj/`, `.git/`, `.vs/`, `.idea/`, `.agents/`, `.claude/`, `.serena/`, `.github/`, `**/secrets/`, `**/.env`, `recordings/`, `tests/`, `*.md`, `docs/`, `AGENTS.md`, `GLOSSARY.md`, `*.sln.DotSettings.user`.

### Compose

- One service `stream-keepr`; build; image; `container_name`; `restart: unless-stopped`; `init: true`; `stop_grace_period: 50s`.
- Port `8080:8080` (plain HTTP). **HTTPS is an external reverse-proxy requirement** (E-15); without it, sign-in works only from `localhost`.
- Environment: `ASPNETCORE_ENVIRONMENT=Production`, `TZ`, optional `Twitch__Channels__0` seed, optional `Telemetry__Enabled` and `OTEL_EXPORTER_OTLP_ENDPOINT` (both default to disabled/empty).
- Secrets: `Twitch__ClientId`, `Twitch__ClientSecret`; optional `Recorder__TwitchUserOAuthToken`. Mounted as `/run/secrets` and loaded by `AddKeyPerFile`.
- Volume: `recordings:/recordings`; bind mount requires `chown -R 1654:1654 ./recordings`.
- Logging: `json-file`, `max-size: 10m`, `max-file: 5`. The app writes through Serilog's console sink only, configured by the `Serilog` section; `Serilog__MinimumLevel__…` environment variables change levels on restart.
- The image healthcheck is inherited; no Compose override.

## 9. Shutdown

- Docker sends SIGTERM to the app (tini forwards to the main process, not the process group).
- The app cancels channel loops; each recorder stops gracefully within the 35 s job budget; the host timeout is 40 s; Compose grace is 50 s.
- If the host timeout fires, the container kills the children and recovery repairs the parts on the next start.
- `stop_grace_period` must remain above the host timeout.
- Lifecycle breadcrumbs (#198, `HostLifecycleLog`): once the host has started it logs one `Information` summary (environment, watched-channel count, recordings root, EventSub transport, chat capture on/off); when shutdown begins (SIGTERM, `docker compose stop`) it logs one `Information` stopping line. The summary names safe values only and never an options object, so the Twitch client secret and chat token cannot appear. The Critical supervisor/fatal-host logs and the Data Protection key-ring logs keep their own levels.

## 10. Failure modes

| Scenario | Behaviour |
|---|---|
| Unsupported media extension | 404 |
| Dot segment / traversal | 404 |
| Missing media file | 404 |
| Unknown session/channel | 404 |
| Live index with an unloaded library or cold cache | 503 + `Retry-After: 1`, `Cache-Control: no-store` |
| Live snapshot older than `StaleAfterSeconds` | 200 last-known-good body + `X-StreamKeepr-Stale: true`, `no-cache` |
| Live index client abort | 499 (or no response); `Debug` request event; never 500/`Error` |
| Unexpected exception in Development | 500; Development intentionally registers no exception handler |
| Live playlist refresh failure | Last-known-good snapshot keeps serving; host stays up; a failed read publishes nothing; one `Warning` per failure window |
| Refresh/finalize race | The library becomes Complete; the refresher publishes nothing and the endpoint serves the finalized index |
| Delete/live-request race | 404/410 immediately; the library entry is removed first; the refresher prunes later |
| Stuck poller | `/health/live` unhealthy after the push-aware budget |
| Stuck channel loop or stalled media | `/health/live` unhealthy |
| Disk below `MinFreeGb` | `/health/ready` unhealthy; recording stops |
| Library still recovering | `/health/ready` false |
| Missing ffmpeg/ffprobe/streamlink | Startup fails with a clear message |
| Unwritable recordings/keys | Startup fails with UID 1654 guidance |
| Missing secrets | Options validation fails startup |
| Missing reverse proxy / HTTP sign-in | Clear configuration error; admin unavailable |
| Proxy headers not trusted | Callback/cookie origin wrong; sign-in fails |
| Volume permissions wrong | Preflight fails with chown guidance |
| Container killed at grace expiry | Recovery repairs on restart |

## 11. Test matrix

Host and media:

- Middleware order, auth challenge, antiforgery, forwarded headers.
- Allowed media types served with correct content types and cache headers.
- `.json`, `.tmp`, dot-folders, encoded traversal, unknown sessions/channels → 404.
- GET/HEAD and Range; finalized index has exactly one ENDLIST; live index serves the cached snapshot with no session I/O and no session lock; finalize switches to the index immediately; a deleted session is 404 immediately; cold cache is 503 + `Retry-After: 1` + `no-store`; a snapshot at the staleness threshold is 200 with `X-StreamKeepr-Stale: true` and a fresh one omits it; HEAD matches GET's content length for fresh and LKG bodies; a client abort is 499/empty and never a 500 or an `Error` request event; 320 concurrent warm requests return 200/503 only.
- `RequestLogPolicy`: `/media` 499 is `Debug`; other 4xx are `Warning`; 5xx and unhandled exceptions are `Error`. `LivePlaylistService` logs one `Warning` per failure window and `Debug` after that, resetting on success.
- Export download requires sign-in; progress is public; delete/settings require sign-in.

Health:

- Live healthy with a recent poll; unhealthy with a stale poll, stuck loop, or stalled media.
- Live push-aware budget (90 s vs 900 s).
- Ready false until `IsLoaded`; disk thresholds; DVR degradation.

Data Protection and preflight:

- Keys persist across app restart; cookie/antiforgery/admin token survive.
- Lost key ring forces re-auth and clears tokens.
- Missing tools and unwritable root/keys fail startup with clear messages.

Container:

- Image builds with pinned versions; runs as UID 1654.
- `/recordings` volume; secrets not in image; `.dockerignore` excludes secrets/tests/recordings/tooling.
- Healthcheck passes after startup; deep links `/r/{id}` render after restart.
- `docker stop` finalizes within the grace period; restart recovers.
- One startup summary and one stopping breadcrumb, with no secrets (`HostLifecycleLogHostTests`).

Integration:

- Build, compose up, sign in via HTTPS proxy, play a recording, export, delete, restart, verify keys and tokens.
- Direct HTTP 8080: viewing works; sign-in fails with a clear message.

## 12. Binding versus implementation freedom

Binding: middleware order, media allowlist and exclusions, content types and cache policy, health tags/budgets/readiness, Data Protection path/application name/permissions, preflight checks, container non-root/pins/Compose posture, endpoint auth map, forwarded-header trust, and shutdown budgets.

Freedom: minimal API style, static-file provider implementation, middleware mechanics, Docker layer organization, logging, and test harness.

## 13. Related

- ADR-0002: media exposure allowlist.
- ADR-0003: admin-only Twitch sign-in and token storage.
- ADR-0005: minimal Blazor shell in the backend foundation.
- `docs/twitch-auth-contract.md`, `docs/runtime-contract.md`, `docs/session-store-contract.md`.
- `docs/reviews/implementation-plan-amendments.md` (A-02, A-06, A-10, C-03, E-15).
