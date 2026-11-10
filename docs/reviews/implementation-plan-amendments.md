# Implementation Plan Amendments

Status: accepted (2026-11-10)
Owner: @brandtkstrom
Date: 2026-11-10
Source: `StreamKeepr_Implementation_Plan_Final.md` (Part 1 v3, Part 2, Nov 9–10 2026)
Related: `docs/adr/0001-one-web-project-strangler-migration.md`

## Purpose

This register reviews the implementation plan for defects, ambiguities, and invalid dependency edges, and records one disposition for each finding. It is the input to `to-spec`; it does not rewrite the plan document. Behavior, interfaces, data contracts, and acceptance criteria bind the spec. The plan's code snippets are reference, not binding.

## Disposition legend

- **Fix** — a binding correction. The spec must state the corrected behavior/contract.
- **Accept** — an intentional product choice. The spec keeps the plan's behavior and records why.
- **Defer** — an unresolved product question. It belongs in the map's Not-yet-specified section or a named ticket.
- **Delegate** — a frontend contract detail. The named feature ticket resolves it; this register records the gap.

Every fix names an owner ticket. A finding is not resolved until its owner ticket is closed or the spec records the amendment.

## A. Correctness, security, and data integrity

| ID | Finding | Evidence | Disposition | Binding amendment / owner |
|---|---|---|---|---|
| A-01 | A recorder that produces one segment and then dies can retry forever; `Streak` never reaches `FailuresBeforeCooldown`. | Plan lines 297–299 reset `Attempt`/`Streak` on `PartProducingMedia`; lines 388–403 set `attempt = 1` when `HasMedia` and only increment `Streak` after retries are exhausted. | Fix | Define a **healthy-part** threshold (minimum segments or duration). Count consecutive short-lived parts toward `Streak`; reset `Streak` only on a healthy part or a normal offline finalize. Guarantee a bounded retry path into `CoolingDown` or `Paused`. Implemented by `docs/domain-model.md`: `RecorderExitKind`, `PartProducingMedia(At)`, `Recording.FirstMediaAt`, and `HealthyPartSeconds`. **Owner: Domain model, events, and reducer.** |
| A-02 | `/media` serves `session.json` and `.streamkeepr` despite the “private” comment. `ServeUnknownFileTypes = false` does not exclude known MIME types, and dot-folders are not filtered. | Plan lines 2463–2480; the F10 note at 3170 assumes dot-folder skipping that the code never configures. | Fix | Allowlist only `.m3u8`, `.m4s`, `.mp4`, `.jpg`, `.png`; reject any path segment starting with `.`; return 404 for `session.json`, `.streamkeepr`, and temp files. Add integration tests for both. Implemented by `docs/hosting-contract.md`. **Owner: Hosting, media security, health, and container.** |
| A-03 | The claim “two writers never touch one folder” is false. `OpenAsync`, `AppendMetadataAsync`, and `StitchLiveAsync` do not take the per-session lock. | Plan lines 1407, 1571–1586, 1610–1624, 1668–1678; only `ReopenAsync` (1589) and `FinalizeAsync` (1626) lock. | Fix | Every mutating session path takes `LockFor`: open, reopen, append metadata, finalize, delete, retention, export lease. Define whether live stitching takes the lock or reads a snapshot. Add a concurrent finalize/delete/stitch test. **Owner: Session store, stitching, recovery, and retention.** |
| A-04 | `AdminTokenStore` is registered but never defined; `IAdminTokenStore` has no save/clear methods; Part 1 does not build alone because `Program.cs` uses Part 2 components. | Plan lines 829–835 (interface), 2541 (registration), 2564–2575 (`AddRazorComponents`/`App`); F10-T7 at 3180. | Fix | Define `IAdminTokenStore` with `GetAsync`, `SaveAsync`, `ClearAsync`, and `WaitForTokenAsync`. Provide a minimal implementation in the backend foundation; EventSub runs polling-only until a token exists. Include a minimal Blazor shell in the backend foundation; the full UI remains Part 2. **Owner: Twitch polling, EventSub, and auth; Hosting; migration route.** |
| A-05 | EventSub silently subscribes to nothing until `ChannelEntry.UserId` is populated by F10-T1. | Plan lines 926–927 filter `UserId is not null`; F10-T1 at 3174 populates it from Helix `/users`. | Fix | Resolve `UserId` when a channel is seeded or added, in `ChannelStore`, not in a later frontend task. EventSub subscribes to every watched channel with a resolved id; unresolved channels log and stay on polling. Implemented by `docs/twitch-auth-contract.md` (`GetUserAsync` at add/seed). **Owner: Twitch polling, EventSub, and auth.** |
| A-06 | `/health/live` can stay healthy for 15 minutes with a stuck poller. | Plan lines 2344–2346: `3 * max(PollIntervalSeconds = 30, PollIntervalWithEventSubSeconds = 300)` = 900 s. | Fix | Liveness is **push-aware**: 90 s when EventSub coverage is incomplete; 900 s when every watched channel has an active subscription. Readiness may use the larger interval. Add tests for both budgets. Implemented by `docs/hosting-contract.md` and `docs/twitch-auth-contract.md`. **Owner: Hosting, media security, health, and container.** |
| A-07 | The allowed `FinalizeTimeoutSeconds` maximum (60 s) plus the ~20 s stop path exceeds the 40 s host and 50 s Compose budgets. | Plan lines 481 (range 5–60), 2524–2526 (40 s host); research note `docs/research/recorder-process.md`. | Fix | Cap `FinalizeTimeoutSeconds` at 15 s (default 10 s) and keep host 40 s / Compose 50 s. Add a worst-case test. **Owner: Recorder part lifecycle and finalize contract; Hosting.** |
| A-08 | Retention is described but unspecified: no `RetentionService` class, project entry, registration, or task. | Plan lines 490–491 (options), 1894 (optional service), 2848 (no defaults); project structure 86–126 and `Program.cs` 2500–2581 omit it. | Accept (non-goal) | **Retention automation is a non-goal.** The spec omits `RetentionDays`/`MaxTotalGb` and any hourly-service requirement; the plan document remains historical input. Recordings are kept until manually deleted. `ADR-0004` records the trade-off. **Owner: Plan review; Session store.** |
| A-09 | Library summaries mix the first title with the last game, so a title change updates the game but not the displayed title. | Plan lines 1696–1698 (`Metadata[0].Title`, `Metadata[^1].Game`). | Fix | Use the latest metadata for both title and game in `RecordingSummary`; chapters keep the history. Add a title-change test. **Owner: Session store, stitching, recovery, and retention.** |
| A-10 | Admin tokens are encrypted in wave 3, but Data Protection key persistence is only in F9-T6 (wave 5), so tokens can become unreadable after a restart. | Plan lines 3180 (F10-T7) and 3165 (F9-T6). | Fix | Persist the Data Protection key ring in the backend foundation before token storage. F10-T7 depends on that foundation; F9-T6 verifies it in the container. Implemented by `docs/twitch-auth-contract.md` (keys under `.streamkeepr/keys`). **Owner: Hosting; Twitch polling, EventSub, and auth.** |
| A-11 | The recorder omits `-bsf:a aac_adtstoasc`, which can fail on MPEG-TS/AAC input; it is harmless for fMP4. | Research note `docs/research/recorder-process.md`; plan args at 1125–1145 omit it. | Fix | Add the bitstream filter when the input is MPEG-TS/AAC; keep it harmless for fMP4. Add an FFmpeg argument test. **Owner: Recorder part lifecycle and finalize contract.** |

