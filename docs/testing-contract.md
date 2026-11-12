# Stream Keepr Testing Contract

Status: accepted (2026-11-10)
Source: Implementation plan Testing and verification; Part 2 F9; plan review B-03/B-04/B-06/B-07; runtime, recorder, session-store, Twitch, and hosting contracts.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract defines the test seams, projects, fake-Twitch harness, determinism rules, CI gates, and per-area coverage. It replaces the current vacuous `StreamCaptureService.Tests` project when the new suite lands.

## 2. Test layers

| Layer | Project | Scope |
|---|---|---|
| Unit | `tests/StreamKeepr.Tests` | Pure reducer, stitcher, parser, recovery, args, path/media rules, poll schedule, token cache, allowlist logic |
| Integration | `tests/StreamKeepr.Tests` | `WebApplicationFactory`: media, health, auth, progress, export, channel endpoints; real DI with fake external services |
| Component | `tests/StreamKeepr.Tests` | bUnit: primitives, library, player shell, stats, details, auth, settings |
| JS | `tests/js` | Vitest: timecode, preferences, tree navigation, shortcuts, sparkline, player pure logic |
| E2E | `tests/StreamKeepr.E2E` | Playwright .NET against the Mock profile: journeys, axe, keyboard, screenshots |
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
- `tests/StreamKeepr.E2E` starts the app with the `Mock` launch profile on a free loopback port, waits for `/health/ready`, then runs Playwright.
- The E2E fixture may publish the Debug output and run `StreamKeepr.dll` with `ASPNETCORE_ENVIRONMENT=Mock` when the SDK development static-asset handler cannot serve the framework `_framework/*.js` assets. It keeps the source project as the content root for the Mock fixture path and points `ASPNETCORE_WEBROOT` at the publish output.
- `tests/js` is dev-only; Node never enters the image.
- The old `StreamCaptureService.Tests` project is deleted with the old app in the migration route.

## 4. Seams

- Add `IStreamPoller` and `IEventSubClient` interfaces; the production classes implement them, and the Mock profile registers fakes. The supervisor depends on the interfaces.
- Inject `TimeProvider` everywhere. Inject retry jitter/delay (`IRetryDelay` or `Func<TimeSpan, TimeSpan>`) so tests can make it deterministic; production uses random jitter.
- Use `IHttpClientFactory` with stub `HttpMessageHandler`s for Helix, token refresh, and EventSub subscription requests. No test calls Twitch.
- Use an in-memory `IAdminTokenStore` for auth/EventSub tests; the encrypted implementation is tested separately with a temp directory.
- Use a temp `StorageOptions.RecordingsRoot` and `WebApplicationFactory` settings per test class. Never write under the repo.
- Fake `IChannelControl`/`IRecordingActions` only at the UI seam; do not fake the supervisor when testing it.

## 5. Fake Twitch harness

### Fake Streamlink

Commit executable scripts under `tests/fixtures/`:

- `fake-streamlink` — generates a short test stream to stdout with ffmpeg (for example 5–10 s of `testsrc2` + sine, MPEG-TS). The plan's 10-minute script is for manual/soak runs; automated variants use short media.
- `fake-streamlink-no-media` — exits without writing media.
- `fake-streamlink-stall` — writes one segment and then sleeps; the stall watchdog must fire.
- `fake-streamlink-adbreak` — writes the Streamlink ad-break log line and then continues.
- `fake-streamlink-broken` — answers `--version` but exits 1 otherwise, so preflight passes and recording fails.
- `fake-streamlink-slow` — writes a partial source to exercise backpressure.
- `fake-streamlink-token-echo` — echoes its arguments to stderr so the token-redaction test can prove the subscriber header reached Streamlink without leaking it.

Rules:

- Scripts are POSIX `sh`, executable, and use only `ffmpeg` and standard shell tools.
- CI installs ffmpeg; tests fail with a clear message if it is missing.
- No network access; no Twitch credentials.
- The recorder integration tests set `Recorder:StreamlinkPath` to the script and `Recorder:FfmpegPath` to the real ffmpeg; the recordings root is a temp directory.
- `-bsf:a aac_adtstoasc` is exercised by an MPEG-TS/AAC fixture.

### Mock launch profile

`Mock` registers:

