# Stream Keepr Details, Export, Delete, Auth, and Settings Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 2, F5/F6/F10/F11; `docs/backend-ui-contract.md`; `docs/twitch-auth-contract.md`; `docs/hosting-contract.md`; `docs/runtime-contract.md`; `docs/library-contract.md`; `docs/player-contract.md`; `docs/frontend-foundation-contract.md`; `docs/testing-contract.md`; plan review E-22/E-23.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract covers the Recording details card, chapter seeking, Copy playlist URL, MP4 export, recording deletion, the Twitch admin sign-in UX, the `/settings` page, and the stop/resume surfaces. It consumes the existing backend services and makes one additive backend-UI change: `IExportService.FindByRecording`.

The backend contract wins over the plan's F10-T3: export is **always one MP4**. The old zip-per-part fallback is gone; incompatible parts are a failed job with no file.

## 2. Recording details card

- `RecordingDetails.razor` is a normal Blazor component, rendered under the stats panel on `/r/{id}`. It uses the `Collapsible` primitive and `UiPreferences.DetailsOpen` (default open); the collapsed header is 48 px and shows “Recording details” plus the start date.
- The body shows:
  - title (`headline-small`);
  - a meta row with the streamer login and avatar/initials, category (or “No category”), full start date/time in the viewer's zone, and counts in the form `1,342 segments · 3 parts · 9.8 GB`; `SizeBytes` is omitted or shown as `—` while Recording/Reconnecting;
  - a chapters list when `RecordingView.Chapters` is non-empty: one button per chapter with the timecode, title, and game/category; clicking it seeks through `PlayerBridge` and leaves focus on the button. The player scrolls into view only as an implementation detail; it must not autoplay;
  - the actions row and, when applicable, the export status row.
- The start date uses `ViewerClock.ToLocal` and a full `ddd, MMM d, yyyy · h:mm tt`-style value; it re-renders when the clock service changes. The details component subscribes to `ViewerClock.Changed` and disposes.
- The card re-renders when `RecordingView`, `UiPreferences`, `IExportService.Changed`, or the relevant channel state changes. It never reads files or calls the supervisor.
- A recording deleted while the card is open closes the dialog/row through the normal `Home` navigation; no stale title is left behind.

## 3. Chapters

- Chapters come only from `RecordingView.Chapters` (`StartSeconds`, `Title`, `Game`). Empty or invalid data produces no list and no player ticks.
- Each entry is a `<button>` with an accessible name such as “Seek to 1:02:13 — Celeste (game)”. The timecode uses `Format.Timecode`; the chapter title is the stream title; the game is shown when present.
- The list updates with the `RecordingView` instance; `@key` by `StartSeconds`.
- Seeking while playing continues playback; seeking while paused stays paused. No progress is written just because a chapter was clicked.

## 4. Copy playlist URL

- The button is an Outlined action and is public; it is never wrapped in `RequiresTwitch`.
- The absolute URL is built from `NavigationManager.BaseUri` and the relative `RecordingView.PlaylistUrl` (`/media/{channel}/{id}/index.m3u8`). Use `new Uri(new Uri(NavigationManager.BaseUri), playlistUrl).AbsoluteUri`; never construct the media path by hand. Forwarded-header configuration makes this the public origin.
- Copy through the clipboard API; confirm “Playlist URL copied” through `SnackbarService`. A missing/rejected clipboard shows “Could not copy playlist URL”. No legacy fallback.
- Copy failure is not an auth error and never opens a dialog.

## 5. Export MP4

### Backend addition

`IExportService` gains:

```csharp
ExportJob? FindByRecording(string recordingId);
```

- Returns the most recent job for that recording: the active job while Queued/Running, otherwise the latest result.
- Returns `null` when there is no job, or when a Succeeded job's file has been cleaned up (the 24 h sweep). `Find(exportId)` and the download endpoint keep their 404 semantics.
- Lets the details card rehydrate after navigation, reload, or another tab. The service remains singleton, one export at a time, FIFO; `Changed` still fires at most every 500 ms.
- `ExportJob.Error` uses the stable code `ExportIncompatibleParts` for incompatible parts; other values are user-safe messages.

### UI states

The details card subscribes to `IExportService.Changed`, calls `FindByRecording` on init/parameter changes/events, and renders one row:

