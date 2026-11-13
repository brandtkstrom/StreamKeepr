# Stream Keepr Decision Digest

Status: accepted (2026-11-10)
Owner: @brandtkstrom
Entry point for: `to-spec`
Source: map issue #1 and decision tickets #2–#18; the binding contracts; ADRs 0001–0005; the plan-review register; `GLOSSARY.md`; `docs/plans/streamkeepr-implementation-plan.md`; `docs/design-assets.md`.

This is the single handoff for the full application specification. It records the decisions already made, the plan amendments that bind the spec, the accepted risks, the open items, and the post-implementation conformance review. It does not rewrite the plan and does not implement the app.

## 1. How to use this digest

`to-spec` starts here, then reads the binding artifacts in this order:

1. `GLOSSARY.md` for the domain vocabulary.
2. `docs/domain-model.md` — state, events, commands, reducer, retry, recovery.
3. `docs/recorder-contract.md` — Streamlink/FFmpeg process and part lifecycle.
4. `docs/session-store-contract.md` — manifest, locks, stitching, recovery, library, disk guard.
5. `docs/runtime-contract.md` — supervisor, coordinators, queues, channel control, status, shutdown.
6. `docs/twitch-auth-contract.md` — app token, poller, EventSub, OAuth, allowlist, token store.
7. `docs/hosting-contract.md` — middleware, `/media`, health, Data Protection, preflight, container.
8. `docs/testing-contract.md` — test layers, fake harness, determinism, CI, gates.
9. `docs/backend-ui-contract.md` — view models, progress, export, delete, storage, auth seams.
10. `docs/frontend-foundation-contract.md` — Blazor host, shell, preferences, clock, `LibraryView`, primitives, Mock.
11. `docs/library-contract.md` — grouping, sort, search, drawer, keyboard tree, live updates.
12. `docs/player-contract.md` — hls.js, controls, live edge, resume, preferences, stats, chapters.
13. `docs/details-auth-settings-contract.md` — details, export, delete, auth UX, `/settings`, stop/resume.
14. `docs/adr/0001`–`0005`; `docs/reviews/implementation-plan-amendments.md`; `docs/design-assets.md`.
15. The plan for task IDs, original wording, and waves — reference only.

**Precedence:** explicit user instructions in the task → the binding contracts → ADRs and the amendment register → the plan → the plan's code snippets. A contract that says “this contract wins” supersedes the plan wherever they differ. The plan's code is reference, not binding.

**Status of this digest:** the map is complete when #18 closes. The 63 frontend task slices are deliberately not here; they are produced by `to-tickets` after the spec.

## 2. Destination, scope, and non-goals

**Destination:** a decision-complete application specification input for `to-spec`.

- The backend is specified implementation-first: target architecture, domain/reducer, recorder, session storage/recovery, runtime, Twitch, hosting/deployment, tests.
- The frontend is specified at feature-area/contract level: shell, library, player/stats/resume, details/export/delete/auth/settings.
- The implementation plan is reviewed; its defects are amended or explicitly accepted.
- This digest plus the post-implementation review criteria are the handoff.

**In scope:** one `Microsoft.NET.Sdk.Web` project at `src/StreamKeepr`, tests at `tests/StreamKeepr.Tests`/`tests/StreamKeepr.E2E`, the Blazor Interactive Server UI, the HLS recorder/player, Twitch polling/EventSub/auth, the filesystem session store, MP4 export, delete, settings, Docker/Compose, and the fake harness.

**Non-goals (binding):**

- Implementing the app inside this map; running the post-implementation review here.
- Slicing the 63 frontend tasks (that is `to-tickets`).
- Rewriting the implementation plan document; amendments live here and in the register.
- Retention automation and retention options/claims (`ADR-0004`).
- Continuing the `languageext-migration` branch.
- Multi-variant playback/quality switching until a master playlist exists (E-20).
- A capture-schedule feature; the schedule button is a stub (E-02).
- Streamer group stop/resume menus and player title-strip stop controls (E-23).
- Per-user progress or per-user playback preferences.
- Public hosting, multi-tenant deployment, or third-party distribution support; the project scope is private home use (`#152`).

## 3. Decision ledger — every ticket

