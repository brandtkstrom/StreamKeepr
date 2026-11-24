# Stream Keepr Testing Contract

Status: accepted (2026-11-10)
Source: Implementation plan Testing and verification; Part 2 F9; plan review B-03/B-04/B-06/B-07; runtime, recorder, session-store, Twitch, and hosting contracts.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract defines the test seams, projects, fake-Twitch harness, determinism rules, CI gates, and per-area coverage. It replaces the current vacuous `StreamCaptureService.Tests` project when the new suite lands.

## 2. Test layers

| Layer | Project | Scope |
|---|---|---|
| Unit | `tests/StreamKeepr.Tests` | Pure reducer, stitcher, parser, recovery, args, path/media rules, poll schedule, token cache, allowlist logic, IRC/Twitch chat parser, chat timeline, chat log and purge, third-party emote providers and cache (BetterTTV, 7TV) |
| Integration | `tests/StreamKeepr.Tests` | `WebApplicationFactory`: media, health, auth, progress, export, channel, chat, and emote catalog endpoints; real DI with fake external services |
| Component | `tests/StreamKeepr.Tests` | bUnit: primitives, library, player shell, stats, chat pane, details, auth, settings |
| JS | `tests/js` | Vitest: timecode, preferences, tree navigation, shortcuts, sparkline, player pure logic, chat playhead/reveal/sync, third-party emote matching |
| Process | `tests/StreamKeepr.Tests` | The built app as a child process, only for behavior `WebApplicationFactory` cannot observe: fatal startup logging, log flush, and exit code. Temp roots, no network, killed on timeout |
| E2E | `tests/StreamKeepr.E2E` | Playwright .NET against the Mock profile: journeys, chat replay, emote replay, axe, keyboard, screenshots |
| Container | `tests/StreamKeepr.E2E` or CI | Docker build, non-root smoke, health, deep links, restart |

Use the highest seam that still exercises real behavior. Do not mock the reducer, stitcher, session store, or other internal domain objects.

## 3. Projects and layout

```text
tests/
  StreamKeepr.Tests/          xUnit + bUnit; references src/StreamKeepr
    Domain/
    Storage/
    Media/
    Runtime/
    Twitch/
    Hosting/
    Ui/
  StreamKeepr.E2E/            Playwright for .NET
  js/                         package.json, Vitest, ESLint (dev-only)
  fixtures/
    fake-streamlink*          shell scripts, executable
    media/                    generated at test time; no large committed binaries
```

- `tests/StreamKeepr.Tests` references `src/StreamKeepr` directly. No separate test-only public API.
- `src/StreamKeepr` grants `InternalsVisibleTo` to `StreamKeepr.Tests` so unit and integration tests can exercise internal seams (emote providers, catalog service, channel resolver) without widening the public surface. Do not add test-only public members to production types; cover new internals through that binding.
- `tests/StreamKeepr.E2E` starts the app with the `Mock` launch profile on a free loopback port, waits for `/health/ready`, then runs Playwright.
- The E2E fixture runs one Debug publish per run: the folder named by `STREAMKEEPR_E2E_PUBLISH_DIR` when it is set (CI publishes once in a workflow step), otherwise its own `dotnet publish`. It starts `StreamKeepr.dll` with `ASPNETCORE_ENVIRONMENT=Mock` on a free loopback port, then runs Playwright. It keeps the source project as the content root for the Mock fixture path and points `ASPNETCORE_WEBROOT` at the publish output. The shared publish is a stable test artifact, not a workaround: the Mock host enables build-time static web assets outside Development, so `_framework/*.js` is also served from a normal build.
- `tests/js` is dev-only; Node never enters the image.
- The old `StreamCaptureService.Tests` project is deleted with the old app in the migration route.

## 4. Seams