| Job state | Row | Actions |
|---|---|---|
| none | No row; Export MP4 enabled when Complete and signed in | Export MP4 |
| Queued | “Queued — waiting for another export” | Cancel |
| Running | “Exporting… 42%” with a progressbar | Cancel |
| Succeeded | “MP4 ready” and the file name/size when available | Download |
| Failed (`ExportIncompatibleParts`) | “This recording's parts cannot be joined into one MP4.” | none; Export stays disabled |
| Failed (other) | User-safe error message | Retry |
| Cancelled | “Export cancelled” | Retry |

- `Export MP4` is a Tonal action. It is disabled:
  - signed out → `RequiresTwitch` tooltip “Connect Twitch to use this”;
  - Recording/Reconnecting → tooltip “Available when the recording finishes”;
  - an active job for this recording → the row is the action surface;
  - an incompatible failure for this recording → tooltip explaining that one MP4 is impossible.
- `Start` requires the authenticated `ClaimsPrincipal` and a Complete recording. Expected exceptions are caught: `InvalidOperationException` (live), `KeyNotFoundException` (unknown), `UnauthorizedAccessException` (session), otherwise a generic message. The UI never relies on the disabled button alone.
- `Cancel` calls `IExportService.Cancel(job.Id, user)`; no confirmation because Retry is available. Cancelling a Queued job removes it. A failed cancel re-reads the job and ignores an already-finished state.
- `Retry` calls `Start` again for the same recording. It is hidden for `ExportIncompatibleParts`; the server would produce the same failure.
- Progress is `job.Progress` clamped to `[0,1]`. The row is a `role="status"` for state changes; the progressbar is `aria-valuemin=0`, `aria-valuemax=100`, `aria-valuenow`, and `aria-valuetext="42%"`. Per-percent changes are not announced as live text.
- Completion or failure raises one snackbar (“Export ready”, “Export failed”); cancellation does not. The row remains visible while the details card is open and is rehydrated after navigation.
- The Download action is a normal anchor to `/api/exports/{job.Id}/download` with `data-enhance-nav="false"`; the endpoint sets `Content-Disposition`. It requires sign-in and is wrapped in `RequiresTwitch`; the anchor is disabled while signed out/expired. If the session expires between click and response, the browser shows the endpoint's 401/403 and the user can return and reconnect. Range requests are handled by the server.
- While an export for this recording is Queued/Running, Delete is disabled with the reason “Cancel the export first”; the server's lease is the final guard.
- The export row is hidden when the details card is collapsed; the completion snackbar still fires. The job is independent of the circuit and continues if the user navigates away.
- A new export replaces the row only after `Start` returns; a stale job is never shown as current for a different recording.

## 6. Delete recording

- The Delete action is an icon button (`aria-label="Delete recording"`) in the details actions, wrapped in `RequiresTwitch`. It is disabled for Recording/Reconnecting and while this recording has a Queued/Running export, with a visible tooltip reason.
- On click, open the `Dialog` primitive:
  - title “Delete recording?”;
  - an `error-container` banner naming the recording (`title — streamer`) and stating that the files and progress are removed permanently;
  - Danger “Delete” and Text “Cancel”; focus lands on Cancel; Esc closes; focus returns to the trigger.
- On confirm, set the dialog busy and call `IRecordingAdmin.DeleteAsync(id, user, ct)`; the server checks Complete-only, the session lock, and the export lease.
- Success: close the dialog, raise “Recording deleted”, let `ILibraryView.Changed` update the library, and navigate to the next newest recording in the current query (or `/`), per `docs/player-contract.md`. The dialog never remains over the next recording.
- Refusal/race: an `InvalidOperationException` (live or leased) keeps the dialog open and shows its user-safe message in the banner; refresh the details state. `KeyNotFoundException` closes and shows “Recording was already deleted”. `UnauthorizedAccessException` closes and shows the reconnect message. Other failures stay in the dialog with a generic message.
- Deleting a recording does not delete an already-produced export file; that file expires with the 24 h export sweep. The dialog copy does not promise otherwise (known limitation).

## 7. Twitch admin auth UX

### Top bar states

