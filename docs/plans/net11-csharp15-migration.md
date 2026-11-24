---
title: Upgrade to .NET 11 and C# 15
version: 1.0
date_created: 2026-11-23
last_updated: 2026-11-23
owner: @brandtkstrom
tags: [platform, build, upgrade, tooling]
---

Status: accepted (2026-11-23). Binding: yes for the target versions, the
package set, and the acceptance criteria; commit order and wording are advisory.
Branch: `chore/net11-csharp15`, based on `dev` at `68eae17`.

# Introduction

Stream Keepr targets .NET 10 and C# 14. This plan moves the solution to
.NET 11 and C# 15 on one reviewable branch, without changing behaviour and
without adopting new language features. .NET 11 shipped GA on 2026-11-10 and is
a Standard Term Support release (two years); .NET 10 remains supported as an LTS
release through 2028-11-14, so this upgrade is a deliberate move to the current
release rather than a forced one.

## 1. Purpose & Scope

**In scope:**

- Retarget all three projects from `net10.0` to `net11.0`.
- Take C# 15 as the language version by virtue of the target framework.
- Move framework-coupled NuGet packages to matching 11.0.x versions.
- Update the container image pins, the CI SDK version, and the one hardcoded
  target-framework path.
- Update the docs that state the current stack.
- Record what was and was not exercised for each .NET 11 breaking change.

**Out of scope:**

- Adopting C# 15 language features (unions, closed hierarchies, collection
  expression arguments, extension indexers).
- Upgrading packages that are not coupled to the ASP.NET Core version:
  MudBlazor stays pinned at `[9.12.0]`, OpenTelemetry at `1.21.0`, CliWrap,
  bunit, xunit, Playwright, and the test SDK stay as they are.
- Moving to xunit v3 or Microsoft.Testing.Platform.
- Adding `global.json`, `Directory.Build.props`, or package lock files.
- Changes to recording, playback, chat, export, or any user-visible behaviour.
- Runtime Async (`<Features>runtime-async=on</Features>`), which remains a
  preview feature in .NET 11.

## 2. Decisions

| # | Decision | Rationale |
| --- | --- | --- |
| D1 | Branch from `dev` at `68eae17`; PR targets `dev`. | `dev` is current with `main` (0 behind, 3 unique commits). No branch sync needed. |
| D2 | Plan document plus the migration on the same branch, committed incrementally. | The plan is reviewable before the diff. |
| D3 | Retarget only; no C# 15 feature adoption. | Language features are unrequested and would widen an already broad diff. |
| D4 | Bump only version-coupled packages (see §3.2). | Mixing 10.x test packages with an ASP.NET Core 11 runtime is the likeliest silent failure. MudBlazor's exact pin looks deliberate and stays. |
| D5 | Pin `sdk:11.0.100` and `aspnet:11.0.0` in the Dockerfile; CI uses `11.0.x`; still no `global.json`. | Mirrors the existing policy: precise container pins, floating CI SDK. A `global.json` is a separate build-policy decision. |
| D6 | Keep `<DisableBuildCompression>`, then attempt removal as a separate commit. | The workaround and the E2E precompressed-asset guard are a matched pair; the guard decides whether it is still needed. |
| D7 | Update current-state docs only; annotate research docs; leave plan snapshots alone. | Rewriting measurements taken on .NET 10 would falsify the record. |
| D8 | Verify with build, format, unit tests, and the Mock-profile E2E suite; not a manual feature sweep. | Reproducible and reviewable. Each breaking change is recorded with its exercise status. |

## 3. Change set

### 3.1 Target frameworks

| File | Change |
| --- | --- |
| `src/StreamKeepr/StreamKeepr.csproj:4` | `net10.0` → `net11.0` |
| `tests/StreamKeepr.Tests/StreamKeepr.Tests.csproj:4` | `net10.0` → `net11.0` |
| `tests/StreamKeepr.E2E/StreamKeepr.E2E.csproj:4` | `net10.0` → `net11.0` |

No `<LangVersion>` is added: C# 15 is the default language version for
`net11.0`. No project sets `LangVersion`, `NoWarn`, `WarningsAsErrors`, or an
analyzer level today, and no source file uses a `#if NET*` symbol, so nothing
else guards the version.

### 3.2 Packages

Version-coupled packages move to their 11.0.x release:

| File | Package | From | To |
| --- | --- | --- | --- |
| `src/StreamKeepr/StreamKeepr.csproj` | `Serilog.AspNetCore` | `10.0.0` | `11.0.0` |
| `src/StreamKeepr/StreamKeepr.csproj` | `AspNet.Security.OAuth.Twitch` | `10.1.0` | `11.0.0` |
| `tests/StreamKeepr.Tests/StreamKeepr.Tests.csproj` | `Microsoft.AspNetCore.Mvc.Testing` | `10.0.5` | `11.0.0` |
| `tests/StreamKeepr.Tests/StreamKeepr.Tests.csproj` | `Microsoft.Extensions.Diagnostics.Testing` | `10.11.0` | `11.0.1` |
| `tests/StreamKeepr.Tests/StreamKeepr.Tests.csproj` | `Microsoft.Extensions.TimeProvider.Testing` | `10.11.0` | `11.0.1` |

Unchanged: `MudBlazor [9.12.0]`, `CliWrap 3.10.5`, OpenTelemetry
`1.21.0` (four packages, latest), `bunit 2.15.2`, `coverlet.collector 6.0.4`,
`Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3`,
`xunit.runner.visualstudio 3.1.4`, `Microsoft.Playwright 1.64.1`,
`Deque.AxeCore.Playwright 4.14.0`.

`Microsoft.AspNetCore.Mvc.Testing` has no 11.0.1 on NuGet, so 11.0.0 is the
newest available; the two `Microsoft.Extensions.*.Testing` packages do have
11.0.1 and take it.

### 3.3 Container and CI

| File | Change |
| --- | --- |
| `Dockerfile:3,7` | Header comment and `FROM mcr.microsoft.com/dotnet/sdk:10.0.103` → `sdk:11.0.100` |
| `Dockerfile:18` | `mcr.microsoft.com/dotnet/aspnet:10.0.14` → `aspnet:11.0.0` |
| `.github/workflows/ci.yml:86,294` | `dotnet-version: "10.0.x"` → `"11.0.x"` |
| `.github/workflows/ci.yml:328` | `bin/Debug/net10.0/playwright.ps1` → `net11.0` |
| `README.md:118` | Same Playwright path |

`11.0.100` and `11.0.0` are the only 11.x patch tags currently published for
those images. The Dockerfile comment explaining that the SDK and runtime pins
move together still applies.

### 3.4 Docs

Update, because they describe the current stack:

- `docs/hosting-contract.md:140-147` — image pins and the pinned-version line.
- `docs/testing-contract.md:126` — ".NET 10" in the CI sequence.
- `docs/frontend-foundation-contract.md:14` — "Blazor Web App on .NET 10".
- `README.md` — Playwright path, plus any stack statement.

Annotate, because they are dated records:

- `docs/decision-digest.md` — §3 is a decision ledger and §9 a conformance
  review, so the .NET 10 entries stay as written. A dated platform note under
  the status line points at this plan instead.
- `docs/research/live-hls-playback-resilience.md`,
  `opentelemetry.md`, `mudx-chat-components.md`, `live-transcode-vaapi.md`, and
  `vaapi-streamlink-spike.md` — one line under the title, because their claims
  rest on .NET 10 framework behaviour or on the `aspnet:10.0.14` image.
  `irc-chat-messages.md` and `twitch-chat-replay.md` are not annotated: their
  .NET 10 mentions are third-party package target frameworks, not claims about
  this application.

Leave unchanged: `docs/plans/streamkeepr-implementation-plan.md`, whose version
snippets are a historical snapshot and are already stale relative to the
Dockerfile.

## 4. Verified baseline

Captured on `chore/net11-csharp15` at `68eae17`, before any change:

- SDK installed: `8.0.301`, `9.0.200`, `10.0.100`, `10.0.402`, `10.0.404`,
  `11.0.100`. `dotnet --version` → `11.0.100`; runtimes include
  `Microsoft.AspNetCore.App 11.0.0`.
- `dotnet build StreamKeepr.sln -warnaserror` → succeeded, 0 warnings,
  0 errors, using SDK 11 against `net10.0`.
- `dotnet test StreamKeepr.sln --no-build --filter "Category!=E2E"` → passed:
  1885 tests in `StreamKeepr.Tests` and 2 in `StreamKeepr.E2E`, 0 failed,
  0 skipped, 2 m 17 s.

Solution shape: three projects, all `net10.0`. 420 C# files / 57,302 lines and
53 `.razor` files / 5,735 lines, excluding `bin/` and `obj/`.

## 5. Breaking-change checklist

Each item is recorded here with how it was or was not exercised. "Build" means
the compiler or an existing test would have caught it; "reasoned" means the
code was inspected and the change cannot affect it.