- Add `IStreamPoller` and `IEventSubClient` interfaces; the production classes implement them, and the Mock profile registers fakes. The supervisor depends on the interfaces.
- Inject `TimeProvider` everywhere. Inject retry jitter/delay (`IRetryDelay` or `Func<TimeSpan, TimeSpan>`) so tests can make it deterministic; production uses random jitter.
- Use `IHttpClientFactory` with stub `HttpMessageHandler`s for Helix, token refresh, and EventSub subscription requests. No test calls Twitch.
- Use an in-memory `IAdminTokenStore` for auth/EventSub tests; the encrypted implementation is tested separately with a temp directory.
- Use `IIrcSocket`/`IIrcSocketFactory` with a scripted transcript for the IRC state machine and parser. The Mock profile uses `MockChatSource`; no test connects to Twitch IRC or an external IRC network.
- Use a temp `StorageOptions.RecordingsRoot` and `WebApplicationFactory` settings per test class. Never write under the repo.
- `StreamKeeprAppFactory` returns its host only after startup recovery has loaded the library. Recovery runs after the server starts listening and replaces the library's contents, so seeding earlier races the scan. A test that expects recovery to fail and the host to stop passes `waitForStartupRecovery: false`.
- Fake `IChannelControl`/`IRecordingActions` only at the UI seam; do not fake the supervisor when testing it.

## 5. Fake Twitch harness

### Fake Streamlink

Commit executable scripts under `tests/fixtures/`:

- `fake-streamlink` — generates a short test stream to stdout with ffmpeg (for example 5–10 s of `testsrc2` + sine, MPEG-TS). The plan's 10-minute script is for manual/soak runs; automated variants use short media.
- `fake-live-streamlink` — streams until stopped, for user-stop, runtime, and Mock captures.
- `fake-streamlink-broken` — answers `--version` but exits 1 otherwise, so preflight passes and recording fails.
- `fake-streamlink-slow` — writes a partial source to exercise backpressure.
- `fake-hanging-streamlink` / `fake-hanging-ffmpeg` — ignore their graceful signal, for the worst-case stop budget and the process runner's force kill.

Recorder job scenarios that only need tool output (no media, stall, ad break, a failing Streamlink, token echo, and hostile stderr) run through the fake process runner, not shell scripts; see `docs/recorder-contract.md` §10.

Rules:

- Scripts are POSIX `sh`, executable, and use only `ffmpeg` and standard shell tools.
- CI installs ffmpeg; tests fail with a clear message if it is missing.
- No network access; no Twitch credentials.
- The recorder integration tests set `Recorder:StreamlinkPath` to the script and `Recorder:FfmpegPath` to the real ffmpeg; the recordings root is a temp directory.
- `-bsf:a aac_adtstoasc` is exercised by an MPEG-TS/AAC fixture.

### Mock launch profile

`Mock` registers:

- `MockSeeder`: four streamers, eight recordings (finished VOD, two-part partial, growing EVENT capture), thumbnails, chapters. `Mock:Seed:ScaleRecordings` (default 0) adds finished archive recordings without media segments for the library scale measurement (#110). `Mock:Seed:Enabled=false` starts with no channels or recordings; `EmptyLibraryJourneyTests` uses it for the first-run empty states (#185).
- `MockPoller` / `MockEventSub`: deterministic observations and push notifications driven by `FakeTimeProvider` or explicit test controls.
- Fake signed-in and signed-out auth states; no OAuth.
- `fake-streamlink` for the growing capture.
- `MockChatSource` for deterministic live chat and `MockChatSeeder` for finished-recording chat fixtures.
- No Twitch client credentials are required.

`dotnet run --launch-profile Mock` is the local path. The E2E fixture may run the published Debug output with the Mock environment as described in §3 when the SDK static-asset handler cannot serve the framework assets. Plain `dotnet run` is the real recorder.

## 6. Determinism and isolation

- Use `Microsoft.Extensions.TimeProvider.Testing` `FakeTimeProvider`; no real `Task.Delay`, clocks, or timers in unit tests.
- Fixed `T0`, deterministic stream IDs, deterministic session IDs.
- Inject retry jitter; production randomness never enters assertions.
- Unique temp directory per test; fixture cleans it up. Tests never write to the repo, user profile, or `/recordings`.
- No network; stub HTTP handlers; fake processes.
- No shared static mutable state. Use xUnit collections only when a real shared resource is unavoidable.
- The E2E suite's one exception is `E2EBrowser`: one Playwright driver and one Chrome per test process, guarded by a lock. Each test still opens and disposes its own `IBrowserContext`, which isolates cookies, storage and permissions, and never closes the shared browser.
- Dispose services, `WebApplicationFactory`, child processes, and temp directories in `IAsyncLifetime`/`IDisposable`.
- Tests must pass in any order and in parallel unless explicitly isolated.

### 6.1 E2E readiness, reconnect and failure evidence

- A navigation is not complete when the document loads. `Journey.GotoAsync` and `Journey.ReloadAsync` wait for the shell's interactive marker (`input[data-search-input][data-sk-ready]`) or the sign-in page (`[data-signin]`). A journey that opens a recording waits for `[data-sk='seek'][aria-disabled='false']` (via `Journey.OpenRecordingAsync`) before it presses or clicks. New journeys use `Journey`, not a raw `IPage.GotoAsync`.
- After a click that may navigate, wait for the current URL (`Journey.WaitForUrlAsync`) rather than for a future navigation event: the click can finish before `WaitForURLAsync` starts, which was the sign-in race in #296.
- Before an action, wait for `body[data-sk-reconnect]` to be absent or `hide`. `Journey.ActAsync` and the library helpers do this; a hard-loss state fails immediately with the URL, reconnect state, focused element and the last app log lines instead of waiting for Playwright's 30 s default.
- The library keyboard handoff re-presses Right when focus stayed on a channel after the 5 s column wait. Do not raise the 5 s cap; it exists so focus is not pulled back after the viewer has moved on.
- Chat-sync rapid input is not required to be lossless: each press must produce one rendered step, so the journey waits for each step before the next press.
- Every journey owns a `Journey` session, which starts a Playwright trace. It writes the trace and a `<test>.failure.txt` (browser state plus the last 80 Mock app log lines) only when the test failed; a passing test discards the trace. xUnit v2 has no test context, so `[CaptureFailure]` records `FirstChanceException` for `XunitException`, `TimeoutException`, `InvalidOperationException` and `PlaywrightException` in an `AsyncLocal` marker.

## 7. Selectors

bUnit and Playwright tests find elements in this order (ADR 0012):

1. **Role and accessible name**: `GetByRole(AriaRole.Button, new() { Name = "Retry" })`, `[role='treeitem'][aria-level='1']`, `nav[aria-label=Library]`, `dialog`, `main`, or an ARIA relationship such as the element named by `aria-controls` or `aria-labelledby`.
2. **A `data-*` hook** where no role fits: `[data-recording-id]`, `[data-delete-confirm]`, `[data-status='rec']`, `[data-shell][data-theater-mode]`. State a test needs to observe is exposed as an ARIA attribute or a `data-*` attribute, never as a modifier class.
3. **Never a styling class.** Tests do not select or assert `sk-*` or `mud-*` classes, or the unprefixed modifiers that go with them (`.rail`, `.theater`, `.idle`, `.warning`), and do not assert class lists or keyframe names. Styling changes during the MudBlazor migration; behaviour tests must pass unchanged across it.

When a test needs an element that has neither a role nor a hook, add the missing role, accessible name or `data-*` attribute to the markup; do not fall back to a class. `TestSelectorGuardTests` fails the unit suite when a file in `tests/StreamKeepr.Tests` or `tests/StreamKeepr.E2E` contains an `sk-*` or `mud-*` class selector (`.sk-row`) or `class` attribute match (`class="sk-shell`, `[class*='sk-chip']`), uses a class-list API (`ClassList`, `classList`, `className`, `HasClass`, `ToHaveClass`), or reads the `class` attribute (`HasAttribute`/`GetAttribute`, `hasAttribute`/`getAttribute`, either quote style). It does not catch a bare `sk-*` token in a string, because cookie and storage names such as `sk-auth` look the same, and it does not catch unprefixed class selectors such as `.dot`, which look like other dotted text. Review catches those.

The Vitest suite under `tests/js` is outside the guard. Its fixtures reproduce the markup the production JavaScript modules query, including the classes those modules select, and its assertions check classes the module itself sets (`idle`, `at-edge`, `sk-spin`), which the shared `components.css` consumes; asserting them checks the module's output to that stylesheet, not a selector the test chose. Where a module also exposes a state as a hook (`data-idle`, `data-at-edge`, `data-spinner`, `data-crosshair`), Vitest asserts the hook as well, and the .NET tests use only the hook. A module that moves to a `data-*` hook moves its fixture with it.

## 8. CI

One workflow `.github/workflows/ci.yml` (replacing the current main-only build) with jobs:

| Job | Steps |
|---|---|
| `dotnet` | setup .NET 10; restore; `dotnet build -warnaserror`; `dotnet format --verify-no-changes`; `dotnet test` with coverage; upload results |
| `js` | setup Node 24 LTS; `npm ci`; `npm run lint`; `npm test` |
| `e2e` | setup .NET; restore and build only `tests/StreamKeepr.E2E` (and the app it references); `dotnet publish` the app once; `playwright install --with-deps`; `dotnet test tests/StreamKeepr.E2E` against that publish; upload the TRX always and the Playwright report, traces, axe output and screenshots only on failure |
| `docker` | `docker build`; run the image with dummy non-secret config and a temp `/recordings` volume; hit `/health/live` and `/`; verify non-root; stop cleanly |

- Triggers: pull requests and pushes to `main` and `dev`. The `fullstack-refactor` trigger was removed once that migration merged (#112).
- `dev` is the integration branch for active work. Pull requests into `dev` and pushes to `dev` run only `changes`, `dotnet` and `js`; `docker`, `e2e` and `compose` run for everything else, including every pull request into `main`.
- Required checks for merge; configure branch protection when collaborating.
- Add a minimal `.editorconfig` and a `.gitattributes` (`* text=auto eol=lf`) so `dotnet format --verify-no-changes` has a stable baseline on every platform. An existing Windows checkout that still has CRLF files needs one re-materialisation: `git rm --cached -r . && git reset --hard`.
- Cache NuGet, npm, and Playwright browsers; cancel superseded runs.
- Upload Playwright reports and coverage as artifacts.
- The Docker job does not need Twitch credentials; use dummy values and no channels.
- CI installs ffmpeg for the recorder/fake-Streamlink tests.

## 9. Budgets and quality gates

- **Coverage:** collect it (`coverlet` or `dotnet test --collect`); no percentage gate initially. Add a threshold once the pure core is covered; do not block early work on an arbitrary number.
- **Performance:** enforce the plan's first-paint JS budget (≤ 200 KB gzipped, including `blazor.web.js`) once the frontend exists. hls.js and the player module load only on recording pages. CI fails over budget.
- **Accessibility:** axe (`Deque.AxeCore.Playwright`) clean on every E2E screen; a keyboard-only journey reaches every control; reduced motion stops the REC pulse and collapse animations.
- **Visual regression:** dropped for now (2026-11-12, #106). There is no screenshot gate; layout and styling are covered by bUnit markup tests, axe, and the E2E journeys. The harness built for it is kept unmerged on branch `test/106-visual-baselines` if the gate comes back.
- **No automatic retries** for unit, integration, or E2E. A flaky test is fixed or quarantined with a linked issue and a deadline; quarantine is explicit and reported, never a silent skip.
- Do not delete, skip, or weaken tests to make a build pass.

## 10. Per-area matrix

| Area | Required tests |
|---|---|
| Domain | Every transition row in `docs/domain-model.md`; invariants; stale-job events; healthy/short parts; cooldown; stop/resume; recovery classification; property tests for totality/determinism |
| Recorder | Args and paths; parser `N/A`/`progress=end`; part cleanup; shutdown order/budget; normal end; either process first; stop; stall; no media; short final block; part-exists; ad grace; MPEG-TS/AAC; HEVC/AV1 args |
| Session store | Manifest round-trip; atomic writes; lock coverage; stitch/ENDLIST/chapters; live cache; recovery; corrupt manifests; missing index; library events; disk guard; no retention |
| Runtime | Command order; open failure skip; finalize failure; job overrun fatal; retry timer; disk full; channel add/remove/stop/resume; Helix validation; seeded failure; control gate; shutdown |
| Twitch/auth | Token cache/refresh/401; forced admin-token refresh; poller batch/Unknown/offline/profile; schedule/coverage; EventSub welcome/refusal/409 session verification/429 capacity/partial coverage/no-resolved-channel idle/non-live online/dedupe/reconnect handoff/malformed and route isolation/401 retry/timeouts/backoff; allowlist empty/configured; callback/HTTPS/cookie/logout; token encryption/rotation/clear/wait; subscriber token |
| Hosting | Media allowlist/traversal/content types/cache/Range; live vs final index; health push-aware/readiness; Data Protection persistence; preflight; endpoint auth; Serilog pipeline (Serilog factory, levels from the `Serilog` section, no duplicate providers, static logger untouched by test hosts); request logging (one completion event per request with `RequestId`, level policy for quiet paths/4xx/5xx, query string never logged, handled 500 logged as an error); shipped logging config and template, fatal startup log and non-zero exit; lifecycle breadcrumbs (one startup summary, one stopping line, no client secret or chat token) |
| Frontend | bUnit primitives and feature components; Vitest helpers/player logic; Playwright journeys; axe/keyboard |
| Emotes | Provider normalization and static URL assembly; cache TTL/stale/single-flight and bounds; channel-ID fallback and memoization; cold global with a slow resolver; endpoint shape/disabled/failure; matcher precedence and native-range protection; image attributes and text fallback; Mock/E2E fixture; no external network |
| Container | Build; non-root; health; deep links; volume; secrets; stop finalize; restart recovery |

## 11. Failure modes

| Scenario | Behaviour |
|---|---|
| ffmpeg missing locally | Recorder/fake-Streamlink tests fail with a clear setup message; CI installs it |
| Playwright browser missing | E2E job fails after `playwright install`; upload logs |
| Mock profile fails to start | E2E fails after `/health/ready` timeout with captured logs |
| Docker unavailable | Docker job fails; runner must provide Docker |
| Test writes outside temp | Test fails review; cleanup guard |
| Flaky test | No retry; fix or quarantine with an issue |
| Coverage below future threshold | Gate once introduced, not before |

## 12. Binding versus implementation freedom

Binding: layer responsibilities, project layout, `IStreamPoller`/`IEventSubClient` seams, fake harness variants, Mock profile behavior, determinism/isolation rules, the selector rule (§7) and its guard, CI jobs/gates, no-retry policy, coverage/performance/a11y/visual posture, and per-area minimums.

Freedom: test framework details within xUnit/bUnit/Playwright/Vitest, fixture builder style, assertions, CI caching, and artifact naming.

## 13. Related

- `docs/domain-model.md`, `docs/recorder-contract.md`, `docs/session-store-contract.md`, `docs/runtime-contract.md`, `docs/twitch-auth-contract.md`, `docs/hosting-contract.md`.
- `docs/reviews/implementation-plan-amendments.md` (B-03, B-04, B-06, B-07).
- Plan Testing and verification.