- **Disconnected:** a real `<a class="sk-twitch">` labelled “Connect with Twitch” to `/api/auth/twitch/login?returnUrl=<current path+query>`; `data-enhance-nav="false"` so the OAuth redirect is a full-page navigation. A tiny collocated JS handler may set `aria-busy`, the spinner, and “Waiting for Twitch…” on click; the link works without JS.
- **Connecting:** `aria-busy="true"`, spinner, “Waiting for Twitch…”, Twitch hover fill. No circuit call is required.
- **Connected:** `.sk-auth-chip` with avatar/initials, login (and display name when available), a success dot, and a menu:
  - “Manage Twitch connection” → `/settings#twitch`;
  - “Disconnect” → a submit button for a real `POST /api/auth/logout` form containing `<AntiforgeryToken />` and a hidden local `returnUrl`; the form post signs out, clears the admin token, and redirects to the current local path (or `/`).
- **Expired:** the account chip is replaced by an `error-container` chip “Twitch session expired” and a text Reconnect link to the login endpoint with the current return URL. The current page and playback continue; admin actions are inert because the revalidating provider signs the circuit out. No dismiss action.

### Expiry

- `RevalidatingServerAuthenticationStateProvider` re-checks the stored token against `id.twitch.tv/oauth2/validate` every 30 minutes. On failure it signs out, raises `AuthenticationStateChanged`, and sets a scoped `AdminSessionState` (`Expired = true`, last login) that the top bar and Settings read. Login/logout clears it.
- The 30-minute interval uses `TimeProvider`; tests use a fake validator and `FakeTimeProvider`, not a real wait.

### Sign-in failures

- Known failures redirect back to the local return URL with one `authError` query key:
  - `notAllowed` — the login is not in `Auth:AllowedTwitchLogins`;
  - `httpsRequired` — the request is not HTTPS and not localhost;
  - `failed` — remote/OAuth failure.
- A shell-level notifier shows one snackbar, then removes only `authError` with `replace: true`, preserving `q`/`state`/`sort` and the current path. Messages: “This Twitch account is not allowed to manage Stream Keepr.”, “Twitch sign-in requires HTTPS except on localhost.”, “Twitch sign-in failed. Try again.”
- Empty `AllowedTwitchLogins` keeps the accepted open-admin behavior (E-13); no UI gate beyond successful authentication.
- Return URLs are local paths only (`/...`); external URLs fall back to `/`. The login link builds the current path and query, excluding fragments and an existing `authError`.
- Settings shows a short note when the page is HTTP and the host is not localhost/loopback: Twitch sign-in needs HTTPS through the reverse proxy. The Connect button remains visible.

### Antiforgery and cookies

- Logout is the only HTML form post; it includes an antiforgery token and is rejected without one. Login is a GET; the OAuth handler owns its state parameter. Export/delete/channel mutations are authenticated circuit calls, not form posts.
- The `sk-auth` cookie remains HttpOnly, SameSite=Lax, 7-day sliding, secure under HTTPS; tokens never reach the browser. The UI displays only login/display name/avatar.

## 8. Settings page

- `/settings` is a public route rendered in the app shell. The top bar settings icon links to it.
- Signed out: a centered panel “Connect Twitch to manage channels” with the sign-in link (return URL `/settings`) and the HTTPS note; no channel data, no `ChannelsView` calls, no redirect.
- Signed in, the page has these sections:
  1. **Twitch account** (`#twitch`): avatar/initials, login/display name, connected dot, Disconnect form post; if expired, the expired chip and Reconnect; the HTTPS caveat when applicable.
  2. **Push status**: “Push notifications on” when every watched channel reports `PushConnected`; otherwise “Polling only — <reason>” using the first non-connected reason (`No admin token` → connect/reconnect to enable push, `Partial coverage` → some channels are not subscribed, `EventSub reconnecting` → push is reconnecting, `Polling only` → polling every 30 seconds). No channels → “No channels watched.” The line is `role="status"`; each row also carries a `Push`/`Polling` badge with `title=PushReason`.
  3. **Channels**: watched channels sorted by display name then login. Each row has avatar/initials, display name, login, a state chip, the push badge, and actions.
     - State labels: Recording (“Rec”), Reconnecting (“Reconnecting”), Watching (“Watching”), Paused (“Paused”), CoolingDown (“Cooling down”). Paused channels show no live ring in the library; the group meta says “Paused”.
     - Actions: Stop recording (confirm; finalizes an open capture and parks the channel in Paused), Resume recording (immediate; next observation decides), Remove (confirm).
     - Remove dialog: “Remove {name}? Recordings are kept. An open capture is finalized first.” On confirm, close the dialog, show “Removing…” on the row, disable its actions, and await `RemoveAsync` (up to ~35 s). Success removes the row and raises “Channel removed”; failure restores the row and shows the user-safe error. The row's `@key` is the login.
     - Stop confirmation: “Stop recording for {name}? The current capture is finalized, and the channel stays paused until you resume.” For an idle channel the label may read “Pause channel” with equivalent copy. Resume has no confirmation.
  4. **Add channel**: a labelled login input and Add button (or form submit). Trim and lowercase; disable while empty/busy; show a spinner/`aria-busy`; on success clear the input, keep focus in it, and raise “Channel added”. Inline error (`role="alert"`) for unknown login, already watched, invalid login, Helix/network failure, or unauthorized/expired session. `ChannelsView.AddAsync` validates through Helix.