| Ticket | Decision | Binding artifact |
|---|---|---|
| [#2](https://github.com/brandtkstrom/StreamKeepr/issues/2) Target architecture and migration route | One Web SDK project at `src/StreamKeepr`; tests beside it; strangler migration; keep the old worker buildable until the final switch; port only salvageable Twitch/media/options pieces; minimal shell first. | [ADR-0001](adr/0001-one-web-project-strangler-migration.md) |
| [#3](https://github.com/brandtkstrom/StreamKeepr/issues/3) Plan review and amendments | 53 findings dispositioned; media/locking/retries/auth/buildability/health/export/dependency defects fixed; retention is a non-goal; export is one MP4; ADRs record the hard choices. | [Register](reviews/implementation-plan-amendments.md), [ADR-0002](adr/0002-media-exposure.md)–[ADR-0005](adr/0005-part1-part2-buildability.md) |
| [#4](https://github.com/brandtkstrom/StreamKeepr/issues/4) Domain model, events, and reducer | Pure reducer per channel; five closed states; `RecorderExitKind`; `HealthyPartSeconds`; offline is advisory while Recording; retry timers are skipped after offline; complete sessions recover as Finished and reopen at the next part; only `Paused` persists; commands execute in order. | [Domain](domain-model.md) |
| [#5](https://github.com/brandtkstrom/StreamKeepr/issues/5) Recorder process behavior on Linux | CliWrap signals one PID with no escalation; FFmpeg finalizes on stdin EOF; Streamlink exits 130 on signals, 0 on normal end and network starvation; `-progress` has no byte totals; the 60 s finalize max broke budgets; add `aac_adtstoasc`. | [Recorder](recorder-contract.md); research branch `research/recorder-process` (d0cb561) |
| [#6](https://github.com/brandtkstrom/StreamKeepr/issues/6) Recorder part lifecycle and finalize contract | Two CliWrap processes with a bounded 4 MiB pipe; fMP4 args plus `aac_adtstoasc`; codecs `h264,h265,av1`; bounded `Stopped`/`Killed`/`Exited`; byte progress from files; `PartProducingMedia` fallback; parts only; empty-part cleanup; no overwrite. | [Recorder](recorder-contract.md) |
| [#7](https://github.com/brandtkstrom/StreamKeepr/issues/7) Session store, stitching, recovery, and retention | Manifest v1 with derived summary fields; per-session locks; atomic writes; stitched `#EXT-X-MAP`/discontinuity with one final `ENDLIST`; recovery repairs/finalizes; in-memory library; `DiskGuard`; retention non-goal; live target-duration drift accepted. | [Session store](session-store-contract.md), [ADR-0004](adr/0004-retention-non-goal.md) |
| [#8](https://github.com/brandtkstrom/StreamKeepr/issues/8) Supervisor, coordinators, and channel control | One coordinator per channel with an unbounded single-reader queue; sequential command execution; fatal job-stop overrun stops the host; open/reopen failure skips the recorder; add validates through Helix; control operations serialize; `DvrStatus` splits `LastError`/`LastDetail`; no global recording cap. | [Runtime](runtime-contract.md) |
| [#9](https://github.com/brandtkstrom/StreamKeepr/issues/9) Twitch polling, EventSub, and auth | App-token cache/refresh/401 invalidation; 100-login Helix batches with `Unknown` on failure; push-aware 30 s/300 s polling with all-or-nothing coverage; EventSub backoff and polling fallback; HTTPS `/signin-twitch`; allowlist (empty = open admin, E-13); encrypted token store with Data Protection keys; manual subscriber token; push-aware liveness. | [Twitch/auth](twitch-auth-contract.md), [ADR-0003](adr/0003-admin-token-auth.md) |
| [#10](https://github.com/brandtkstrom/StreamKeepr/issues/10) Hosting, media security, health, and container | Forwarded-header/auth/antiforgery pipeline; allowlisted `/media` blocking metadata and dot-folders; live index endpoint; push-aware `/health/live` plus library-loaded readiness; Data Protection keys under `.streamkeepr/keys`; tool preflight; non-root image and Compose with secrets/volume/init/healthcheck/log rotation/50 s stop; HTTPS proxy external (E-15). | [Hosting](hosting-contract.md), [ADR-0002](adr/0002-media-exposure.md), [ADR-0005](adr/0005-part1-part2-buildability.md) |
| [#11](https://github.com/brandtkstrom/StreamKeepr/issues/11) Testing strategy and fake-Twitch harness | Unit/integration/bUnit/Playwright/Vitest/container layers; `IStreamPoller`/`IEventSubClient` seams; fake Streamlink variants and Mock profile; deterministic clocks/temp roots/jitter; split CI with warnings-as-errors, format, tests, JS, E2E, Docker; coverage collected without a gate; no retries; axe/keyboard and JS budget; visual regression dropped for now (#106). | [Testing](testing-contract.md) |
| [#12](https://github.com/brandtkstrom/StreamKeepr/issues/12) Backend surface for the UI (F10) | Nullable library fields and deterministic sort/state rules; `ChannelsView` with push status; progress file/endpoint; always-one-MP4 export with ffprobe compatibility, queue/progress/lease/cleanup/download and `FindByRecording`; Complete-only delete with lock/lease; storage monitor; actor-principal auth seams; F10-T1 absorbed; F10-T7 precedes sign-in. | [Backend UI](backend-ui-contract.md) |
| [#13](https://github.com/brandtkstrom/StreamKeepr/issues/13) Preserve planning inputs and acquire design assets | Plan committed; design system exported at `design-system/`; built `tokens.css`/`components.css`/`fonts.css`; no Google Fonts; system monospace stack; Material Symbols 0.48.3 and hls.js 1.7.4 vendored. | [Design assets](design-assets.md), `design-system/` |
| [#14](https://github.com/brandtkstrom/StreamKeepr/issues/14) Frontend foundation and app shell | Global Interactive Server with prerender; static `App.razor` + `MapStaticAssets`; `/` and `/r/{id}`; `q`/`state`/`sort`; `.sk-shell` drawer 320/72/hidden, theater, 960/600; versioned `sk-ui` cookie and `sk-tz` IANA with UTC fallback; scoped `LibraryView` at 500 ms; F0 primitives; Mock; reconnect/auth states; `prefs.js`/`AppShell.razor.js`; committed design-system CSS with sync deferred. | [Frontend foundation](frontend-foundation-contract.md) |
| [#15](https://github.com/brandtkstrom/StreamKeepr/issues/15) Library, search, and live updates | Recording-first streamer groups; newest/oldest/A-Z with deterministic ties; `q`/`state`/`sort` URL with 250 ms search and highlighting; viewer-zone day headers; selected rows as links; tree keyboard with one tab stop; all-DOM `content-visibility` instead of `<Virtualize>` (E-19); 500 ms `Changed` with targeted re-renders; F8-T3 player reactions delegated; F8-T4 paused note. | [Library](library-contract.md) |
| [#16](https://github.com/brandtkstrom/StreamKeepr/issues/16) Player, stats, and resume | One-time player/stats skeleton with JS-owned hls.js and controls; no autoplay; live edge at `liveSyncPosition` with one target-duration safety and explicit follow; live→VOD via ENDLIST; VOD→live reopen reloads at position; 15 s/pause/pagehide progress with live skip and near-end clear; direct resume; `sk-player` volume/mute/speed; 2 s/20 stats with copy JSON and native fallback; static Source quality (E-20); Chrome-channel E2E (E-21). | [Player](player-contract.md) |
| [#17](https://github.com/brandtkstrom/StreamKeepr/issues/17) Details, export, delete, auth UX, and settings | Details/chapters/absolute copy; inline export row with `FindByRecording` rehydration, one MP4, Cancel/Download/Retry, non-retryable incompatible parts (E-22); delete dialog with lease/live/unknown handling and next-item navigation; Twitch connect/expired/error/return-URL/logout-antiforgery UX; public `/settings` with account, push summary, add/remove/stop/resume; schedule stub; stop/resume surfaces reduced to Settings + details (E-23). | [Details/auth/settings](details-auth-settings-contract.md) |
| [#18](https://github.com/brandtkstrom/StreamKeepr/issues/18) Decision digest and post-implementation review criteria | This digest is the single `to-spec` entry point; it links every ticket, contract, ADR, and amendment; records accepted risks/open items; defines the post-implementation criteria, evidence, gates, and gap reporting. | This file |
| [#152](https://github.com/brandtkstrom/StreamKeepr/issues/152) Private-home-use project scope | StreamKeepr is a personal project for private home use. Public hosting, multi-tenant deployment, and third-party distribution are outside the support scope. The MIT license is unchanged; the statement describes intent and support, not a license restriction. | [README](../README.md), [Security](../SECURITY.md), [Hosting](hosting-contract.md) |

## 4. Backend decisions

### 4.1 Architecture and migration

- One `Microsoft.NET.Sdk.Web` project at `src/StreamKeepr`; one test project plus a separate E2E project; no premature class-library split.
- Strangler migration from `StreamCaptureService`: scaffold beside the old worker, port Twitch DTOs/source-generated JSON, options binding, the 401 refresh handler, and the pipeline shape; keep both buildable; delete the old worker and its tests at the final switch.
- Part 1 includes a minimal Blazor shell so the backend foundation builds and runs; the full UI is Part 2 (`ADR-0005`).
- Target .NET 10, CliWrap, Streamlink, FFmpeg, hls.js 1.7.4, Blazor Interactive Server.

### 4.2 Domain and reducer

- One pure reducer per channel; time and IDs arrive on events/settings; telemetry is not reducer state.
- States: `Idle`, `Recording`, `WaitingToRetry`, `CoolingDown`, `Paused`; `Recording` tracks `Attempt`, `HasMedia`, `FirstMediaAt`, `Streak`.
- `RecorderExitKind` (`Exited`, `Stopped`, `Killed`); a healthy part requires `HealthyPartSeconds` of media and a self-exit. Short/failed parts increment `Streak`; only a healthy part or a normal offline finalize resets it. The retry path is bounded into cooldown or pause.
- `Unknown` is never offline and never finalizes. Offline is advisory while `Recording`; two consecutive offline observations finalize a `WaitingToRetry` session.
- Complete sessions recover as `Finished`; the same stream returning reopens the finalized session at the next part; only the `Paused` flag persists.
- Commands execute in order; stale job events are filtered by `JobId`; `StopRequested` works from every state; `Paused` ignores observations until resume.

### 4.3 Recorder

- One `RecorderJob` records one part: Streamlink `--stdout` piped to FFmpeg `pipe:0` through a 4 MiB bounded `System.IO.Pipelines.Pipe` (resume at 2 MiB).
- Part files: `pNNN.m3u8`, `init_NNN.mp4`, `pNNN_%05d.m4s`; titles never enter paths; no overwrite; empty parts are removed.
- FFmpeg: fMP4 HLS, EVENT playlist, `+temp_file+independent_segments+program_date_time`, `-c copy`, always `-bsf:a aac_adtstoasc`; Streamlink codecs `h264,h265,av1`.
- Shutdown is bounded: graceful stop, then force kill; `FinalizeTimeoutSeconds` is capped at 15 s (default 10 s); host 40 s, Compose 50 s. `Stopped`/`Killed`/`Exited` drive the reducer.
- Byte/media progress comes from files and the progress pipe; stalls and media-producing thresholds are detected; `PartProducingMedia` is published once per part.
- Linux behavior (research #5): CliWrap signals one PID, FFmpeg finalizes on stdin EOF, Streamlink exits 130 on signals, `-progress` has no byte totals.

### 4.4 Session store

- `session.json` v1 (`Recording`/`Complete`), derived `RecordingSummary` fields: latest title/game, `PartCount`, `SegmentCount`, `EndedAt`, duration, media size, thumbnail, chapters, `IsRecording`.
- Every mutation takes the per-session lock: open, reopen, append metadata, finalize, delete, retention, export lease; atomic temp+rename writes; stale `.tmp` cleanup.
- Stitching: EVENT header, strip per-part headers, keep `#EXT-X-MAP`/program-date-time, insert discontinuity between non-empty parts, drop a trailing `EXTINF` without a URI, append `ENDLIST` only when final; target duration `max(2 × SegmentSeconds, part max)`.
- Chapters are metadata changes after the first, positioned against the stitched timeline.
- Recovery scans before channels start, skips dot folders and corrupt manifests, repairs/finalizes incomplete indexes, and classifies resumable/finished. The in-memory library raises `Changed`; `DiskGuard` gates recording; retention is a non-goal (`ADR-0004`).

### 4.5 Runtime

- `DvrSupervisor` is the singleton `BackgroundService` and `IChannelControl`; one `ChannelCoordinator` per watched channel with an unbounded single-reader `Channel<DvrEvent>`; publish never blocks or drops while running.
- Commands run sequentially; open/reopen failure skips the recorder and synthesizes one `RecorderExited`; finalize failure stays recoverable; retry timers use jitter; a job-stop overrun is fatal and stops the host; a channel-loop fault stops the host.
- Startup: load channels, resolve missing profiles, recover sessions, mark the library loaded, start coordinators, start poller/EventSub. Shutdown stops channels in parallel and finalizes; no global recording cap.
- `DvrStatus` exposes copied snapshots with `LastError`/`LastDetail`; `Changed` feeds the UI's 500 ms throttle.

### 4.6 Twitch, EventSub, and auth

- App token: client-credentials cache, refresh 5 minutes early, 401 invalidate/retry once, then `Unknown`.
- Poller: 100-login `/helix/streams` batches; absent = Offline; any batch failure = Unknown; `/helix/channels` for metadata; `/helix/users` for profile/UserId at add/seed.
- Poll schedule: 30 s normal, 300 s with full push coverage, 10 s burst for 3 minutes after a notification, nudge on add/resume; any recording/waiting forces the short interval.
- EventSub: user token, `stream.online`/`stream.offline` for every resolved channel, all-or-nothing coverage; 409 verified against the current session; 429 capacity keeps the covered subset and `PartialCoverage`; forced token refresh on 401; keepalive timeout; message-ID dedupe; concurrent reconnect handoff without resubscribe; revocation/reconnect; polling fallback. The WebSocket cost cap yields about five fully covered channels; more channels stay on the 30 s poll. Optional conduit transport (ADR-0007): app token, one saved conduit with one shard, reconcile on welcome, independent of admin sign-in; the WebSocket rules above apply to WebSocket mode.
- OAuth: `AspNet.Security.OAuth.Twitch`, callback `/signin-twitch`, no extra scopes, local-only return URLs, cookie `sk-auth` (HttpOnly, SameSite=Lax, 7-day sliding, secure under HTTPS), logout POST with antiforgery.
- Allowlist: non-empty restricts sign-in; empty admits every successfully authenticated Twitch user and logs a startup warning (E-13). Viewing/recording stay open.
- Admin token store: encrypted access/refresh tokens under `.streamkeepr/admin-tokens.json`, Data Protection keys under `.streamkeepr/keys`, refresh/rotation, clear on logout, `WaitForTokenAsync`; EventSub uses it only. Subscriber-only streams use a manual Streamlink browser token, not the admin token.
- Health: push-aware liveness (900 s with full coverage, 90 s otherwise), stale poll/stuck loop/stalled media unhealthy; retry/cooldown/Unknown degraded.

### 4.7 Hosting and deployment

- Middleware: forwarded headers (trusted proxies only), exception handler, authentication, authorization, antiforgery, `UseRecordingsMedia`, `MapStaticAssets`, Razor components, live index endpoint, health. No HTTPS redirect; TLS terminates at the proxy.
- `/media`: only `.m3u8`, `.m4s`, `.mp4`, `.jpg`, `.png`; dot-prefixed segments and traversal 404; `session.json`, `.streamkeepr`, exports, temp files never served; explicit content types; playlists `no-cache`, segments/init/thumbnails immutable; GET/HEAD/Range; missing = 404.
- `/health/live` runs DVR only; `/health/ready` runs DVR, disk, and library-loaded. Docker uses live.
- Data Protection keys persist under `.streamkeepr/keys`; tool preflight (`ffmpeg`, `ffprobe`, `streamlink`) and writable roots fail startup with clear ownership guidance.
- Container: multi-stage .NET 10 image, Python/Streamlink venv, static FFmpeg/ffprobe, non-root UID 1654, `/recordings` volume, `EXPOSE 8080`, healthcheck; Compose publishes plain HTTP 8080, uses secrets, init, log rotation, and 50 s stop grace; HTTPS is external (E-15).

### 4.8 Testing and CI

- Layers: unit, integration (`WebApplicationFactory`), bUnit, Playwright .NET against the Mock profile, Vitest/ESLint for JS, Docker/container. Highest seam that exercises real behavior; do not mock the reducer/session store.
- Seams: `IStreamPoller`, `IEventSubClient`, fake Streamlink, fake Twitch handlers, injected `TimeProvider`, temp roots, injected jitter. No test writes under the repo.
- Determinism: no automatic retries; flaky tests are fixed or quarantined with an issue and deadline; no silent skips.
- CI: restore/build warnings-as-errors/format, unit + bUnit, JS lint + Vitest, E2E with Playwright report on branded Chrome (E-21), Docker build; required to merge; NuGet/npm/browser caches; coverage collected without a gate; axe/keyboard; JS first-paint budget ≤200 KB gzipped; no visual regression gate (dropped, #106). Auth/export/delete/HTTP endpoints have integration tests.

### 4.9 Backend surface for the UI

- `ILibraryView` (`StreamerView`, `RecordingView`, `StorageView`, `LibraryQuery`): scoped, 500 ms `Changed`, nullable `LastRecordedAt`/`SizeBytes`/`ResumeSec`, deterministic sort/search/state rules, `PlaylistUrl` `/media/{channel}/{id}/index.m3u8`.
- `IChannelsView`: scoped, signed-in; list/add/remove/stop/resume; `ChannelView` with `PushConnected`/`PushReason`; service enforces authorization.
- `IProgressStore`: JSON file under `.streamkeepr`, in-memory reads, atomic writes at most every 10 s, clamp `[0,duration]`, clear within 30 s of end; public `PUT /api/recordings/{id}/progress`, 204/400/404, `no-store`.
- `IExportService`: always one MP4; auth; Complete-only; ffprobe compatibility; one at a time FIFO; progress from FFmpeg; lease registry; 24 h cleanup; signed-in range download; cancel; `FindByRecording` for rehydration.
- `IRecordingAdmin`: signed-in, Complete-only, session lock, lease refusal, removes library/progress; `IStorageMonitor` 60 s/event sampling with >1% threshold.
- Authorization: scoped views pass the authenticated `ClaimsPrincipal` to singleton services; disabled controls are never the only guard. F10-T1 absorbed; F10-T7 precedes sign-in.

## 5. Frontend decisions

### 5.1 Foundation and shell

- One Web project; Blazor Web App .NET 10, global Interactive Server with prerender; static `App.razor` (HeadOutlet, ImportMap, template reconnect, CSS), interactive `Routes`.
- Routes `/` and `/r/{id}`; library query `q`/`state`/`sort` in the URL with defaults; unknown id shows a message and link.
- `.sk-shell`: top app bar (menu, wordmark/REC dot, search, storage meter, schedule stub, settings, auth last), drawer 320/72/hidden, theater, main max 1100, player → details → stats. Breakpoints 960/600.
- `UiPreferences` scoped from versioned `sk-ui` cookie (4 KB/50 cap; defaults expanded/theater off/stats closed/details open); `ViewerClock` from `sk-tz` IANA with UTC fallback and one post-circuit sync.
- `LibraryView` scoped over the backend services; 500 ms `Changed`; shell uses it for REC dot/storage.
- Primitives: Button, IconButton, Icon, Chip, StatusChip, Avatar, Collapsible, Switch, Menu, Tooltip, Dialog, Snackbar; `RequiresTwitch` visible-but-inert; `/dev/components` Development-only.
- Mock profile: four streamers/eight recordings, growing capture, MockPoller, fake auth, fake Streamlink; no credentials.
- Reconnect: template modal for hard loss; `ReconnectNotice` for paused updates; never both. Skeleton/empty/unknown states.
- JS: `prefs.js`, `AppShell.razor.js`, collocated modules; hls.js only in the player; ImportMap; no Node at runtime. CSS order tokens → fonts → components; no Google Fonts; generated CSS committed; `sync:ds` deferred (E-18).

### 5.2 Library

- Recording-first streamer groups; `newest`/`oldest`/`az` with deterministic ties; recordings newest-first except oldest.
- URL `q`/`state`/`sort`; `state=recording` matches Recording/Reconnecting, `saved` matches Saved/Partial; search 250 ms debounce over title/category/streamer with `mark.sk-hl`.
- Viewer-zone day headers; empty groups/headers hide; no-results/empty messages.
- Drawer: streamer group (avatar/initials, live ring, count/state, chevron, persisted collapse), rows (thumbnail/duration/resume/live bar, two-line title, meta, status chip except Saved, selected `aria-current`), rail.
- Keyboard tree: one tab stop, roving focus, ↑/↓, Home/End, ←/→ collapse/expand, Enter opens, `aria-expanded`/`aria-current`; `/` focuses search, Esc clears.
- All-DOM long lists with `content-visibility`/`contain-intrinsic-size`; revisit at ~2,000 items (E-19).
- 500 ms `Changed` with `@key`/reused view models; storage/REC update without full drawer re-render; F8-T3 player reactions delegated; F8-T4 paused note.

### 5.3 Player, stats, and resume

- Static player/stats skeleton rendered once; `VideoPlayer.razor.js` owns hls.js, controls, dynamic DOM, idle, keyboard, progress timing, errors; `stats.js` owns stats DOM. No Blazor re-render of JS-owned DOM.
- hls.js lazily imported from `wwwroot/lib/hls.js/hls.mjs`; native HLS fallback; no autoplay.
- Live edge = `hls.liveSyncPosition` with `liveSyncDurationCount: 1`, `liveMaxLatencyDurationCount: Infinity`, `maxLiveSyncPlaybackRate: 1`, `backBufferLength: 90`; red tick at seekable end; Live edge button follows; manual seek back/pause stops following.
- Live→VOD via ENDLIST; VOD→live reopen reloads at the current position; deleted recording → snackbar + next item.
- Controls per design: play/skip/volume/timecode/live edge/speed/static Source/stats/PiP/theater/fullscreen; responsive hiding; seek slider commits once, chapter ticks/tooltips, ARIA; keyboard Space/K, J/L, ←/→, M, F, T, I with yield rules.
- Resume: Completed starts at `ResumeSec` (no prompt); live ignores; save 15 s/pause via `ProgressStore`, flush on route/dispose/pagehide with keepalive; skip live; clear on ended/near end; no timeupdate circuit; no cross-tab sync.
- Preferences: `sk-player` v1 volume/mute/speed; clamped/defaulted; no quality preference (E-20); localStorage failures degrade.
- Stats: 2 s/20-sample rings; Video bitrate/frame rate, Network bandwidth/buffer; native fallback; copy JSON v1 via clipboard + snackbar; open state `sk-ui` only; collapsed body inert.
- Errors: three retries with backoff; media recovery/swap audio; waiting-for-first-segment; unsupported-browser overlay; retry at last position.
- Known: live edge ~one target duration behind; no captions; native Safari not tested on a device (#108); quality switching deferred.

### 5.4 Details, export, delete, auth, settings

- Details: `Collapsible`/`DetailsOpen`; title, streamer, category, viewer-zone date, counts/size, chapters seek via `PlayerBridge`; Copy playlist absolute from `BaseUri` + `PlaylistUrl`, public, clipboard/snackbar.
- Export: `FindByRecording` rehydration; inline row Queued/Running + percent/Cancel, Succeeded + Download, Failed + Retry, Cancelled + Retry; `ExportIncompatibleParts` non-retryable; one MP4; progressbar; completion/failure snackbar; download anchor full-page, signed-in; lease blocks delete (E-22).
- Delete: dialog names recording, `error-container`, Danger/Text, Complete-only, live/leased refusal, busy, success snackbar + next newest, unknown/unauthorized paths; exported file survives 24 h (known).
- Auth: full-page Connect with local return URL and `data-enhance-nav=false`; connected account chip; Manage → `/settings#twitch`; Disconnect antiforgery form post; expired provider signs out, top-bar expired chip/Reconnect, playback continues; `authError` codes (`notAllowed`, `httpsRequired`, `failed`) snackbar and query strip; HTTPS caveat; empty allowlist open-admin (E-13).
- Settings: public `/settings`; signed-out connect prompt; account; push summary and per-row badges; sorted channel list; add-by-login with inline Helix errors; Stop confirm/Resume; Remove confirm with recordings kept and Removing… up to 35 s; `ChannelsView.Changed`; schedule stub snackbar.
- Stop/resume surfaces: Settings + details-card Stop while live; library Paused/no ring; no group menu/player title-strip control (E-23).
- Auth enforcement: `RequiresTwitch` plus service checks; delete/export/download/channels; logout antiforgery; no tokens in browser.

## 6. ADRs

| ADR | Decision |
|---|---|
| [ADR-0001 One Web project and strangler](adr/0001-one-web-project-strangler-migration.md) | One Web SDK project; migrate beside the old worker; port salvageable pieces; delete the old worker at the switch; no class-library split yet. |
| [ADR-0002 Media exposure](adr/0002-media-exposure.md) | `/media` is an allowlisted surface, not a static root; dot segments/traversal/metadata/exports are 404. |
| [ADR-0003 Admin token auth](adr/0003-admin-token-auth.md) | Admin-only Twitch OAuth; server-side encrypted token store; Data Protection keys; EventSub only while a token exists. |
| [ADR-0004 Retention non-goal](adr/0004-retention-non-goal.md) | No retention automation/options; manual delete; `MinFreeGb` still stops recording. |
| [ADR-0005 Part 1 buildability](adr/0005-part1-part2-buildability.md) | Part 1 includes a minimal Blazor shell so the backend foundation builds/runs; full UI remains Part 2. |
| [ADR-0006 EventSub reconnect handoff](adr/0006-eventsub-reconnect-handoff.md) | Keep the old socket until the new welcome; drain both; skip resubscribe; dedupe the overlap. |
| [ADR-0007 EventSub conduit transport](adr/0007-eventsub-conduit-transport.md) | `Twitch:EventSubTransport` = `WebSocket` (default) or `Conduit`; conduit mode uses the app token, one conduit and shard, and does not depend on admin sign-in. |

## 7. Plan amendment ledger

The register holds the plan-line evidence and the full finding text. This table records the binding amendment/disposition and the artifact that carries it. All 53 findings are accounted for.

### A. Correctness, security, and data integrity

| ID | Binding amendment | Disposition | Artifact |
|---|---|---|---|
| A-01 | Healthy-part threshold (`HealthyPartSeconds`); short parts count toward `Streak`; bounded retry/cooldown/pause. | Fix | `domain-model.md` |
| A-02 | `/media` extension allowlist; dot-segment rejection; 404 for metadata/exports/temp. | Fix | `hosting-contract.md`, ADR-0002 |
| A-03 | Lock every session mutation; define live-stitch snapshot; concurrent finalize/delete/stitch tests. | Fix | `session-store-contract.md` |
| A-04 | Define `IAdminTokenStore` save/clear/wait; backend foundation + minimal shell. | Fix | `twitch-auth-contract.md`, ADR-0005 |
| A-05 | Resolve `UserId` at channel add/seed; EventSub covers resolved channels; unresolved polling-only. | Fix | `twitch-auth-contract.md` |
| A-06 | Push-aware liveness: 90 s polling / 900 s full coverage. | Fix | `hosting-contract.md`, `twitch-auth-contract.md` |
| A-07 | Cap `FinalizeTimeoutSeconds` at 15 s (default 10); host 40 s/Compose 50 s. | Fix | `recorder-contract.md`, `hosting-contract.md` |
| A-08 | Retention automation non-goal; remove options/hourly-service claims. | Accept (non-goal) | ADR-0004 |
| A-09 | Latest metadata supplies both title and game; chapters keep history. | Fix | `session-store-contract.md` |
| A-10 | Persist Data Protection keys before token storage; container verifies. | Fix | `twitch-auth-contract.md`, `hosting-contract.md` |
| A-11 | Always pass `-bsf:a aac_adtstoasc`; test arguments. | Fix | `recorder-contract.md` |

### B. Buildability and dependency graph

| ID | Binding amendment | Disposition | Artifact |
|---|---|---|---|
| B-01 | Minimal Blazor shell in the backend foundation; full UI remains Part 2. | Fix | ADR-0005 |
| B-02 | Break F6-T1/F10-T7 cycle; F10-T7 precedes sign-in. | Fix | `backend-ui-contract.md` |
| B-03 | Split CI into F9-T5a skeleton and F9-T5b full gates. | Fix | `testing-contract.md` |
| B-04 | Add missing dependency edges; recompute waves. | Fix | Register “Corrected dependency edges” |
| B-05 | Move `PartCount`/`SegmentCount`/`EndedAt`/profile/UserId into Part 1; F10-T1 absorbed. | Fix | `backend-ui-contract.md` |
| B-06 | Visual baselines depend on design assets/foundation/settings; blocked until approved. Superseded: visual regression dropped on 2026-11-12 (#106). | Delegate | `testing-contract.md`, `design-assets.md` |
| B-07 | Docker gate belongs to F9-T5b after the container task. | Fix | `testing-contract.md` |

### C. Contract gaps

| ID | Binding amendment | Disposition | Artifact |
|---|---|---|---|
| C-01 | Always one MP4; incompatible parts fail with `ExportIncompatibleParts`; no ZIP; queue/progress/lease/cleanup/download. | Fix | `backend-ui-contract.md` |
| C-02 | Scoped actions pass `ClaimsPrincipal` to singleton export/delete; server enforces. | Fix | `backend-ui-contract.md` |
| C-03 | Complete auth options/callback/cookie/logout/return URL/token store/scopes/subscriber token. | Fix | `twitch-auth-contract.md` |
| C-04 | Define `ChannelView`/`ChannelsView` and push reason; expose to settings. | Fix | `backend-ui-contract.md` |
| C-05 | Nullable `StreamerView.LastRecordedAt?`; zero-recording channels in Settings. | Fix | `backend-ui-contract.md`, `library-contract.md` |
| C-06 | Define export/delete lease registry; delete refuses held leases; release on completion/failure/cancel. | Fix | `backend-ui-contract.md` |

### D. Frontend design gaps (delegated and resolved)

| ID | Resolution | Artifact |
|---|---|---|
| D-01 | Design system, fonts, hls.js, and licenses acquired/vendored. | `design-assets.md`, `design-system/`, `wwwroot/` |
| D-02 | `prefs.js` owns `sk-tz`; `AppShell.razor.js` owns `.scrolled`. | `frontend-foundation-contract.md` |
| D-03 | Keyboard precedence defined across tree, player, search. | `library-contract.md`, `player-contract.md` |
| D-04 | Sort ties and zero-recording streamers specified. | `library-contract.md` |
| D-05 | Stats/player shared state, chapter seek, live/VOD/deleted navigation, all-DOM lists specified. | `player-contract.md`, `library-contract.md`, `details-auth-settings-contract.md` |
| D-06 | Mock seeder/fake Streamlink fixtures specified; implementation in tests. | `testing-contract.md`, `frontend-foundation-contract.md` |

### E. Accepted choices, risks, and deferrals

| ID | Decision | Status | Evidence / artifact |
|---|---|---|---|
| E-01 | Thumbnail strategy: Helix live preview vs frame grab. | **Open** | Digest §8.2; `backend-ui-contract.md` keeps `ThumbnailUrl` nullable |
| E-02 | Schedule button. | **Resolved: stub with snackbar** | `details-auth-settings-contract.md` §8 |
| E-03 | Retention automation/defaults. | Accept (non-goal) | ADR-0004 |
| E-04 | Allowlist logins and viewing without sign-in. | **Resolved semantics**: empty = open admin (E-13); viewing public; exact values deployment config | `twitch-auth-contract.md` §6 |
| E-05 | HTTPS/reverse proxy for sign-in. | **Resolved**: external TLS proxy; HTTPS/localhost only (E-15) | `hosting-contract.md` §8, `twitch-auth-contract.md` §6 |
| E-06 | Text webfont licensing. | **Resolved: no text webfont bundled**; UI uses the system monospace stack, and Material Symbols Rounded is Apache-2.0 | `design-assets.md` |
| E-07 | Subscriber-only token handling. | **Resolved mechanism**: manual `Recorder:TwitchUserOAuthToken`; Settings entry deferred | `twitch-auth-contract.md` §8 |
| E-08 | Hover thumbnails dropped. | Accept | — |
| E-09 | No hardware target; defaults stand. | Accept | — |
| E-10 | HEVC/AV1 alongside H.264; player surfaces unsupported codecs; MP4 tags HEVC `hvc1`; test. | Accept (risk) | `recorder-contract.md`, `player-contract.md`, `backend-ui-contract.md` |
| E-11 | Live target-duration drift tolerated; final index stable. | Accept (risk) | `session-store-contract.md` |
| E-12 | No global concurrent-recording cap. | Accept | `runtime-contract.md` |
| E-13 | Empty allowlist = any authenticated Twitch user is admin; startup warning. | Accept (risk) | `twitch-auth-contract.md` |
| E-14 | Subscriber-token Settings entry. | Defer | `twitch-auth-contract.md` §8 |
| E-15 | HTTPS external; Compose HTTP 8080; LAN HTTP cannot administer. | Accept | `hosting-contract.md` |
| E-16 | Coverage collected, no percentage gate initially. | Accept | `testing-contract.md` |
| E-17 | No automatic test retries; fix/quarantine with an issue. | Accept | `testing-contract.md` |
| E-18 | Design-system CSS committed; `sync:ds` deferred. | Accept | `frontend-foundation-contract.md` |
| E-19 | All-DOM library with `content-visibility`; revisit ~2,000 items. | Accept (deviation); measured at 2,019 items on 2026-11-11 with no need to virtualize (#110) | `library-contract.md` §7 |
| E-20 | Quality menu/preference deferred; static Source. | Accept (deferral) | `player-contract.md` |
| E-21 | E2E on branded Chrome; native/fullscreen/PiP unit/manual. | Accept (environment) | `testing-contract.md`, `player-contract.md` |
| E-22 | Inline export row + `FindByRecording`; one MP4. | Accept (deviation/addition) | `details-auth-settings-contract.md` |
| E-23 | Stop/resume in Settings + details; no group menu/player strip. | Accept (deviation) | `details-auth-settings-contract.md` |

## 8. Accepted risks and open items

### 8.1 Accepted risks (carry into the spec and review)

| Risk | Why accepted | Mitigation / evidence |
|---|---|---|
| HEVC/AV1 playback/export compatibility | Twitch codecs enabled; browser/export support varies | Player error overlay for unsupported codecs; MP4 HEVC `hvc1`; test fixtures; E-10 |
| Live target-duration drift | A later part with longer segments can raise the live target; hls.js tolerates it | Final index is stable; revisit if a strict client appears; E-11 |
| Open administration with an empty allowlist | Explicit opt-in for personal use | Startup warning; set the allowlist on shared networks; E-13 |
| Plain LAN HTTP cannot administer | Twitch requires HTTPS except localhost | External TLS proxy; documented; E-15 |
| No coverage percentage gate initially | Avoid blocking early work on an arbitrary number | Collect coverage; add a threshold later; E-16 |
| No automatic test retries | Keep flakes visible | Fix/quarantine with an issue and deadline; E-17 |
| Quality switching absent | One captured variant/media playlist | Static Source; E-20 |
| Chrome-only playback E2E | Playwright Chromium lacks H.264 | Branded Chrome in CI; native Safari not tested on a device (#108); E-21 |
| Stop/resume surfaces reduced | Player DOM is JS-owned; settings is the management surface | Settings + details Stop; library Paused; E-23 |
| No global recording cap | Channel list is user-controlled | Add only under disk/bandwidth pressure; E-12 |
| Manual subscriber token | No safe automated flow yet | Secret rotation documented; Settings entry deferred; E-07/E-14 |
| Font redistribution | Bundled font assets | Material Symbols Rounded only (Apache-2.0); UI text uses system fonts; E-06 |
| No visual regression gate | Dropped by the owner for now (#106) | bUnit markup, axe, and E2E journeys cover the UI; harness kept on branch `test/106-visual-baselines` |
| No captions | Recorder captures no subtitle track | Documented limitation; axe caption best-practice not a gate |
| Native Safari/fullscreen/PiP not tested on a device | Not deterministic in CI, and no iOS device is available | Unit tests with stubs only; manual pass dropped on 2026-11-12 (#108); checklist kept in `docs/manual/` |
| Export file survives recording deletion | Export is a derived artifact; 24 h cleanup | Documented; delete dialog does not promise otherwise |
| Export jobs are in-memory | Process-wide service; restart loses jobs | Completed file may remain; user can re-export |
| Thumbnail strategy undecided | No source chosen yet | `ThumbnailUrl` nullable; spec must not assume Helix vs frame grab |
| Exact allowlist values | Deployment-specific | Config; empty = open admin; E-13 |

### 8.2 Open items (explicit, non-blocking unless stated)

| Item | Status | Owner / impact |
|---|---|---|
| Thumbnail strategy: Helix live preview vs a frame grab from the recording | Open product choice | Backend UI; `ThumbnailUrl` nullable, so the spec can proceed but must not assume a source |
| Exact `Auth:AllowedTwitchLogins` values | Deployment config | Operator; semantics fixed (empty = open admin); set for shared networks |
| Subscriber-token rotation UI in Settings | Deferred | Auth/settings; manual secret works today |
| Quality menu/preference | Deferred until a multi-variant master playlist exists | Player; E-20 |
| Streamer group/player stop-resume surfaces | Deferred | Details/settings; E-23 |
| Native Safari, fullscreen, PiP transitions | Unit-tested only; manual pass dropped (#108) | Accepted risk; unit tests cover stubs |
| Captions | Not captured/available | Product limitation; no caption control |
| Post-implementation review findings | Future | Produced by the review in §9; not a map decision |

Resolved and not open: schedule behavior (stub + snackbar), retention (non-goal), HTTPS (external proxy), font licensing (no text webfont bundled), and allowlist semantics (E-13). Deferred choices remain E-20 and E-23.

## 9. Post-implementation conformance review

### 9.1 Purpose and timing

Run after implementation, before release, **from this digest alone** plus the binding contracts. It verifies that the built app matches the contracts and that every accepted risk/deviation is intentional. It does not rewrite the plan or silently accept gaps. Output is a review report and, for each gap, a new issue.

### 9.2 Method

Repeat the register's method against the built app:

1. **Source check** — every binding claim is checked against the contract, ADR, or amendment; plan code is reference.
2. **Scenario walkthrough** — crash during a part, offline blip, ad break, disk full, delete during export, restart during recording, EventSub outage, token expiry, HTTPS/open-admin, player live→VOD, resume, 2,000-item library, incompatible export.
3. **Graph check** — the built tasks/services match the corrected dependency edges; no cycles; the migration checkpoints exist.
4. **Security check** — `/media` allowlist/traversal/metadata, auth/antiforgery/return URLs, token encryption/keys, secrets/logs, open-admin warning.
5. **Evidence format** — finding ID, contract clause, scenario, evidence, severity, disposition, owner, binding sentence.
6. **Gap reporting** — new issue per gap; never silently accept; no test skipping/weakening.

### 9.3 Criteria

**Architecture and build**

- One Web SDK project at `src/StreamKeepr`; tests at `tests/StreamKeepr.Tests`/`StreamKeepr.E2E`; the old worker is deleted at the final switch; no unintended project split.
- Build warnings-as-errors, format check, restore, publish, and Docker build pass.
- The minimal shell and full UI build in the intended Part 1/Part 2 sequence.

**Domain, recorder, session, runtime**

- Every reducer transition/invariant has a test; healthy-part/streak/cooldown/pause/reopen/offline rules match `domain-model.md`.
- Recorder args include fMP4, `aac_adtstoasc`, `h264,h265,av1`; the two-process pipe is bounded; stop/finalize budgets match; no overwrite; empty-part cleanup; exit kinds correct.
- Manifest v1/derived fields/locks/atomic writes/stitching/recovery match `session-store-contract.md`; target-duration drift is the accepted risk; chapters position correctly.
- Coordinator queue/command order/fatal overrun/open-failure/retry/status/startup/shutdown match `runtime-contract.md`; no global cap.
- Scenario tests: crash during part, offline blip, ad break, disk full, stop/resume, restart recovery, concurrent finalize/delete/stitch.

**Twitch, auth, hosting**

- App token cache/refresh/invalidate; poller batching/Unknown; poll schedule/burst/push coverage; EventSub lifecycle/backoff/coverage.
- OAuth callback, local return URLs, allowlist (including empty warning), cookie flags, logout antiforgery, non-HTTPS rejection, forwarded headers.
- Token store encryption/refresh/clear/wait/keys; no token in logs/browser/cookies.
- `/media` allowlist/traversal/metadata/exports blocked; content types/cache/Range; live/final index 404 behavior.
- Health budgets, readiness library/disk, preflight, non-root container, volume, secrets, init, healthcheck, log rotation, stop grace, deep links, Data Protection persistence.

**Backend UI**

- `ILibraryView` shape/state/sort/search/throttle/nullable fields; `ChannelsView` auth/push; progress endpoint/30 s/atomic writes; export one-MP4/queue/lease/cleanup/cancel/download/`FindByRecording`/incompatible; delete Complete/lock/lease; storage threshold; actor-principal seams.

**Frontend**

- Shell/routes/prefs/clock/`LibraryView`/primitives/Mock/reconnect; library grouping/sort/search/filter/keyboard/all-DOM/live updates; player engine/live edge/handoffs/resume/prefs/stats/copy/chapters/errors; details/export/delete/auth/settings/stop-resume.
- Accessibility: axe clean, keyboard-only reaches every control, labels/ARIA/focus return, reduced motion; no hidden/inert focus traps.
- Performance: first-paint JS ≤200 KB gzipped; hls.js/player only on recording pages; no timeupdate circuit traffic.
- Browser: Chrome playback E2E; native Safari and fullscreen/PiP unit-tested only (manual pass dropped, #108).

**Licensing and docs**

- No text webfont is bundled; Material Symbols Rounded is Apache-2.0 and committed.
- Material Symbols/hls.js licenses present; no Google Fonts; no CDN at runtime.
- Contracts, ADRs, digest, and code agree; deviations recorded, not silently accepted.

### 9.4 Acceptance gates

A gate is **blocking** unless the owner explicitly accepts the risk in writing in the review report.

- **G1 Build/test:** build, format, unit, integration, bUnit, Vitest, Playwright (Chrome), and Docker gates pass; no required test is skipped; no automatic retries.
- **G2 Media security:** allowlist/traversal/metadata/exports tests pass; no unintended file served.
- **G3 Auth:** sign-in/allowlist/HTTPS/return URL/logout antiforgery/token encryption/keys tests pass; no secrets/tokens exposed; open-admin warning present.
- **G4 Recording integrity:** finalize/shutdown/recovery/concurrency scenarios pass; no lost or overwritten parts; 15 s finalize cap respected.
- **G5 Data actions:** one MP4, incompatible failure, export lease/delete, progress/resume, download auth/range pass.
- **G6 UI conformance:** shell/library/player/details/auth/settings behaviors match contracts; axe/keyboard/reduced motion pass; performance budget passes.
- **G7 Container/ops:** non-root, health, volume, secrets, stop grace, deep links, Data Protection restart pass.
- **G8 Licensing:** only Apache-2.0 Material Symbols Rounded is bundled; no Google Fonts.
- **G9 Documentation:** every binding claim is traceable; every deviation/risk has an owner and disposition.

### 9.5 Gap reporting

- One issue per gap, labelled for triage, linking the contract clause and this digest; include severity, scenario, evidence, owner, and proposed disposition.
- **Critical:** security exposure, data loss/corruption, broken auth/media allowlist, missing recovery, license violation, secrets in artifacts → blocks release.
- **Major:** user-visible contract violation, missing required test, broken export/delete/resume, a11y blocker → blocks release unless accepted with a dated follow-up.
- **Minor:** copy/layout/performance polish → may ship with a linked issue.
- **Observation:** no action needed; record for context.
- Do not fix silently during the review; fixes are separate issues/PRs. Do not weaken tests to pass a gate.

## 10. `to-spec` handoff

Produce one specification that:

1. States the destination, scope, precedence, and non-goals from §1–§2.
2. Carries the backend contracts in implementation order: architecture/migration, domain/reducer, recorder, session store, runtime, Twitch/auth, hosting, backend UI, testing.
3. Carries the frontend feature-area contracts: foundation/shell, library, player/stats/resume, details/export/delete/auth/settings.
4. Preserves the binding amendments A-01–A-11, B-01–B-07, C-01–C-06, the resolved D-01–D-06 frontend gaps, and the accepted/deferred E items with their artifacts.
5. Keeps the open items explicit; do not invent a thumbnail source or an allowlist value.
6. Includes acceptance criteria, scenario tests, security/a11y/performance gates, and the post-implementation review above.
7. Keeps the 63 frontend task slices out; `to-tickets` follows the spec.

The spec is decision-complete when a reader can implement backend-first, then the feature-area UI, without returning to the plan or the issue tracker for a binding choice. The plan remains the source for task IDs/waves and historical wording only.

## 11. Related

- Map: [Stream Keepr v3 app specification route](https://github.com/brandtkstrom/StreamKeepr/issues/1).
- Decision tickets: #2–#18 (table in §3).
- Contracts: [domain](domain-model.md), [recorder](recorder-contract.md), [session store](session-store-contract.md), [runtime](runtime-contract.md), [Twitch/auth](twitch-auth-contract.md), [hosting](hosting-contract.md), [testing](testing-contract.md), [backend UI](backend-ui-contract.md), [frontend foundation](frontend-foundation-contract.md), [library](library-contract.md), [player](player-contract.md), [details/auth/settings](details-auth-settings-contract.md).
- ADRs: [0001](adr/0001-one-web-project-strangler-migration.md)–[0005](adr/0005-part1-part2-buildability.md).
- [Plan-review register](reviews/implementation-plan-amendments.md), [plan](plans/streamkeepr-implementation-plan.md), [design assets](design-assets.md), [glossary](../GLOSSARY.md).