## B. Buildability and dependency graph

| ID | Finding | Evidence | Disposition | Binding amendment / owner |
|---|---|---|---|---|
| B-01 | Part 1 is not independently buildable; it depends on Part 2 Razor components. | Plan lines 2504, 2564–2575; Phase 7 at 2604–2612. | Fix | The backend foundation includes a minimal shell so Part 1 builds and runs; the full UI remains Part 2. This is the migration route's first checkpoint. **Owner: Target architecture and migration route; Hosting.** |
| B-02 | F6-T1 and F10-T7 form a dependency cycle. | F6-T1 (3126) Done criteria requires F10-T7; F10-T7 (3180) depends on F6-T1. | Fix | F10-T7 has no dependency on F6-T1; F6-T1 depends on F10-T7. Swap the waves: F10-T7 wave 2, F6-T1 wave 3. **Owner: Plan graph; Backend surface for the UI.** |
| B-03 | F9-T5 is in wave 2 but cannot meet its Done criteria until e2e, Playwright, and Docker exist. | F9-T5 (3164) depends on F0-T1; Done criteria include F9-T2 (wave 9), F9-T6 (wave 5), and JS tests. | Fix | Split into **F9-T5a CI skeleton** (restore, build warnings-as-errors, format, unit tests; wave 2) and **F9-T5b full gates** (JS lint/Vitest, e2e with Playwright report, Docker build; depends on F9-T5a, F9-T1, F9-T2, F9-T6, F3-T1; wave 10). Implemented by `docs/testing-contract.md`. **Owner: Testing strategy; Frontend quality.** |
| B-04 | Several task dependencies omit services their Done criteria require. | F0-T3 (3048) needs F10-T2/F10-T5; F1-T2 (3062) reads `ILibraryView` but omits F0-T3; F2-T3/T4 (3075–3076) need F10-T2; F9-T1 (3160) needs F10-T2/F10-T3/F6-T1; F9-T2 (3161) needs F7-T1/T2/T3 and F5-T4. | Fix | Add the missing edges (see “Corrected dependency edges”). Recompute waves from the corrected graph. **Owner: Plan graph.** |
| B-05 | F10-T1 puts a backend task on the frontend critical path: summary fields and channel profiles are needed before the library can work. | F0-T3 (3048) depends on F10-T1; F10-T1 (3174) adds `PartCount`/`SegmentCount`/`EndedAt` and Helix profiles/UserIds. | Fix | Move `PartCount`, `SegmentCount`, `EndedAt`, and channel profile/UserId resolution into Part 1 (`RecordingSummary`, `ChannelStore`). F10-T1 becomes UI wiring or disappears. F0-T3 depends on the backend foundation, not a Part 2 task. Recompute the critical path. Implemented by `docs/backend-ui-contract.md`. **Owner: Plan graph; Backend surface for the UI; Session store.** |
| B-06 | F9-T4 baselines are “approved against design-system previews” that do not exist. | F9-T4 (3163); F11 (3184) says the design system has no Settings preview; design assets are absent (map Notes). | Delegate | Visual baselines are blocked until the design-system previews exist. `F9-T4` depends on **Preserve planning inputs and acquire design assets** and **Frontend foundation and app shell**; Settings baselines also depend on **Details, export, delete, auth UX, and settings**. Implemented/blocked as recorded in `docs/testing-contract.md`. **Owner: Frontend quality; design acquisition.** |
| B-07 | F9-T5’s Docker gate precedes the container task. | F9-T5 (3164) includes `docker build`; F9-T6 (3165) is the container/deep-link task in wave 5. | Fix | Covered by the F9-T5a/T5b split: the Docker gate belongs to F9-T5b after F9-T6. **Owner: Testing strategy; Frontend quality.** |