- No schedule section; the top-bar schedule icon remains a stub and shows “Capture schedule is not available yet” in a snackbar.
- The page subscribes to `ChannelsView.Changed` (500 ms throttle) and re-renders rows with `@key`; it disposes the subscription. `ChannelsView.All` and every mutation throw when signed out; the signed-out branch never instantiates the channel component.
- Authorization is enforced by `ChannelsView` and the services as well as `RequiresTwitch`; a disabled control is never the only guard.

## 9. Stop and resume surfaces

This contract supersedes the plan's F11-T2 surfaces (E-23):

- Stop/Resume live in the **Settings channel rows**.
- The **details card** shows “Stop recording” only while the selected recording is Recording/Reconnecting, wrapped in `RequiresTwitch`, with the same confirmation. It calls `IChannelsView.StopAsync(RecordingView.StreamerLogin, ct)`; an unauthorized/expired session shows the reconnect message.
- The **library group meta** shows “Paused” and no live ring when `StreamerView.IsPaused`; it otherwise keeps “Recording now”, “Reconnecting”, or the last-capture line. There is no streamer group action menu and no player title-strip Stop control in v3; the player title strip stays JS-owned and unchanged.
- Resume is available from Settings. A paused channel is not in the shell's REC dot or live rings.
- All surfaces update through `ILibraryView.Changed` / `ChannelsView.Changed`; no local optimistic state survives a refresh.

## 10. Library and player reactions

- `RecordingView.State` drives Export/Delete enablement and the details Stop visibility: Recording/Reconnecting disables Export and Delete; Partial/Saved enables them (subject to auth and export state).
- Stopping from the details card finalizes the capture; the player follows the recorder contract (live → VOD when ENDLIST arrives) and continues to play; the library shows Paused and no live ring.
- Deleting navigates per the player contract; the details card never blocks the next recording with a stale dialog.
- An expired session disables admin actions but does not pause playback or hide public content. Reconnecting restores the actions.
- Logout while an export runs does not cancel the export; the row remains, and Download/Retry require sign-in again. Disconnecting clears the admin token, stops EventSub, and drops to polling.

## 11. Edge cases

- **Export started in another tab:** `FindByRecording` shows the active job; this tab's Export is disabled.
- **Export queued behind another recording:** row says Queued; Cancel removes it.
- **Export file cleaned up:** `FindByRecording` returns null; the row disappears and Export is enabled.
- **Incompatible parts:** failed job, no Retry, Export disabled; the message explains that one MP4 is impossible.
- **Delete/lease race:** the dialog stays open with the server's message; Delete becomes disabled after the state refresh.
- **Delete/already gone:** dialog closes; snackbar; Home navigates or shows the unknown-id message.
- **Auth expires mid-action:** the service throws Unauthorized; the UI shows the reconnect snackbar and the top bar expired chip; the operation is not retried automatically.
- **Auth error query with other params:** only `authError` is removed; `q`/`state`/`sort` survive.
- **Logout/antiforgery expiry:** the post is rejected; the user can reload and sign out again; no stale cookie action.
- **Clipboard denied:** snackbar error; the URL is not displayed as a fallback.
- **Download 401/403/404/409:** the anchor is a full-page navigation, so the browser shows the endpoint response; the UI disables it while signed out/expired, leaving only a session expiring between click and response or a cleaned/absent file. The back button returns; reconnect or a fresh export recovers.
- **Remove while recording:** row shows Removing… until finalize; recordings remain.
- **Add duplicate/unknown:** inline error; the input keeps the value for correction.
- **Push partial coverage:** all rows show Polling; the summary explains the first reason.
- **No channels:** settings shows the empty state and “No channels watched.”
- **Non-HTTPS LAN:** settings shows the caveat; the Connect link yields the `httpsRequired` snackbar.
- **Empty library/channel list:** no crashes; the shell still renders.

