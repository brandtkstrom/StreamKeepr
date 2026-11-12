# Post-implementation conformance review — 2026-11-11

Review of the built app on `main` at `a10c225` (after #107) against `docs/decision-digest.md` §9 and the binding contracts. Parent spec: #19.

**Outcome:** 7 of 9 gates pass. G1 and G6 are held by three Major gaps. Each gap is one issue. Per digest §9.4, a Major gap blocks release until it is fixed or the owner accepts the risk in writing here with a dated follow-up.

**Status on 2026-11-12:** all 9 gates pass. Every Major gap is fixed or accepted by the owner (CR-01 and CR-02 accepted as dropped). See [Status update](#status-update-2026-11-12). The sections below are the review as written on 2026-11-11.

## Method

As digest §9.2 specifies:

- **Source check:** each criterion is checked against the contract clause and the test or code that enforces it.
- **Scenario walkthrough:** each of the 13 scenarios is matched to its tests.
- **Graph check:** the project layout, plan, and migration state are compared.
- **Security check:** media, auth, and secrets.

Nothing was fixed during the review (§9.5). The latest CI run on `main` passes, and the local suites pass (869 .NET, 132 Vitest on Node 22, 31 E2E).

## Gates

| Gate | Result | Evidence |
|---|---|---|
| **G1 Build/test** | **Held** | All five CI jobs pass on `a10c225` and are required to merge (ruleset on `main`). No `Skip` or retry in any test or config. Held by **CR-03** (E2E failed on `93a15e2` and passed on the next commit), CR-05, and CR-08. |
| G2 Media security | Pass | `RecordingsMediaTests` covers allowlist, dot segments (including `.streamkeepr/exports/…` and `/keys/…`), traversal, encoded traversal, no directory listing, and Range. `LiveIndexEndpointTests` covers live and final index, 404 for unknown, invalid, or corrupt. |
| G3 Auth | Pass | `TwitchSignInTests` covers callback, no scopes, allowlist, and a token absent from logs. `AuthRoutingTests` covers loopback-only HTTP, local return URLs, logout antiforgery, and forwarded headers. `AdminTokenStoreTests` covers encryption. The open-admin warning is logged from `Program.cs:199` and tested in `WebAppSmokeTests`. |
| G4 Recording integrity | Pass | `FinalizeTimeoutSeconds` is `[Range(5, 15)]`. Recovery, restart continuity, shutdown parallelism, finalize failure, and session-store concurrency are all tested. |
| G5 Data actions | Pass | Export: one MP4, incompatible parts, cancel/retry, 24 h cleanup, `hvc1`. Lease vs. delete, progress/resume, and download auth and Range all have tests (#66–#68, #102, #107). |
| **G6 UI conformance** | **Held** | Behaviour, axe, keyboard, reduced motion, and the JS budget (~64 KB of 200 KB) pass after #107. Held by **CR-01** (no visual baselines) and **CR-02** (no Safari manual pass). CR-04 is open. |
| G7 Container/ops | Pass | Dockerfile: `USER`, `HEALTHCHECK`, ffprobe. `compose.yaml`: `init`, `stop_grace_period: 50s`, secrets, volume, log rotation. `compose-smoke.sh` checks deep links and Data Protection keys (mode 700) across a restart. |
| G8 Licensing | Pass | The only font is `material-symbols-rounded.woff2`, with its Apache-2.0 licence. The hls.js licence is present. No Google Fonts or CDN references in `src/`. |
| G9 Documentation | Pass with gaps | Contracts, ADRs, and code agree on the checked clauses. One web project; the old worker is deleted (ADR-0001). CR-06 and CR-07 are stale docs/config. |

## Scenario walkthrough

| Scenario | Covered by |
|---|---|
| Crash during a part | Recovery classification and resume tests (`An_unfinished_recovered_session_resumes_at_the_next_part…`), `RestartContinuityTests` |
| Offline blip | `An_offline_blip_then_the_same_stream_reopens_the_finalized_session` and reducer offline-count tests |
| Ad break | `An_ad_break_grants_grace_and_does_not_trip_the_stall_watchdog` |
| Disk full | `A_full_disk_skips_the_recorder_and_schedules_a_retry`, `DiskGuardTests` |
| Delete during export | `Delete_refuses_while_an_export_lease_is_held`, `An_active_export_disables_delete_with_a_reason` |
| Restart during recording | `RestartContinuityTests`, `StartupRecoveryHostTests`, compose restart smoke |
| EventSub outage | `EventSubClientTests` and poll-schedule push-coverage tests |
| Token expiry | `TwitchTokenProviderTests` (cache, refresh, 401), `TwitchAdminSessionValidatorTests` |
| HTTPS / open-admin | `Login_over_plain_http_outside_loopback_redirects_with_https_required`, open-admin warning test |
| Player live→VOD | `A_growing_capture_opens_near_the_live_edge_follows_and_hands_off_on_endlist` |
| Resume | `A_paused_position_resumes_after_reload_and_shows_a_resume_bar`, `ProgressStoreTests` |
| 2,000-item library | **Not measured — CR-04** |
| Incompatible export | `Incompatible_parts_fail_with_the_stable_code_and_produce_no_file` and the bUnit row test |

## Findings

| ID | Severity | Gate | Clause | Finding | Disposition | Issue |
|---|---|---|---|---|---|---|
| CR-01 | Major | G6 | Testing contract §8; plan F9-T4; B-06 | No visual baselines at 1280×820 and 375×812 | Deferred by the owner during #71; needs written acceptance with a date | #106 |
| CR-02 | Major | G6 | Player contract: "the iPhone Safari pass is manual" | The manual iPhone Safari checklist does not exist and no pass is recorded | Write the checklist and run it once on a device | #108 |
| CR-03 | Major | G1 | Testing contract §8: a flaky test "is fixed or quarantined with a linked issue and a deadline" | E2E failed on `main` at `93a15e2`: the player never became ready in 3 tests. It passed on the next commit. | Reproduce and fix; quarantine only explicitly | #109 |
| CR-04 | Minor | G6 | E-19; library contract (revisit at ~2,000 items) | The library has never been measured near 2,000 items | Add a measurement and record it | #110 |
| CR-05 | Minor | G1 | — | CI pins Node 20 (end of life). Node 26 breaks 4 `player-prefs-keys` tests through its built-in `localStorage`. | Move CI to an active LTS and harden the test | #111 |
| CR-06 | Minor | G9 | Testing contract §7: "Narrow to `main` after merge" | CI still triggers on the merged `fullstack-refactor` branch | Remove the trigger | #112 |
| CR-07 | Minor | G9 | Digest §9.3: docs and code agree | The plan's task table shows all 63 tasks as "To do" | Mark them done, or point to the issues | #113 |
| CR-08 | Minor | G1 | — | Parallel E2E fixtures can collide in `dotnet publish` | Publish once per run | #114 |
| CR-09 | Observation | G9 | — | #19 still says the binding contracts live on `fullstack-refactor`; they are on `main` | None | — |

## Owner acceptance

Release is blocked by CR-01, CR-02, and CR-03 until each is fixed or accepted below with a dated follow-up.

| ID | Accepted by | Date | Follow-up due |
|---|---|---|---|
| CR-01 | Repository owner: visual regression dropped for now; #106 closed as not planned | 2026-11-12 | None. Reopen #106 to bring it back; the harness is kept on branch `test/106-visual-baselines`. |
| CR-02 | Repository owner: the iPhone Safari pass is dropped (no iOS device to test with); #108 closed as not planned | 2026-11-12 | None. Native HLS, fullscreen, and PiP stay unit-tested only; the checklist in `docs/manual/` is kept. |
| CR-03 | Not needed: fixed (see below) | | |

## Status update (2026-11-12)

| ID | Status | Resolved by |
|---|---|---|
| CR-01 | Accepted (dropped) | #106 closed as not planned; PR #121 updates the testing contract, plan F9-T4, and digest |
| CR-02 | Accepted (dropped) | PR #118 added the checklist; the device pass was dropped and #108 closed as not planned; the player contract and digest say so |
| CR-03 | Fixed | PR #116: one shared E2E publish and a guard against empty compressed assets (the root cause was a torn brotli `hls.js`); `main` then passed 5 runs in a row and #109 was closed |
| CR-04 | Fixed | PRs #117 and #119: 2,019 flattened items measured within budget; E-19 stands |
| CR-05 | Fixed | PR #118: CI on Node 24; the prefs tests pass on Node 22–26 |
| CR-06 | Fixed | PR #118: CI pushes trigger on `main` only |
| CR-07 | Fixed | PR #116: plan task statuses point at their issues |
| CR-08 | Fixed | PR #116: the E2E app is published once per run |
| CR-09 | Fixed | #19's links now point at `main` |

| Gate | Now |
|---|---|
| G1 Build/test | Pass (CR-03, CR-05, CR-08 fixed) |
| G6 UI conformance | Pass (CR-01 and CR-02 accepted, CR-04 fixed) |
| G9 Documentation | Pass (CR-06, CR-07, CR-09 fixed) |

Nothing blocks release: no Major gap is open.