## C. Contract gaps

| ID | Finding | Evidence | Disposition | Binding amendment / owner |
|---|---|---|---|---|
| C-01 | Export artifact shape is ambiguous: F10-T3 may produce one MP4 or a ZIP of per-part MP4s; F5-T2 and the endpoint table assume one MP4. No MIME type, filename, progress event, queue, lease, or cleanup contract is defined. | F10-T3 (3176); F5-T2 (3116); HTTP endpoint table (3001–3005). | Fix | **Always produce one MP4.** If `ffprobe` finds parts with incompatible video parameters, the export fails with a clear `ExportIncompatibleParts` error; there is no ZIP fallback. Define the filename/content type, progress event, queue, lease registry, failure/cancel semantics, and the 24-hour cleanup worker. Implemented by `docs/backend-ui-contract.md`. **Owner: Backend surface for the UI; Details, export, delete, auth UX, and settings.** |
| C-02 | Export and delete are singletons but must check scoped `AuthenticationState`; no scoped wrapper exists. | F6-T4 (3129) says `ExportService`/`RecordingAdmin` check authorization; `AuthenticationState` is per circuit; only `ChannelsView` gets a scoped wrapper (F10-T6, 3179). | Fix | Add a scoped `RecordingActions`/`ExportActions` wrapper that captures the signed-in identity and calls the singleton with an actor identity. Enforce authorization server-side, not only in the UI. Implemented by `docs/backend-ui-contract.md` (`ClaimsPrincipal` parameters). **Owner: Backend surface for the UI; Details, export, delete, auth UX, and settings.** |
| C-03 | The auth contract is incomplete: `IAdminTokenStore` lacks save/clear; `Auth:AllowedTwitchLogins` has no options/schema; no OAuth callback route; “no extra scopes” is not reconciled with EventSub’s user token; subscriber-only token handling is open. | Plan lines 831–835, 3122–3131, 3217–3221. | Fix | Add `SaveAsync`/`ClearAsync`; define an `AuthOptions` class with `AllowedTwitchLogins`; define the callback route, return-url validation, cookie scheme, logout, and scopes; record the EventSub scope requirement; decide the subscriber-token path. Implemented by `docs/twitch-auth-contract.md`. **Owner: Twitch polling, EventSub, and auth.** |
| C-04 | `ILibraryView` has no channel methods; `ChannelsView` is named but undefined; `F11-T1` needs EventSub state that no service exposes. | F10-T6 (3179); `ILibraryView` (2975–2985); `F11-T1` (3188). | Fix | Define `ChannelView` and `ChannelsView` methods (list, add, remove, stop, resume, validation), and expose push-connected/reason from `PollSchedule` through the view. Implemented by `docs/backend-ui-contract.md`. **Owner: Backend surface for the UI.** |
| C-05 | `StreamerView.LastRecordedAt` is non-nullable, so a watched channel with zero recordings has no value. | Plan lines 2966–2971. | Fix | Make `LastRecordedAt` nullable or define a sentinel; render zero-recording streamers in Settings. Implemented by `docs/backend-ui-contract.md` (`StreamerView.LastRecordedAt?`, `ChannelsView`). **Owner: Library, search, and live updates.** |
| C-06 | The export/delete lease is described but no interface or registry is defined. | F10-T3 (3176) and F10-T4 (3177). | Fix | Define an `IExportLease`/lease registry shared by export and delete; delete refuses while a lease is held; the lease is released on completion, failure, or expiry. Implemented by `docs/backend-ui-contract.md`. **Owner: Backend surface for the UI.** |