## 12. Mock profile and tests

- The Mock profile provides fake auth: signed in as a fixed mock admin by default; the visible Connect/Disconnect flow changes the fake session, and a documented signed-out toggle exists for E2E. No Twitch credentials are required.
- The fake `ChannelsView`/Helix accepts known mock logins and rejects unknown ones; the fake event/push state drives the settings summary. The real export path can run against a tiny generated fixture; the details/export row works end to end.
- Expired and non-allowed states use fake providers/validators and `FakeTimeProvider`; E2E covers the visible sign-in/out journey, not a 30-minute wait.

bUnit:

- Details fields, date, counts, chapters, collapse persistence, empty chapters, live/disabling rules.
- Export row for every state, progressbar ARIA, Cancel/Retry/Download, incompatible message, snackbars, rehydration, subscription disposal.
- Delete dialog, banner, busy, success/snackbar/navigation, lease/live/unknown/unauthorized refusals, focus return.
- `TwitchAuthButton` disconnected/connecting/connected/expired, menu focus, logout form/token, return URL.
- Settings signed-out prompt; account; push summary/reasons; channel rows/state labels; add success/errors; remove busy/kept-recordings; stop/resume confirms; empty state; `ChannelsView.Changed`.
- Schedule snackbar; copy playlist absolute URL/clipboard failure.

Integration:

- `FindByRecording` latest/rehydration/cleanup; `ExportService.Start` auth/live/unknown/incompatible; progress event; cancel; download auth/range/404/409.
- `RecordingAdmin.DeleteAsync` auth/Complete/lock/lease; progress cleanup; library event.
- Login local-only return URL; allowlist accept/reject; empty allowlist; HTTPS rejection; callback path; cookie flags; logout antiforgery; non-HTTPS message; forwarded headers.
- `ChannelsView` unauthorized throws; add Helix unknown/known; remove/stop/resume; push reason variants.

Playwright (Chrome channel, Mock profile):

- Sign in and out with the fake admin; expired/not-allowed are fake-state tests.
- Details: chapters seek, copy playlist, export start/progress/download, incompatible failure (fixture), cancel/retry, delete and next-item navigation, lease refusal.
- Settings: add known/unknown, remove with finalize, stop/resume, push summary, empty state, signed-out prompt.
- Auth error snackbar/query removal; HTTPS caveat where testable; schedule snackbar; axe and keyboard-only.
- No automatic retries; flaky tests are fixed or quarantined per the testing contract.

## 13. Binding versus implementation freedom

Binding: details fields/chapters/collapse, absolute copy, `FindByRecording` and the export row states/rules, always-one-MP4/incompatible handling, delete dialog and failure paths, auth states/redirects/return URLs/antiforgery/expiry, settings structure/auth gating/push summary/channel actions, stop/resume surfaces (E-23), library Paused meta, Mock auth behavior, and the test matrix.

Freedom: exact panel markup beyond the `sk-` classes, wording of non-binding messages, dialog animation, channel row layout, push reason copy, add-form validation wording, and test implementation.

## 14. Known limitations

- Stop/resume surfaces are Settings plus the details card; the plan's streamer group menu and player title-strip control are absent (E-23).
- An already-produced export file survives recording deletion until the 24 h sweep; deleting the recording does not revoke a downloaded copy.
- Export jobs are in-memory: a host restart loses queued/running jobs (a completed file may remain until cleanup). One process-wide export runs at a time.
- The schedule button is a stub.
- Admin sign-in requires HTTPS except on localhost; plain LAN HTTP can view and record but cannot administer.
- Empty `AllowedTwitchLogins` means open administration (E-13); set the allowlist on shared networks.
- No per-user progress or preferences.

## 15. Related

- `docs/backend-ui-contract.md` for `RecordingView`, `ILibraryView`, `IExportService`, `IRecordingAdmin`, `IChannelsView`, and the new `FindByRecording`.
- `docs/twitch-auth-contract.md` for OAuth, allowlist, cookie, token store, and EventSub.
- `docs/hosting-contract.md` for `/media`, endpoints, antiforgery, and forwarded headers.
- `docs/runtime-contract.md` for channel control, stop/resume, and remove.
- `docs/library-contract.md` for the drawer, pause meta, and live updates.
- `docs/player-contract.md` for delete navigation and playback continuity.
- `docs/reviews/implementation-plan-amendments.md` (E-22, E-23).