| .NET 11 change | Relevance | Plan |
| --- | --- | --- |
| `IHost.RunAsync`/`StopAsync` throw when a `BackgroundService` fails | High: several hosted services, and the compose smoke asserts a clean stop | Exercise the Mock E2E and the documented shutdown path; reason over each hosted service. |
| Hosting emits OpenTelemetry HTTP semantic-convention tags by default | Medium: custom telemetry and an observability overlay | Compare emitted tag names in the E2E telemetry assertions; record any difference. |
| Blazor SSR defers antiforgery validation to middleware | Medium: forms plus hand-ordered middleware in `Program.cs:417-457` | Build plus E2E form flows; confirm the antiforgery middleware position is still correct. |
| Response compression always emits `Vary: Accept-Encoding` | Low-medium: static assets | Build; check the E2E asset guard. |
| `DeflateStream`/`GZipStream` write headers for empty payloads | Related to the static-asset workaround | Covered by the workaround removal experiment (D6). |
| Decimal/BigInteger floating-point conversions correctly rounded | Low: no `(decimal)` casts found in `src/` | Reasoned; recheck after the compiler swap. |
| `DateOnly`/`TimeOnly` `TryParse` throws for invalid input | Low: no `DateOnly`/`TimeOnly` parse calls found in `src/` | Reasoned. |
| `ZipArchive` CRC32 validation on read | Low: no `ZipArchive` usage found in `src/` | Reasoned. |
| `Uri` parsing and `UriBuilder` validation changes | Low-medium: Twitch and media URLs | Covered by unit tests over URL handling. |
| Minimum hardware requirements updated | Deployment note | Recorded in the PR; no code change. |
| Obsolete APIs and removed Blazor/MVC APIs | Unknown until compile | Build with `-warnaserror`. |
| New Blazor analyzers `BL0011`–`BL0016` | Confirmed: 8 build errors, all `BL0012`/`BL0016` | See §5.1. |
| `QuickGrid` controls render as links | None: no `QuickGrid` usage found | N/A. |

This list is the .NET 11 migration guide's own set; it is not exhaustive by the
guide's admission, so a green suite is evidence, not proof.

### 5.1 Blazor analyzers BL0011–BL0016

The ASP.NET Core 11 component analyzer adds `BL0011`–`BL0016`. They are warnings
by default, and this repository passes `-warnaserror`, so they surface as build
errors even though no project sets `WarningsAsErrors`. Eight fired:

| Site | Diagnostic | Resolution |
| --- | --- | --- |
| `ViewersPanel.razor:62` | `BL0012` redundant `StateHasChanged` | Removed. Bound only as an `EventCallback`; the framework renders after the callback. |
| `LibraryDrawer.razor:409` | `BL0012` | Removed. Synchronous handler that ends the method; same reasoning. |
| `LibraryDrawer.razor:355` | `BL0012` | Kept, with `#pragma warning disable BL0012` and a reason. The render must happen before the `FocusAsync` below, which queries the DOM. |
| `ChannelSettings.razor:325` | `BL0012` | Kept, with `#pragma warning disable BL0012` and a reason. The render must happen before the awaited recorder stop, or the row never shows its removing state. |
| `VideoPlayerTests.cs:335,362,373`, `UiPreferencesTests.cs:227` | `BL0016` unguarded JS interop | `<NoWarn>$(NoWarn);BL0016</NoWarn>` in `StreamKeepr.Tests.csproj`. The flagged calls are on bUnit doubles, where a disconnected circuit is not a failure mode. |

The diagnostic's documented reach is narrower than it looks: it analyses only
the synchronous prefix of a method, before the first `await`, and does not
consider other callers. A method bound as an `EventCallback` and also invoked
through `@ref`, `[JSInvokable]`, or `OnAfterRenderAsync` may still need its
explicit render. Each of the four sites above was checked for other callers
before changing it; none has one.

## 6. Commit sequence

1. `docs: add .NET 11 and C# 15 migration plan`
2. Retarget the three projects to `net11.0`, resolve the analyzer diagnostics the
   retarget introduces (§5.1), and bump the version-coupled packages. These land
   together: the diagnostics exist only because of the retarget, and a
   half-applied state does not build.
3. Update the container pins, the CI SDK version, the Playwright paths, and the
   current-state docs; annotate the dated records.
4. Optional, only after the suite is green: attempt removal of
   `<DisableBuildCompression>`.

Each commit must build. Commits 2 and 3 may need to land together if the
10.x test packages fail against the 11.0 shared framework; if so, that is
recorded in the commit message rather than squashed silently.