## D. Frontend design gaps (delegated)

| ID | Finding | Evidence | Disposition | Owner |
|---|---|---|---|---|
| D-01 | The design system is absent: `tokens.css`, `components.css`, previews, Material Symbols, hls.js 1.7.4; MonoLisa is local but unlicensed for web self-hosting pending review. | Plan lines 3047, 3087; filesystem search. | Delegate | **Preserve planning inputs and acquire design assets.** |
| D-02 | The `sk-tz` cookie script and the `.scrolled` observer have no owning task. | F0-T8 (3053); F1-T2 (3062). | Delegate | **Frontend foundation and app shell.** |
| D-03 | Keyboard handling is split across tree navigation, player shortcuts, and search, with no precedence rule. | F2-T6 (3078), F3-T6 (3092), F7-T2 (3140). | Delegate | **Library, search, and live updates; Player, stats, and resume.** |
| D-04 | Sort ties and zero-recording streamers are unspecified. | F2-T1 (3073); `StreamerView` (2966). | Delegate | **Library, search, and live updates.** |
| D-05 | Stats-panel/player shared state, chapter seek API, live-to-VOD/deleted navigation, and virtualization vs tree navigation are underspecified. | F4-T2 (3105), F5-T1 (3115), F8-T3 (3151), F2-T6/T7 (3078–3079). | Delegate | **Player, stats, and resume; Library, search, and live updates; Details, export, delete, auth UX, and settings.** |
| D-06 | Mock seeder and fake Streamlink fixtures are not in the repo; F0-T4 cannot be built from the plan alone. | F0-T4 (3049); Part 1 testing (2802–2815). | Delegate | **Testing strategy and fake-Twitch harness; Preserve planning inputs and acquire design assets.** |

## E. Accepted product choices and deferred questions