- `MockSeeder`: four streamers, eight recordings (finished VOD, two-part partial, growing EVENT capture), thumbnails, chapters. `Mock:Seed:ScaleRecordings` (default 0) adds finished archive recordings without media segments for the library scale measurement (#110).
- `MockPoller` / `MockEventSub`: deterministic observations and push notifications driven by `FakeTimeProvider` or explicit test controls.
- Fake signed-in and signed-out auth states; no OAuth.
- `fake-streamlink` for the growing capture.
- No Twitch client credentials are required.

`dotnet run --launch-profile Mock` is the local path. The E2E fixture may run the published Debug output with the Mock environment as described in §3 when the SDK static-asset handler cannot serve the framework assets. Plain `dotnet run` is the real recorder.

## 6. Determinism and isolation

- Use `Microsoft.Extensions.TimeProvider.Testing` `FakeTimeProvider`; no real `Task.Delay`, clocks, or timers in unit tests.
- Fixed `T0`, deterministic stream IDs, deterministic session IDs.
- Inject retry jitter; production randomness never enters assertions.
- Unique temp directory per test; fixture cleans it up. Tests never write to the repo, user profile, or `/recordings`.
- No network; stub HTTP handlers; fake processes.
- No shared static mutable state. Use xUnit collections only when a real shared resource is unavoidable.
- Dispose services, `WebApplicationFactory`, child processes, and temp directories in `IAsyncLifetime`/`IDisposable`.
- Tests must pass in any order and in parallel unless explicitly isolated.

## 7. CI

One workflow `.github/workflows/ci.yml` (replacing the current main-only build) with jobs:

| Job | Steps |
|---|---|
| `dotnet` | setup .NET 10; restore; `dotnet build -warnaserror`; `dotnet format --verify-no-changes`; `dotnet test` with coverage; upload results |
| `js` | setup Node 24 LTS; `npm ci`; `npm run lint`; `npm test` |
| `e2e` | setup .NET and Node; build; `playwright install --with-deps`; start the Mock profile; `dotnet test tests/StreamKeepr.E2E`; upload Playwright report, traces, screenshots |
| `docker` | `docker build`; run the image with dummy non-secret config and a temp `/recordings` volume; hit `/health/live` and `/`; verify non-root; stop cleanly |

- Triggers: pull requests and pushes to `main`. The `fullstack-refactor` trigger was removed once that migration merged (#112).
- Required checks for merge; configure branch protection when collaborating.
- Add a minimal `.editorconfig` so `dotnet format --verify-no-changes` has a stable baseline.
- Cache NuGet, npm, and Playwright browsers; cancel superseded runs.
- Upload Playwright reports and coverage as artifacts.
- The Docker job does not need Twitch credentials; use dummy values and no channels.
- CI installs ffmpeg for the recorder/fake-Streamlink tests.

## 8. Budgets and quality gates

- **Coverage:** collect it (`coverlet` or `dotnet test --collect`); no percentage gate initially. Add a threshold once the pure core is covered; do not block early work on an arbitrary number.
- **Performance:** enforce the plan's first-paint JS budget (≤ 200 KB gzipped, including `blazor.web.js`) once the frontend exists. hls.js and the player module load only on recording pages. CI fails over budget.
- **Accessibility:** axe (`Deque.AxeCore.Playwright`) clean on every E2E screen; a keyboard-only journey reaches every control; reduced motion stops the REC pulse and collapse animations.
- **Visual regression:** dropped for now (2026-11-12, #106). There is no screenshot gate; layout and styling are covered by bUnit markup tests, axe, and the E2E journeys. The harness built for it is kept unmerged on branch `test/106-visual-baselines` if the gate comes back.
- **No automatic retries** for unit, integration, or E2E. A flaky test is fixed or quarantined with a linked issue and a deadline; quarantine is explicit and reported, never a silent skip.
- Do not delete, skip, or weaken tests to make a build pass.

## 9. Per-area matrix

| Area | Required tests |
|---|---|
| Domain | Every transition row in `docs/domain-model.md`; invariants; stale-job events; healthy/short parts; cooldown; stop/resume; recovery classification; property tests for totality/determinism |
| Recorder | Args and paths; parser `N/A`/`progress=end`; part cleanup; shutdown order/budget; normal end; either process first; stop; stall; no media; short final block; part-exists; ad grace; MPEG-TS/AAC; HEVC/AV1 args |
| Session store | Manifest round-trip; atomic writes; lock coverage; stitch/ENDLIST/chapters; live cache; recovery; corrupt manifests; missing index; library events; disk guard; no retention |
| Runtime | Command order; open failure skip; finalize failure; job overrun fatal; retry timer; disk full; channel add/remove/stop/resume; Helix validation; seeded failure; control gate; shutdown |
| Twitch/auth | Token cache/refresh/401; poller batch/Unknown/offline/profile; schedule/coverage; EventSub welcome/refusal/reconnect/backoff; allowlist empty/configured; callback/HTTPS/cookie/logout; token encryption/rotation/clear/wait; subscriber token |
| Hosting | Media allowlist/traversal/content types/cache/Range; live vs final index; health push-aware/readiness; Data Protection persistence; preflight; endpoint auth |
| Frontend | bUnit primitives and feature components; Vitest helpers/player logic; Playwright journeys; axe/keyboard |
| Container | Build; non-root; health; deep links; volume; secrets; stop finalize; restart recovery |

## 10. Failure modes

| Scenario | Behaviour |
|---|---|
| ffmpeg missing locally | Recorder/fake-Streamlink tests fail with a clear setup message; CI installs it |
| Playwright browser missing | E2E job fails after `playwright install`; upload logs |
| Mock profile fails to start | E2E fails after `/health/ready` timeout with captured logs |
| Docker unavailable | Docker job fails; runner must provide Docker |
| Test writes outside temp | Test fails review; cleanup guard |
| Flaky test | No retry; fix or quarantine with an issue |
| Coverage below future threshold | Gate once introduced, not before |

## 11. Binding versus implementation freedom

Binding: layer responsibilities, project layout, `IStreamPoller`/`IEventSubClient` seams, fake harness variants, Mock profile behavior, determinism/isolation rules, CI jobs/gates, no-retry policy, coverage/performance/a11y/visual posture, and per-area minimums.

Freedom: test framework details within xUnit/bUnit/Playwright/Vitest, fixture builder style, assertions, CI caching, and artifact naming.

## 12. Related

- `docs/domain-model.md`, `docs/recorder-contract.md`, `docs/session-store-contract.md`, `docs/runtime-contract.md`, `docs/twitch-auth-contract.md`, `docs/hosting-contract.md`.
- `docs/reviews/implementation-plan-amendments.md` (B-03, B-04, B-06, B-07).
- Plan Testing and verification.