## 7. Verification

Required before the branch is called done:

- `dotnet build StreamKeepr.sln -warnaserror` — zero warnings.
- `dotnet format StreamKeepr.sln --verify-no-changes`.
- `dotnet test StreamKeepr.sln --filter "Category!=E2E"`.
- `dotnet build tests/StreamKeepr.E2E` and the Mock-profile E2E suite.
- Docker image build and the compose smoke test, or an explicit statement that
  they were left to CI if the local environment cannot run them.

Not required: a manual walkthrough of recording, playback, chat, and export
against the Mock profile.

### 7.1 Load-sensitive tests observed during verification

Full-suite runs on this machine are not perfectly deterministic. Three runs of
`dotnet test StreamKeepr.sln --filter "Category!=E2E"` produced one green run
(1885 passed) and two runs with a single failure each, in two different tests:

- `SessionStoreConcurrencyTests.Concurrent_finalize_append_and_summarize_do_not_corrupt_the_session`
  — `UnauthorizedAccessException` from `File.Move` inside
  `SessionStore.WriteAtomicAsync`.
- `RecordingStopTests.Stop_confirms_then_calls_the_channel_control` — a bUnit
  `WaitForAssertion` around MudBlazor's asynchronous dialog lifecycle.

Both pass in isolation: 15/15 and 6/6 respectively.

The first has a concrete, version-independent mechanism.
`SessionStoreTestContext.cs:37` passes `retryDelay ?? (static (_, _) => Task.CompletedTask)`,
so the five-attempt sharing-violation retry in `SessionStore.cs:461-476` runs
with no delay and completes in microseconds. Any handle held outside the process
— a scanner or the search indexer — defeats it. `IsSharingViolation` already
covers `UnauthorizedAccessException`, so the guard exists; the test simply gives
it no time to work.

The second is wall-clock sensitive by construction: it waits on a dialog that
MudBlazor opens and closes asynchronously.

Neither is attributed to the .NET 11 upgrade here (see the A/B result in §7.2),
and neither warrants a source change in this branch. They belong in their own
issue: the harness should supply a real delay, and the dialog assertions should
use a timeout that tolerates a loaded machine.

### 7.2 Cross-version baseline

The same suite was run against `dev` at `68eae17` (`net10.0`, no migration
changes) from a clone outside `%TEMP%`, twice:

| Build | Full-suite runs | Green | Failures observed |
| --- | --- | --- | --- |
| `dev` @ `68eae17`, `net10.0` | 2 | 1 | `MockAuthTests.Fake_login_rejects_external_return_urls` |
| `chore/net11-csharp15`, `net11.0` | 4 | 1 | `SessionStoreConcurrencyTests…` (twice), `RecordingStopTests.Stop_confirms…` |

`RecordingStopTests.Stop_confirms…` failed on both builds. A third baseline was
discarded: cloned under `%TEMP%`, it failed three runs out of three, but
`MockEnvironmentTests.Refuses_a_repository_path…` cannot pass there by design —
the mock refuses a repository path inside temp — so that location is not a
usable comparison and those results are excluded.

Conclusion: full-suite runs on this Windows machine are non-deterministic on
both framework versions, at comparable rates, in different tests each time, all
of which pass in isolation. No observed failure is attributable to the retarget.
CI runs this suite on Ubuntu and was green before the branch; the branch's own
CI run is the authoritative result.

Recommended follow-up, tracked separately rather than fixed here:
`SessionStoreTestContext` should inject a real retry delay, and the dialog and
HTTP assertions should tolerate a loaded machine.

## 8. Rollback

Revert the merge commit on `dev`. No data, schema, or configuration format
changes are involved, and the previous container image tag remains pullable, so
rollback needs no coordinated migration. The only irreversible step would be
deleting the .NET 10 image pins from published tags, which this plan does not do.

## 9. Risks

- **Local verification gap.** The E2E suite and the Docker smoke test are slower
  and may not run on this machine; if they are skipped, CI is the only evidence
  and that must be stated rather than implied.
- **Transitive analyzer drift.** A new SDK can surface new warnings even at the
  same `AnalysisLevel`, because warnings-as-errors is a CLI flag and not a
  project property. The build step in §7 is the check.
- **Exact MudBlazor pin.** MudBlazor `[9.12.0]` must still resolve against
  `net11.0` reference assemblies. If it does not, the bump becomes a separate
  decision, not a silent one.
- **Stale docs.** The research notes are dated and will remain .NET 10 records;
  the annotation added in §3.4 is what prevents them being read as current.