| ID | Finding | Disposition | Note |
|---|---|---|---|
| E-01 | Thumbnail strategy: Helix live preview vs a frame grab. | Defer | Map Not-yet-specified; owner **Backend surface for the UI** if it changes F10-T1. |
| E-02 | Schedule button behavior. | Defer | Remains a stub; owner **Details, export, delete, auth UX, and settings**. |
| E-03 | Retention automation and defaults. | Accept | Retention automation is a non-goal; recordings are kept until manually deleted. The spec omits the options and the hourly-service claim. See A-08 and `ADR-0004`. |
| E-04 | Allowlist logins and viewing without sign-in. | Defer | Owner **Twitch polling, EventSub, and auth**. |
| E-05 | HTTPS/reverse proxy for Twitch sign-in. | Defer | Owner **Twitch polling, EventSub, and auth; Hosting**. |
| E-06 | MonoLisa web-font license. | Defer | Owner **Preserve planning inputs and acquire design assets**. |
| E-07 | Subscriber-only stream token handling. | Defer | Owner **Twitch polling, EventSub, and auth**. |
| E-08 | Hover thumbnails dropped. | Accept | No action. |
| E-09 | No hardware target; defaults stand. | Accept | No action. |
| E-10 | HEVC and AV1 recording enabled alongside h264 via `--twitch-supported-codecs h264,h265,av1`. | Accept (risk) | The player must surface unsupported-codec errors; MP4 export should tag HEVC as `hvc1`; test with HEVC/AV1 streams. Owner: **Player, stats, and resume**; **Details, export, delete, auth UX, and settings**. |
| E-11 | The live stitched `#EXT-X-TARGETDURATION` can change when a part spans an ad break, violating RFC 8216 §6.2.1; hls.js tolerates it. | Accept (risk) | Keep the live max-of-parts target; the final index is stable. Revisit if a strict client appears or the drift prevents FFmpeg finalization. Owner: **Session store, stitching, recovery, and retention**. |
| E-12 | No global concurrent-recording cap; one job per channel and channels record in parallel. | Accept | The channel list is user-controlled. Add a cap only if disk or bandwidth pressure appears. Owner: **Supervisor, coordinators, and channel control**. |
| E-13 | An empty `Auth:AllowedTwitchLogins` treats every successfully authenticated Twitch user as an app admin. | Accept (risk) | Opt-in by leaving the allowlist empty; log a startup warning. Set the allowlist for any shared or untrusted network. Viewing/recording stay open. Owner: **Twitch polling, EventSub, and auth**. |
| E-14 | Subscriber-only streams require a manually rotated Streamlink browser token; no Settings entry yet. | Defer | `Recorder:TwitchUserOAuthToken` remains a secret; document rotation and add Settings token entry later. Owner: **Twitch polling, EventSub, and auth**; **Details, export, delete, auth UX, and settings**. |
| E-15 | HTTPS is an external reverse-proxy requirement; Compose publishes plain HTTP 8080. | Accept | Sign-in works over HTTPS or `localhost`; admin features are unavailable over plain LAN HTTP. Provide a TLS proxy outside Compose. Owner: **Hosting, media security, health, and container**. |
| E-16 | Coverage is collected but no percentage gate is enforced initially. | Accept | Add a threshold once the pure core is covered; do not block early work on an arbitrary number. Owner: **Testing strategy and fake-Twitch harness**. |
| E-17 | No automatic test retries; flaky tests are fixed or quarantined with a linked issue and deadline. | Accept | Keeps flakes visible; no silent retries or skips. Owner: **Testing strategy and fake-Twitch harness**. |
| E-18 | Generated design-system CSS is committed; `npm run sync:ds` is deferred until the design system changes. | Accept | No Node runtime; source stays in `design-system/`, built CSS in `src/StreamKeepr/wwwroot/css/`. Add a dev-only sync when needed. Owner: **Frontend foundation and app shell**. |
| E-19 | F2-T7's `<Virtualize>` over variable-height group/day/row items is replaced by all-DOM rendering with `content-visibility`; revisit at roughly 2,000 items. | Accept (deviation) | Preserves keyboard/focus/scroll and avoids a fixed-height virtualizer. Revisit only with measurements. Owner: **Library, search, and live updates**. |
| E-20 | The F3-T2 quality menu and F3-T8 preferred quality are deferred; the player shows a static “Source” pill. | Accept (deferral) | The recorder captures one Streamlink quality and the stitched `index.m3u8` is a single media playlist, so hls.js has no alternate levels to switch. Add both when a multi-variant master playlist exists. Owner: **Player, stats, and resume**. |
| E-21 | E2E playback runs on branded Chrome (`channel: "chrome"`) installed in CI; fullscreen, PiP, and native-HLS transitions stay unit-tested, with native Safari manual. | Accept (environment) | Playwright's bundled Chromium lacks proprietary H.264, so the H.264 Mock fixture cannot play there. Chrome channel provides deterministic playback without changing the app. Owner: **Player, stats, and resume**. |
| E-22 | F5-T2's snackbar-only export progress becomes an inline details-card row, and `IExportService.FindByRecording` is added for rehydration. | Accept (deviation/addition) | An export can outlast a snackbar and navigation; the row keeps progress, Cancel, Download, and Retry next to the job, and one MP4 remains the only output. Incompatible parts are non-retryable. Owner: **Details, export, delete, auth UX, and settings**. |
| E-23 | F11-T2's streamer group menu and player title-strip Stop/Resume are replaced by Settings rows plus a details-card Stop while live; the library shows Paused and no ring. | Accept (deviation) | The player DOM is JS-owned and static, so a .NET stop control there fights the player architecture; Settings is the management surface. No player title-strip control or group action menu in v3. Owner: **Details, export, delete, auth UX, and settings**. |

