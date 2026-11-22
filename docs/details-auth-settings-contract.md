# Stream Keepr Details, Export, Delete, Auth, and Settings Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 2, F5/F6/F10/F11; `docs/backend-ui-contract.md`; `docs/twitch-auth-contract.md`; `docs/hosting-contract.md`; `docs/runtime-contract.md`; `docs/library-contract.md`; `docs/player-contract.md`; `docs/frontend-foundation-contract.md`; `docs/testing-contract.md`; plan review E-22/E-23; #175 (the watch layout split).
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract covers the Recording details card, chapter seeking, Copy playlist URL, MP4 export, recording deletion, the Twitch admin sign-in UX, the `/settings` page, and the stop/resume surfaces. It consumes the existing backend services and makes one additive backend-UI change: `IExportService.FindByRecording`.

The backend contract wins over the plan's F10-T3: export is **always one MP4**. The old zip-per-part fallback is gone; incompatible parts are a failed job with no file.

## 2. Recording details card

- `RecordingDetails.razor` is the **title block** under the player (design system: RecordingDetails, Chapters and Buttons cards, #178), a normal Blazor component on `/r/{id}`. It composes `RecordingDetailsSummary` (the title, meta line and actions) and `RecordingDetailsBody` (the export row) on the page ground, outside any panel: nothing collapses it, so the title and actions always show under the player. The chapters are their own panel under it, `ChaptersPanel`: an outlined `MudPaper` whose header is a real disclosure `<button>` (`aria-expanded`, `aria-controls`, hook `data-collapsible-toggle`) over a `MudCollapse`; the collapsed body is `inert`. It is not a `MudExpansionPanel`, because MudBlazor 9.12's panel header is a focusable `div` with no button role or `aria-expanded`. `UiPreferences.DetailsOpen` (default open; the slot the old Recording details panel used) drives it; the 48 px header shows a `Toc` icon, “Chapters” (`h6`, an `h2`) and the count (`caption`, “1 chapter”, “4 chapters”, `[data-chapter-count]`). A recording with no showable chapters renders no panel.
- The title block shows, in order:
  - on the left, the title (`h5`, an `h2`; `h6` below Md; hook `data-details-title`), wrapping to two lines before it ellipsises, then the meta line under it; on the right, the actions, their top lined up with the title's first line, never wrapping; a `MudTooltip` on it carries the full title and opens on hover over the text or on keyboard focus (the title is a tab stop, `tabindex=0`). Nothing shares that row;
  - one row with the meta line on the left and the actions on the right; when space runs out the actions wrap under the meta, never beside the title. The meta line (`caption`) starts with the live `StatusChip` (REC while Recording, Reconnecting while Reconnecting; none once finished), then the streamer login with its 24px `ChannelAvatar` (the channel's Twitch profile image, passed in as `AvatarUrl`; its initial on its channel colour without one; `[data-details-avatar]`), then icon + text pairs with `icon-inline` (16 px) icons in `action-default`: `SportsEsports` category (or “No category”), `Event` full start date/time in the viewer's zone, `Timer` duration (`Format.Duration`, e.g. `2h 14m`; hook `data-details-duration`), `FolderOpen` counts in the form `1,342 segments · 3 parts`, and `Storage` size (`9.8 GB`). The size pair is omitted while Recording/Reconnecting;
  - the export status row, when applicable (§5);
  - a chapters list when `RecordingView.Chapters` is non-empty: one button per chapter with the timecode, title, and game/category; clicking it seeks through `PlayerBridge` and leaves focus on the button. The player scrolls into view only as an implementation detail; it must not autoplay.
- Actions (design system RecordingDetails), one row of icon buttons at every width in a `role="group"` named “Recording actions”: Copy playlist URL (`PlaylistPlay`, `action-default`), Export MP4 (`Output`, the one emphasised action: tonal `primary` on a 14% `primary` tint), then a `MoreVert` `MudMenu` “More actions” holding Stop recording (`Stop`, `error`; live only; `[data-stop-recording]`), a divider, and Delete recording (`Delete`; `[data-delete]`). Each icon button has a `MudTooltip` with its name and a matching `aria-label`. Actions that need Twitch stay visible behind `RequiresTwitch`; a blocked Export is disabled inside its reason's tooltip (“Exporting…” while its export runs, `[data-export-blocked]`), and a blocked Delete is a disabled menu item (`aria-disabled`) with its reason under the label in `caption` (`[data-delete-blocked]`). When a Stop or Delete dialog closes on the same recording, focus returns to the More actions button, because the item that opened it is gone.
- Below `Breakpoint.Md` (960 px), the title, meta and actions are rendered by `RecordingDetailsSummary` directly under the player, outside the watch tabs, and `RecordingDetailsBody` (the export row and chapters) becomes the **Details** tab. The phone summary's meta line keeps the streamer, start and duration; the live chip leads the actions row; category, segments/parts and size stay in the desktop card. Stop recording and Delete leave the row for a `MoreVert` menu (`MudMenu`, `aria-label="More actions"`, itself behind `RequiresTwitch`) with the same rules: Stop opens the stop confirmation, Delete opens the delete dialog, and a blocked Delete is a disabled item showing its reason. CSS shows one form or the other, so both render and the row's `data-stop-recording`/`data-delete` hooks stay unique.
- The start date uses `ViewerClock.ToLocal` and `Format.FullDate` (`ddd, MMM d, yyyy · h:mm tt`); the header and the summary re-render when the clock service changes. Both subscribe to `ViewerClock.Changed` and dispose.
- `RecordingDetails` re-renders on `UiPreferences.Changed`; `RecordingDetailsSummary` on the clock and `IExportService.Changed`, and the summary's actions read the export service's current job; `RecordingDetailsBody` subscribes to `IExportService.Changed` for the export row and its completion snackbars. None reads files or calls the supervisor.
- A recording deleted while the card is open closes the dialog/row through the normal `Home` navigation; no stale title is left behind.

## 3. Chapters

- Chapters come only from `RecordingView.Chapters` (`StartSeconds`, `Title`, `Game`). Empty or invalid data produces no list and no player ticks.
- Each entry is a `<button>` with an accessible name such as “Seek to 1:02:13 — Celeste (game)”. The timecode uses `Format.Timecode` in `timecode` type and `tertiary`, turning `text-primary` on the hovered row (`surface-raised`), where `tertiary` text is under 4.5:1; the chapter title is the stream title in `subtitle2`; the game is shown, in `caption`, when present.
- The list updates with the `RecordingView` instance; `@key` by `StartSeconds`.
- No chapter is marked current yet (the card's `selection` highlight needs the player's position in the card).
- Seeking while playing continues playback; seeking while paused stays paused. No progress is written just because a chapter was clicked.

## 4. Copy playlist URL

- The button is an outlined `MudButton` with the `PlaylistPlay` icon (it copies the HLS playlist's address) and is public; it is never wrapped in `RequiresTwitch`.
- The absolute URL is built from `NavigationManager.BaseUri` and the relative `RecordingView.PlaylistUrl` (`/media/{channel}/{id}/index.m3u8`). Use `new Uri(new Uri(NavigationManager.BaseUri), playlistUrl).AbsoluteUri`; never construct the media path by hand. Forwarded-header configuration makes this the public origin.
- Copy through the clipboard API; confirm “Playlist URL copied” through a snackbar (`ISnackbar.Show`). A missing/rejected clipboard shows “Could not copy playlist URL”. No legacy fallback.
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

`RecordingDetailsBody` subscribes to `IExportService.Changed`, calls `FindByRecording` on init/parameter changes/events, and renders one row:

| Job state | Row | Actions |
|---|---|---|
| none | No row; Export MP4 enabled when Complete and signed in | Export MP4 |
| Queued | “Queued — waiting for another export” | Cancel |
| Running | “Exporting… 42%” with a progressbar | Cancel |
| Succeeded | “MP4 ready” and the file name/size when available | Download |
| Failed (`ExportIncompatibleParts`) | “This recording's parts cannot be joined into one MP4.” | none; Export stays disabled |
| Failed (other) | User-safe error message | Retry |
| Cancelled | “Export cancelled” | Retry |

- `Export MP4` is the filled `Color.Primary` action with the `Output` icon. It is disabled:
  - signed out → `RequiresTwitch` tooltip “Connect Twitch to use this”;
  - Recording/Reconnecting → tooltip “Available when the recording finishes”;
  - an active job for this recording → the row is the action surface;
  - an incompatible failure for this recording → tooltip explaining that one MP4 is impossible.
- `Start` requires the authenticated `ClaimsPrincipal` and a Complete recording. Expected exceptions are caught: `InvalidOperationException` (live), `KeyNotFoundException` (unknown), `UnauthorizedAccessException` (session), otherwise a generic message. The UI never relies on the disabled button alone.
- `Cancel` calls `IExportService.Cancel(job.Id, user)`; no confirmation because Retry is available. Cancelling a Queued job removes it. A failed cancel re-reads the job and ignores an already-finished state.
- `Retry` calls `Start` again for the same recording. It is hidden for `ExportIncompatibleParts`; the server would produce the same failure.
- The row sits on `surface-raised`: an `Output` icon in `secondary`, the state text, and while Running the percent in `timecode` and a `MudProgressLinear` in `secondary`, then the text Cancel/Retry buttons or a tonal Download link (`Download` icon).
- Progress is `job.Progress` clamped to `[0,1]`. The row is a `role="status"` for state changes; the progressbar is `aria-valuemin=0`, `aria-valuemax=100`, `aria-valuenow`, and `aria-valuetext="42%"`. Per-percent changes are not announced as live text: the progressbar sets `aria-live="off"` over MudBlazor's default polite region.
- Completion or failure raises one snackbar (“Export ready”, “Export failed”); cancellation does not. The row remains visible under the title block and is rehydrated after navigation.
- The Download action is a normal anchor to `/api/exports/{job.Id}/download` with `data-enhance-nav="false"`; the endpoint sets `Content-Disposition`. It requires sign-in and is wrapped in `RequiresTwitch`; the anchor is disabled while signed out/expired. If the session expires between click and response, the browser shows the endpoint's 401/403 and the user can return and reconnect. Range requests are handled by the server.
- While an export for this recording is Queued/Running, Delete is disabled with the reason “Cancel the export first”; the server's lease is the final guard.
- The export row sits under the title block at every width (below Md, under the phone summary, outside the tabs); the completion snackbar fires either way. The job is independent of the circuit and continues if the user navigates away.
- A new export replaces the row only after `Start` returns; a stale job is never shown as current for a different recording.

## 6. Delete recording

- The Delete action is the “Delete recording” item in More actions (§2), wrapped in `RequiresTwitch`. It is disabled for Recording/Reconnecting (“Available when the recording finishes”) and while this recording has a Queued/Running export (“Cancel the export before deleting”), with the reason under its label.
- On click, open a `ConfirmDialog` (an inline `MudDialog`, design system Dialog card; #177; see the foundation contract §8): extra small, full width, no close button:
  - title “Delete recording?” (`h6`), which names the `dialog`;
  - a `MudAlert` Error banner (`Variant.Text`, dense) naming the recording (`title — streamer`) and stating that the files and progress are removed permanently; it is a `note` until it carries an error, then an `alert`;
  - Text “Cancel” first, then filled Error “Delete”; focus lands on Cancel; Esc and the backdrop close it; focus returns to the trigger.
  - MudDialog applies its own attributes only when it opens, so `data-delete-dialog` and `aria-busy` sit on the wrapper inside the content (which also holds the buttons), not on the `dialog` element.
- On confirm, set the dialog busy (both buttons disabled, `aria-busy`, Esc and the backdrop ignored) and call `IRecordingAdmin.DeleteAsync(id, user, ct)`; the server checks Complete-only, the session lock, and the export lease.
- Success: close the dialog, raise “Recording deleted”, let `ILibraryView.Changed` update the library, and navigate to the next newest recording in the current query (or `/`), per `docs/player-contract.md`. The dialog never remains over the next recording.
- Refusal/race: an `InvalidOperationException` (live or leased) keeps the dialog open and shows its user-safe message in the banner; refresh the details state. `KeyNotFoundException` closes and shows “Recording was already deleted”. `UnauthorizedAccessException` closes and shows the reconnect message. Other failures stay in the dialog with a generic message.
- Deleting a recording does not delete an already-produced export file; that file expires with the 24 h export sweep. The dialog copy does not promise otherwise (known limitation).

## 7. Twitch admin auth UX

### Top bar states

- **Disconnected:** a real `<a class="sk-twitch">` labelled “Connect with Twitch” to `/api/auth/twitch/login?returnUrl=<current path+query>`; `data-enhance-nav="false"` so the OAuth redirect is a full-page navigation. A tiny collocated JS handler may set `aria-busy`, the spinner, and “Waiting for Twitch…” on click; the link works without JS.
- **Connecting:** `aria-busy="true"`, spinner, “Waiting for Twitch…”, Twitch hover fill. No circuit call is required.
- **Connected** (amended for the MudBlazor shell, #174): the avatar only, at every width. A `MudMenu` (`[data-auth-state=connected]`, anchored bottom-right) whose activator is a 32px `ChannelAvatar` (the account's Twitch profile image from the sign-in claim when it is an https URL, otherwise its initial on its login's channel colour, #176) and a `success` `MudBadge` dot; its accessible name is “Twitch account: {display name}”, from visually hidden text. The menu opens with a header that is not a menu item (avatar, display name, the login when it differs, and “Connected to Twitch” beside a success dot), a divider, then:
  - “Manage Twitch connection” → link to `/settings#twitch`;
  - “Settings” → link to `/settings`; with the circuit, the shell opens the settings sheet over the current page instead (§8a, #183);
  - “Disconnect” → submits a real `POST /api/auth/logout` form containing `<AntiforgeryToken />` and a hidden local `returnUrl` (`TwitchAccount.razor.js` `submitForm`, because a `MudMenuItem` cannot be a submit button); the form post signs out, clears the admin token, and redirects to the current local path (or `/`).
- **Expired:** the account avatar is replaced by an `error-container` chip “Twitch session expired” and a text Reconnect link to the login endpoint with the current return URL. The current page and playback continue; admin actions are inert because the revalidating provider signs the circuit out. No dismiss action.

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
- Non-empty `AllowedTwitchLogins` makes the app private (ADR-0013): the failures above show on the `/signin` page (design system `SignIn`) rather than as a snackbar, and the signed-out states in this contract (`RequiresTwitch` tooltips, the Settings connect prompt, the expired chip) cannot occur, because no signed-out visitor reaches the app.
- Return URLs are local paths only (`/...`); external URLs fall back to `/`. The login link builds the current path and query, excluding fragments and an existing `authError`.
- Settings shows a short note when the page is HTTP and the host is not localhost/loopback: Twitch sign-in needs HTTPS through the reverse proxy. It is a dense info `MudAlert` with `role="note"` (`[data-https-note]`, #179), above the sections. The Connect button remains visible.

### Antiforgery and cookies

- Logout is the only HTML form post; it includes an antiforgery token and is rejected without one. Login is a GET; the OAuth handler owns its state parameter. Export/delete/channel mutations are authenticated circuit calls, not form posts.
- The `sk-auth` cookie remains HttpOnly, SameSite=Lax, 7-day sliding, secure under HTTPS; tokens never reach the browser. The UI displays only login/display name/avatar.

## 8. Settings page

Amended for the MudBlazor settings page (#179; design-system cards SettingsPage, ChannelSettings, ConnectionStatus, StatusChip).

- `/settings` is a public route rendered in the app shell, for bookmarks and direct visits. The top bar's, the bottom bar's and the account menu's Settings links reach it before the circuit connects; with the circuit they open the settings sheet (§8a) on every other page.
- **Layout:** a column capped at `form-max-width` (760px), `gap-4`. A header row holds `SettingsExit`, an “Exit settings” `MudIconButton` (`ArrowBack`, `aria-keyshortcuts="Escape"`, `[data-settings-exit]`, tooltip “Exit settings (Esc)”), and then the `h1` “Settings” (`MudText` `h5`). Below it the page renders `SettingsSections`, the shared component the settings sheet (F2) reuses, so the two never drift: the HTTPS note, then the account section, `ChannelSettings` and `ConnectionStatusPanel` as separate components, or the signed-out panel. Each section is a `SettingsSection`: an outlined `MudPaper` (`Elevation="0"`) holding a `section` labelled by its `h2` (`MudText` `h6`), titled after an icon: Twitch account `ManageAccounts`, Channels `LiveTv`, Connection status `Wifi`.
- **Exit:** the button and Esc both go back through browser history (`history.back()`) when this circuit has shown an earlier StreamKeepr page, and otherwise navigate to Home (`/`), as after a direct visit, a bookmark or a full-page load (sign-in, Disconnect). The shell's `NavigationHistory` decides: only a change of path counts as a page, so a replaced query (`authError`) or an in-page anchor does not. One exit at a time: a second call while one is under way (a double click, Esc with the button) is ignored until the location changes. Esc comes from `SettingsExit.razor.js`, which also owns the `back()` call; it never fires while typing in a field, with a modifier held, on a key repeat, or while a dialog, menu, overlay or other popover is open (an open tooltip does not block it). The exit button carries `data-sk-ready` once Esc is wired, and leaving the page removes the listener.
- Signed out: a centered panel “Connect Twitch to manage channels” (`[data-settings-signed-out]`) with the sign-in link (return URL `/settings`), the expired chip when the session lapsed, and the HTTPS note; no channel data, no `ChannelsView` calls, no redirect. **Chat** (below) follows the panel: it is the viewer's own preference and needs no sign-in.
- Signed in, the sections are, in this order:
  1. **Twitch account** (`#twitch`): the account's `ChannelAvatar` (its profile image or its login's channel colour, as in the top bar), the display name (`subtitle1`) and login (`caption`), a success dot with “Connected” (`[data-account-connected]`), and an outlined Disconnect `MudButton` that submits the form post, returning to `/settings`. In private mode (ADR-0013) it is labelled “Sign out” and returns to `/signin?returnUrl=%2Fsettings&signedOut=true`; the label and return URL come from `AuthLinks.SignOutLabel`/`SignOutReturnUrl`, which the account menu uses too.
  2. **Channels** (`#channels`, the empty library's “Add a channel” target): watched channels sorted by display name then login, as a list (`ul role="list"`, `[data-channel-list]`) of rows divided by `divider`. Each row has a 40px `ChannelAvatar` (decorative, with the `rec` badge while Recording or Reconnecting), display name, login, a state chip, the push chip, and text `MudButton` actions. The push chip is per channel: `Push` (the success chip, `CheckCircle`) when that channel has both subscriptions, otherwise `Polling` (amber, `Schedule`), with `title=PushReason`.
     - State labels: Recording (“Rec”), Reconnecting (“Reconnecting”), Watching (“Watching”), Paused (“Paused”), CoolingDown (“Cooling down”). Paused channels show no live ring in the library; the group meta says “Paused”.
     - Actions: Stop recording (confirm; finalizes an open capture and parks the channel in Paused), Resume recording (immediate, `Color.Primary`; next observation decides), Remove (confirm). Every action's `aria-label` names the channel.
     - Remove dialog: “Remove {name}? Recordings are kept. An open capture is finalized first.” On confirm, close the dialog, show a small spinner and “Removing…” (`role="status"`, `[data-channel-busy]`, since the rec badge's `MudBadge` is also a `role="status"` region) on the row with `aria-busy`, hide its actions, and await `RemoveAsync` (up to ~35 s). Success removes the row and raises “Channel removed”; failure restores the row and shows the user-safe error. The row's `@key` is the login.
     - Stop confirmation: “Stop recording for {name}? The current capture is finalized, and the channel stays paused until you resume.” For an idle channel the label may read “Pause channel” with equivalent copy. Resume has no confirmation.
     - **Add channel** comes first, directly under the section title and above the list (or the empty “No channels watched.” line), set off from it by a `divider` hairline: an outlined dense `MudTextField` labelled “Add channel” (placeholder “Add a Twitch channel”, no spellcheck, Enter submits, `[data-channel-login]`) and a filled primary “Add” `MudButton` (`Add` icon). Trim and lowercase; disable while empty/busy; show a spinner, “Adding…” and `aria-busy`; on success clear the input, keep focus in it, and raise “Channel added”. Inline error under the field (`role="alert"`, `[data-channel-add-error]`, the field marked invalid) for unknown login, already watched, invalid login, Helix/network failure, or unauthorized/expired session. `ChannelsView.AddAsync` validates through Helix.
  3. **Chat** (`ChatSettings`, `[data-chat-settings]`, design system ChatSettings): the viewer's chat preferences, starting with **Chat sync**, one offset for every recording kept in `UiPreferences.ChatSyncOffset` (cookie `chatSync`). A native `<input type="range">` (`[data-chat-sync-range]`, label "Chat sync") from −30 s to +30 s (`ChatReader.MaxSyncOffsetSeconds`) in 0.5 s steps, drawn like `MudSlider Color.Primary` with a fill from the centre to the thumb, "Earlier" and "Later" at its ends; a `Tune` icon, the readout in `timecode` (`[data-chat-sync-value]`: "+1.5 s", "−3.0 s", "0.0 s" in `text-secondary`, `aria-hidden`), a `RestartAlt` icon button "Reset chat sync to 0" (`[data-chat-sync-reset]`), and one line of help. `aria-valuetext` reads "1.5 seconds later", "3 seconds earlier" or "0 seconds". `input` redraws the readout; `change` (a drag's end, each key press) and Reset save and persist the offset, and an open chat pane applies it at once (player contract §10a). The browser owns the thumb once the slider is shown: the server renders its `value` only when it creates it, because a value echoed back on a slow round trip lands after the next press and undoes it. Reset is the one move the server makes, by rendering a new slider at 0. Keys are the browser's: ←/→ 0.5 s, Home/End ±30 s; focus in the settings sheet keeps them from the player.
  4. **Connection status** (`ConnectionStatusPanel`, issue #148): reads `IConnectionStatusView` (backend-ui contract §3a) and shows, as a dense `MudSimpleTable` of 40px rows (keys are row headers in `caption`/`text-secondary`):
     - **Service health**: the DVR health check's status chip (Healthy = the success chip with `CheckCircle`, Degraded = amber, Unhealthy = red) and its description. It is the same verdict as `/health`.
     - **Transport**: `WebSocket`, `Conduit`, or `Mock`.
     - **Connection** chip: Connected (success; session up, full or partial coverage), Reconnecting (amber), Waiting for admin sign-in (amber; WebSocket mode only), Off (neutral; no channels).
     - **Coverage** chip, only while Connected: “N of M channels”, success when all are covered, amber otherwise. When partial, a dense info `MudAlert` (`Schedule` icon, `role="note"`, `[data-coverage-note]`) says “All channels are polled every {PollIntervalSeconds} until every channel is covered.”
     - **Last message** (any EventSub message, keepalives included), **Last poll** (plus “last successful …” when the last attempt failed), **Twitch API** round trip of the last successful poll, and the current **Poll interval**. These are relative times re-read every 5 s; they never raise `Changed`.
     - Red is reserved for service health; amber means reduced capacity, not an outage; success (`success`, `CheckCircle`) is the Settings-only `Healthy` chip kind.
- No schedule section, and no schedule control in the top bar (removed with the MudBlazor shell, #174).
- The page subscribes to `ChannelsView.Changed` (500 ms throttle) and re-renders rows with `@key`; it disposes the subscription. `ChannelsView.All` and every mutation throw when signed out; the signed-out branch never instantiates the channel component.
- Authorization is enforced by `ChannelsView` and the services as well as `RequiresTwitch`; a disabled control is never the only guard.
- Not in this page yet (card items outside #179): pressing the app bar's Settings button again does not exit, the app bar's Settings button is not marked current, and below `Breakpoint.Md` the page has no dense top bar back arrow; the bottom bar marks Settings current as before.


## 8a. Settings sheet

Added for #183 (design-system card SettingsSheet). Settings opens over the current page, so a playing recording never stops.

- **Where:** `SettingsSheet` (`Components/Features/Settings`) in `AppShell`'s `MudLayout`, with `SettingsSections` passed in by `MainLayout`. Md and up it is an end `MudDrawer` (`Temporary`, `ClipMode.Always`, `Overlay="false"`, 440px, `elevation-24`): an `aside#sk-settings` labelled by its `h2` “Settings”, with a “Close settings” button (tooltip “Close (Esc)” on hover only, since opening focuses it). Clicking outside never closes it, and the player beside it stays usable. Below Md it is an inline full-screen `MudDialog` (`NoHeader`, Esc and backdrop handled by the component, not MudDialog) named “Settings”, with an `ArrowBack` “Close settings” button. Inside the sheet `SettingsSection` (via the `SettingsSheet` cascade) uses an `h3` in `subtitle1` and `divider` rules instead of outlined paper; the channel rows are the page's rows.
- **Now-playing strip (phone, watch pages):** `[data-now-playing]`, a `group` named “Now playing”: the thumbnail with a `primary` progress bar, the title, “streamer · m:ss · playing|paused”, a Pause/Play button (`[data-now-playing-toggle]`) and “Back to the video” (closes the sheet). `SettingsSheet.razor.js` follows the player's `sk:playhead` events, formats time with `/js/timecode.js` (never the player module, which loads only on recording pages) and toggles playback by clicking the player's own Play button.
- **URL:** `?settings=open` (`Ui/SettingsSheetUrl`) on any page but `/settings` shows the sheet; other query keys are kept and a fragment is dropped (as `LibraryUrl` drops it). Opening is a new history entry on the same page (`NavigateTo`, no `replace`), so only the query changes and the page and player stay; Back closes it. Closing an entry this circuit pushed calls `history.back()`; otherwise (a shared or reloaded link) it replaces the URL without the key. A navigation to another page closes it.
- **Opening:** the Settings links stay `<a href="/settings">`. `AppShell.razor.js` claims a plain primary click on a same-origin link to the plain `/settings` (no query, no fragment) from another page in the capture phase: it prevents the default, so Blazor's link interception skips it, and calls `AppShell.ToggleSettingsAsync`. Other click handlers still run (a menu item closes its menu). It is not a .NET location-changing handler, because while one is registered Blazor replays every browser Back through .NET. New tabs, modified clicks, programmatic navigation, browser history and deep links (`/settings#twitch`, `/settings#channels`) still reach the page. The app bar's and bottom bar's Settings carry `aria-expanded` everywhere but `/settings`, and `aria-controls="sk-settings"` while the sheet exists (it is mounted only while open). The app bar's shows a `primary` tint while open; it is a link, so there is no `aria-pressed`. The bottom bar marks Settings `aria-current="page"` while the sheet is open, as it marks Library while the library drawer is open.
- **Focus and keys:** opening moves focus to “Close settings”, also after a breakpoint change swaps the drawer for the dialog while open (a phone reloading `?settings=open`); closing returns it to the opener, else the first visible `[data-settings-toggle]` (the app bar's Settings, or the bottom bar's below Md), else the player (a phone's watch page), when focus was lost with the sheet (focus the viewer moved elsewhere stays). Esc closes the sheet while focus is inside it, except in an editable field, on a repeat, with a modifier, or while another dialog, an open menu or a popover other than a tooltip is open. Player shortcuts are ignored while focus is inside `[data-settings-sheet]` (`docs/player-contract.md` §7).
- **Not in #183:** compact channel rows with a `MoreVert` menu, and “Manage Twitch connection” opening the sheet (it still navigates to `/settings#twitch`).
## 9. Stop and resume surfaces

This contract supersedes the plan's F11-T2 surfaces (E-23):

- Stop/Resume live in the **Settings channel rows**.
- The **details card** offers “Stop recording” in More actions only while the selected recording is Recording/Reconnecting, wrapped in `RequiresTwitch`, with the same confirmation. It calls `IChannelsView.StopAsync(RecordingView.StreamerLogin, ct)`; an unauthorized/expired session shows the reconnect message.
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
- Copy playlist absolute URL/clipboard failure.
- Settings sheet (#183): `SettingsSheetUrl` open/close rules; the shell opens it on the same page, closes by history back or replace, never on `/settings`; drawer Md and up, dialog with the now-playing strip below; toggle `aria-expanded`. Vitest: which link clicks open the sheet, Esc rules, focus in and back, the strip's playhead and toggle, player keys paused inside the sheet.

Integration:

- `FindByRecording` latest/rehydration/cleanup; `ExportService.Start` auth/live/unknown/incompatible; progress event; cancel; download auth/range/404/409.
- `RecordingAdmin.DeleteAsync` auth/Complete/lock/lease; progress cleanup; library event.
- Login local-only return URL; allowlist accept/reject; empty allowlist; HTTPS rejection; callback path; cookie flags; logout antiforgery; non-HTTPS message; forwarded headers.
- `ChannelsView` unauthorized throws; add Helix unknown/known; remove/stop/resume; push reason variants.

Playwright (Chrome channel, Mock profile):

- Sign in and out with the fake admin; expired/not-allowed are fake-state tests.
- Details: chapters seek, copy playlist, export start/progress/download, incompatible failure (fixture), cancel/retry, delete and next-item navigation, lease refusal.
- Settings: add known/unknown, remove with finalize, stop/resume, push summary, empty state, signed-out prompt.
- Settings sheet: at 1280 a playing recording's same `<video>` keeps playing and advancing while the sheet opens, takes K, and closes by Esc and Back; at 375 the dialog opens over a playing recording from the account menu and its strip pauses it; axe on both.
- Auth error snackbar/query removal; HTTPS caveat where testable; axe and keyboard-only.
- No automatic retries; flaky tests are fixed or quarantined per the testing contract.

## 13. Binding versus implementation freedom

Binding: details fields/chapters/collapse, absolute copy, `FindByRecording` and the export row states/rules, always-one-MP4/incompatible handling, delete dialog and failure paths, auth states/redirects/return URLs/antiforgery/expiry, settings structure/auth gating/push summary/channel actions, the settings sheet (§8a: URL key, toggle, focus, Esc, never interrupting playback), stop/resume surfaces (E-23), library Paused meta, Mock auth behavior, and the test matrix.

Freedom: exact panel markup beyond the `sk-` classes, wording of non-binding messages, dialog animation, channel row layout, push reason copy, add-form validation wording, and test implementation.

## 14. Known limitations

- Stop/resume surfaces are Settings plus the details card; the plan's streamer group menu and player title-strip control are absent (E-23).
- An already-produced export file survives recording deletion until the 24 h sweep; deleting the recording does not revoke a downloaded copy.
- Export jobs are in-memory: a host restart loses queued/running jobs (a completed file may remain until cleanup). One process-wide export runs at a time.
- Admin sign-in requires HTTPS except on localhost; with an empty allowlist plain LAN HTTP can view and record but cannot administer, and with a non-empty one it cannot view either (ADR-0013).
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