## Corrected dependency edges

Add or change these edges; then recompute the Part 2 waves from the corrected graph.

- `F10-T7` — remove `F6-T1`; it depends on the backend foundation and Data Protection keys.
- `F6-T1` — add `F10-T7`.
- `F10-T4` — add `F10-T3` (lease registry).
- `F0-T3` — add `F10-T2`, `F10-T5`; if B-05 moves backend-owned fields, remove `F10-T1` and depend on the backend foundation.
- `F1-T2` — add `F0-T3`.
- `F2-T3` — add `F10-T2`.
- `F2-T4` — add `F10-T2`.
- `F9-T1` — add `F10-T2`, `F10-T3`, `F6-T1`.
- `F9-T2` — add `F7-T1`, `F7-T2`, `F7-T3`, `F5-T4`.
- `F9-T4` — add `F0-T2` and `F11-T1` for baseline availability.
- `F9-T5` — split into `F9-T5a` (CI skeleton; depends on `F0-T1`) and `F9-T5b` (full gates; depends on `F9-T5a`, `F9-T1`, `F9-T2`, `F9-T6`, `F3-T1`).
- `F10-T1` — summary fields and profile/UserId resolution move to Part 1; F10-T1 no longer gates `F0-T3`.

## Wave impacts

- `F10-T7` moves to wave 2; `F6-T1` moves to wave 3.
- `F10-T4` moves to wave 3.
- `F9-T5a` is wave 2; `F9-T5b` is wave 10.
- `F0-T3` may move to wave 2 if B-05 removes the `F10-T1` dependency; otherwise it stays wave 3.
- `F9-T1` remains wave 4; `F9-T2` remains wave 9; `F9-T3`/`F9-T4` remain wave 10.
- Recompute all other waves from the corrected edges; do not hand-edit only the table.

## ADRs required

- **ADR-0002 — Media exposure:** allowlisted extensions and dot-segment rejection; why `/media` is not a general static-file root.
- **ADR-0003 — Admin token storage and auth:** `IAdminTokenStore` save/get/clear/wait, Data Protection key persistence, OAuth callback/allowlist, EventSub token boundary.
- **ADR-0004 — Retention non-goal:** retention automation is out of scope; recordings are kept until manually deleted, and the options/claims are removed.
- **ADR-0005 — Part 1/Part 2 buildability:** minimal Blazor shell in the backend foundation vs treating Part 1 as non-standalone. This records the boundary and prevents the plan from claiming a build that does not exist.
- **ADR-0001** already records the one-project/strangler choice.

## Review method (repeat for the post-implementation review)

1. **Source check:** every binding claim is checked against the plan text or a named source; code snippets are treated as reference.
2. **Scenario walkthrough:** crash during a part, offline blip, ad break, disk full, delete during export, restart during recording, EventSub outage, token expiry.
3. **Graph check:** every Done criterion’s services exist and are scheduled before the task; topological order has no cycles; waves are recomputed.
4. **Security check:** `/media` allowlist, auth gating, token encryption/keys, secrets, path traversal.
5. **Evidence format:** finding ID, plan line/code path, scenario, disposition, owner, and the binding sentence.
6. **Post-implementation:** repeat 1–5 against the built app; report gaps as new tickets, never silently accept them.

## Open items

- E-01, E-02, E-04, E-05, E-06, E-07 remain unresolved product questions; they do not block the backend route.
- B-05 and C-01 are confirmed: backend-owned fields move into Part 1, and export is always a single MP4 with an explicit incompatible-parts error.
