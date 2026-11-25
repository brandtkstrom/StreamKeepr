# Stream Keepr Player Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 2, F3/F4/F8; `docs/backend-ui-contract.md`; `docs/frontend-foundation-contract.md`; `docs/library-contract.md`; `docs/hosting-contract.md`; `docs/session-store-contract.md`; `docs/testing-contract.md`; `design-system/components/VideoPlayer`, `StatsPanel`, `ChatPane` and `Keyboard`; plan review E-20/E-21; ADR 0012 and #180 (the JS islands' MudBlazor restyle).
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract covers the recording player, its hls.js engine, control bar and seek bar, live-edge and live-to-VOD behaviour, resume progress, playback preferences, the Statistics overlay and the Viewers panel, the chapter-seek bridge, and the player's error/loading states. The details card, export, delete, and auth UX belong to the next ticket; this contract defines only the `PlayerBridge` they call and the player's reaction to deletion.

The stitched `index.m3u8` is a **single media playlist**, not a multi-variant master: one Streamlink quality is captured, with `#EXT-X-MAP` per part and `#EXT-X-DISCONTINUITY` between parts. The player therefore exposes one source level. Quality switching and a quality preference are deferred (E-20).

## 2. Composition and ownership

- `Home.razor` resolves the selected recording through `ILibraryView.Find` and renders the watch layout in §2a. Md and up the left column is player → title block (`RecordingDetails`) → `ChaptersPanel` → stats, with chat as a full-height column on the right; below Md the player is followed by `RecordingDetailsSummary`, the export row, and the Chat/Chapters/Stats tabs.
- `RecordingDetails` is composed of `RecordingDetailsSummary` (title, meta, actions, stop/delete dialogs) and `RecordingDetailsBody` (export row), on the page ground with no panel. The chapters are `ChapterList`, held by `ChaptersPanel` Md and up and by the Chapters tab below Md; below Md `Home` renders the summary and export row outside the tabs.
- `VideoPlayer.razor` renders the complete player skeleton once: a `<video>` with no native controls, `playsinline`, `preload="metadata"`, and the thumbnail as poster; the title strip, centre play/replay, seek bar, control bar, menus, spinner, and error overlay. A mouse/pen click on the video frame toggles play/pause; controls, links, and menus keep their own click behaviour.
- `VideoPlayer.razor.js` owns hls.js, every player control, all dynamic player DOM, idle state, keyboard handling, progress timing, and error recovery. It calls .NET only for progress, `sk-ui` preferences, and the stats snackbar.
- After the first interactive render, `VideoPlayer` returns `ShouldRender() => false`. Parameter changes call the JS module (`load`/`update`) but never re-render the player markup, so Blazor cannot overwrite JS-owned attributes, classes, text, or SVG children.
- `StatsPanel.razor` likewise renders its skeleton once; `StatsPanel.razor.js` owns the open state, values, sparklines and copy button. `VideoPlayer` renders it inside the player's frame and, because the player never re-renders, passes a changed recording to it with `SetRecordingAsync`. `ViewerChart.razor` (inside `ViewersPanel`) renders the viewer tile and chart once, and `ViewerChart.razor.js` with `viewer-chart.js` owns them. The overlay's open state is the section's `hidden` attribute and `data-open`, from `UiPreferences.StatsOpen`; the player's stats button carries `aria-pressed`. With `Tab="true"` (the phone Stats tab) there is no card header and it is always open.
- `RecordingDetails` remains a normal Blazor component; it composes `RecordingDetailsSummary` (title, meta, actions, dialogs) and `RecordingDetailsBody` (export row). `ChapterList` injects the scoped `PlayerBridge` to seek; none of them manipulates the player DOM.
- **The three JS islands (player, stats, chat) contain no MudBlazor or primitive components** (#180, ADR 0012): their modules own the DOM, so they are plain markup restyled to the design-system cards. MudBlazor components and the shared primitives remain the default for non-island UI.
  - Colours: the player reads only the `player-*` tokens (plus `tertiary` for chapter ticks, `rec`, `warning` and `error`), so it never depends on the page theme. Stats and chat read the palette tokens (`surface`, `drawer-background`, `surface-raised`, `primary`, `secondary`, `tertiary`, `divider`, …) and chat names the `channel-*` tokens.
  - Icons are inline `<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false" data-icon="Name">` holding the MudBlazor `Icons.Material.Rounded.Name` glyph. Razor renders them from the MudBlazor constant (`StreamKeepr.Ui.IslandIcon.Svg`); modules swap them from `wwwroot/js/island-icons.js`, a copy that `IslandIconTests` keeps equal to the constants. `data-icon` names the glyph for tests.
  - Their ARIA and keyboard behaviour follows the foundation contract.

## 2a. Watch layout (#175)

`Home.razor` gives the player the largest area on every watch screen. The width and height rules are layout, not player behaviour; the player's markup and `data-sk` hooks are unchanged. The only player addition is the `theaterAvailable` payload flag and its `setTheaterAvailable` call (design system: AppShell, TheaterMode, PhoneLayout, VideoPlayer).

- **Md and up.** The watch page has no max width and is two columns (a CSS grid; the top row dissolves so the player and chat are grid items). The left column is the player, then `RecordingDetails` (the title block), `ChaptersPanel` (only with chapters), then `ViewersPanel` (only with viewer samples), `space-4` apart; Statistics is an overlay inside the player (§10). The player is as wide as its column at 16:9, and its height is capped at `100dvh − appbar-height − 2 × space-6 − player-peek` (the main column's 24 px block gutters), so the title block's first line shows without scrolling; when the cap applies the video letterboxes inside the black frame. **Chat**, when the recording has chat and the pane is open, is the right column: `clamp(chat-width, 28vw, 400px)` wide, spanning every row, `position: sticky` at the top of the main column and `100dvh − appbar-height − 2 × space-6` tall, so it stays put while the left column scrolls and nothing else sits beside it. Closed, it is not shown and the left column takes the full width.
- **Theater.** The shell hides the drawer and the main column's gutters. The player spans the full width of the view, edge to edge with square corners, at `min(16:9 width, 100dvh − appbar-height)`. The same two columns follow below it with `space-4` gutters: the title block, Chapters and stats on the left, and the open chat pane on the right, sticky and at least 260px tall (the view's height less the app bar and gutters). The chat never sits beside the player in theater. `T` and the player's theater button keep working. Theater is desktop-only: below Md it is inert, but the saved preference is kept for the next desktop view.
- **Below Md.** The player is edge to edge with square corners; on Xs (below 600px) it is `position: sticky` under the app bar so it stays visible while the rest of the page scrolls. `RecordingDetailsSummary` renders the title (h6), meta line and actions directly below it, outside any panel, followed by the export row (`RecordingDetailsBody`). A `MudTabs` with `Color.Primary` and `KeepPanelsAlive` holds **Chat** (only when the recording has chat; first and selected when present), **Chapters** (only when the recording has chapters; `ChapterList`) and **Stats**; the first tab present is selected. the Stats tab is `StatsPanel` with `Tab="true"` (no card header, always open) followed by `ViewersPanel` with `Tab="true"` (the tile and chart, no header); the Chat tab is `ChatPane` with `HideHeader="true"` and is always open. Theater is inert below Md (the player's `theaterAvailable` is false) and the player's Chat and Stats buttons are hidden because the tabs own those panels. On a phone in landscape (below Md and 500px height) the top bar hides and the player fills the screen.
- The player stays the first child of the watch top row at every width, so crossing `Breakpoint.Md` does not remount it. The chat pane and the tab panels may remount when the breakpoint changes; chat catch-up follows the player's `sk:playhead` events.

## 3. Engine and source lifecycle

- On the first interactive render, `VideoPlayer.razor.js` is imported; it dynamically imports the vendored hls.js from `wwwroot/lib/hls.js/hls.mjs` only when `Hls.isSupported()`. No hls.js request occurs on `/` or before a recording page.
- If hls.js is unsupported but the browser reports native HLS support, the module sets `video.src` and uses the native pipeline. If neither is supported, the error overlay explains that the browser cannot play HLS; there is no retry.
- `load(view)` runs when the recording id changes (including `Home` navigating to the next recording after a delete):
  1. Flush the previous recording's progress when eligible.
  2. Destroy the previous hls instance and abort its listeners/timers.
  3. Reset media, error, idle, stats, seek, and menu state.
  4. Apply volume, mute, and speed preferences.
  5. Create hls.js or set the native source; attach the new poster, title, streamer login, chapters, and state.
  6. Restore the resume position when the recording has one; otherwise open at the live edge for Recording/Reconnecting, or at 0 for Completed. When the open carries the one-shot autoplay intent (§4), start playing after that position is applied.
- `update(view)` runs for the same recording when `LibraryView.Changed` supplies a new `RecordingView`. It updates the title, streamer login, poster, chapters, and state chip. It never applies `ResumeSec` again and never resets playback. The component skips `update` when the `RecordingView` instance is unchanged.
- The init/update payload uses camelCase:
  - `recordingId`, `playlistUrl`, `title`, `streamerLogin`, `thumbnailUrl`
  - `state`: `recording`, `reconnecting`, `partial`, `saved`
  - `durationSec`, `resumeSec`, `autoplay`, `chapters[]`, `statsOpen`, `theater`, `theaterAvailable`
  - `chapters[]` is `{ startSeconds, title, game }` from `RecordingView.Chapters`.
  - `autoplay` is true only for a library selection; `update` payloads carry it but never apply it.
- `dispose()` flushes progress, destroys hls/native media, clears timers and listeners, and releases the module reference. The `DotNetObjectReference` is disposed by the component.
- The .NET reference exposes `SaveProgress(string recordingId, double)`, `ClearProgress(string recordingId)`, `SetStatsOpen(bool)`, `SetTheater(bool)`, and `Notify(string)`. The progress calls carry the recording id so a save in flight across a recording change lands on the recording it belongs to. `Notify` shows a snackbar (`ISnackbar.Show`); it is used only for copy-stats confirmation/failure.
- `UiPreferences` is the single source for stats/theater state. JS applies `setStatsOpen(bool)`/`setTheater(bool)` when `UiPreferences.Changed` fires; the player's own buttons call .NET to persist.
- The chat pane announces itself on `document` with `sk:chat` (`detail: { available, open }`) whenever it starts, opens or closes (`available: true`), and when it is disposed (`available: false`). The player's Chat button asks it to toggle with a detail-less `sk:chat-toggle` on `document`, the same request the C key makes; the pane stays the one owner of the open state and its persistence (`SetChatOpen`). At init the player also reads an already-rendered `[data-sk-chat]` and its `data-open`, so the order in which the two modules start does not matter.
- The player dispatches a throttled `sk:playhead` DOM event (at most every 250 ms on `timeupdate`, and immediately after play, pause, seek, rate change, end, loaded metadata, and duration change) with `recordingId`, `time`, `duration`, `paused`, `rate`, and `live`. The chat pane listens on `document`; the event never crosses the Blazor circuit.

## 4. Playback model

- **Autoplay only on a library selection.** Selecting a recording from the library drawer or a Home row opens the watch page playing. Selecting the recording that is already open starts it playing from its current position without reloading the source. The player positions the playhead first (the resume position when there is one, otherwise the live edge for Recording/Reconnecting, otherwise 0 for Completed), then calls `video.play()` with the saved volume, mute and speed preferences. A direct `/r/{id}` link, a reload, and back/forward show the poster and centre play button and wait for Play. A chapter seek never starts playback. If `video.play()` rejects (for example an audible-autoplay policy), the player stays paused with the Play icon/`aria-label` and centre Play, shows no error overlay, and does not retry.
- hls.js configuration is the defaults plus:
  - `enableWorker: true`
  - `lowLatencyMode: false`
  - `backBufferLength: 90`
  - `liveSyncDurationCount: 1`
  - `liveMaxLatencyDurationCount: Infinity`
  - `maxLiveSyncPlaybackRate: 1`
  - `maxBufferLength: 60`
  - `playlistLoadPolicy.default` with `maxTimeToFirstByteMs: 30000` and `maxLoadTimeMs: 30000`
- The playlist policy repeats hls.js's default retries (`timeoutRetry` 2/0/0, `errorRetry` 2/1000/8000) because hls.js replaces the policy object rather than merging it. `maxMaxBufferLength`, `maxBufferSize`, and the deprecated per-type `*LoadingTimeOut` keys stay at hls.js defaults.
- The playlist target duration is 12 s, so the live sync position is about one target duration behind the true end. This is the player's **live edge** and avoids a guaranteed stall at the last segment.
- The 60 s forward buffer keeps playback running while a slow live playlist is fetched; it does not move the live edge.
- The red live tick marks the seekable end (`seekable.end`), not the sync position. The **Live edge** button seeks to `hls.liveSyncPosition` and enables following.
- Following: while Recording/Reconnecting, playing, and following is on, the player keeps the playhead within roughly one target duration of the sync position; if it falls more than 2 s behind `liveSyncPosition`, it seeks forward to it. A manual seek more than 2 s behind the sync position, or a pause, turns following off. Pressing **Live edge**, seeking to within 2 s of the sync position, or seeking to the red tick (within 2 s of `seekable.end`) turns it on; a seek to the true end snaps back to `liveSyncPosition` so playback starts buffered. Each such return by the viewer clears the recording's resume position, once per return; opening at the live edge does not, and staying at the edge does not keep clearing, so a position another viewer saves meanwhile survives until someone next returns.
- A Recording/Reconnecting recording without a resume position opens at `hls.liveSyncPosition` (or `seekable.end - 1`, clamped, before hls.js reports a position), following. With a resume position it opens there, paused and not following; **Live edge** is the viewer's to press.
- **Live to VOD:** when hls.js loads a playlist with `details.live === false` (ENDLIST), the player hides the live tick and button, stops following, updates the duration to the finite media duration, keeps the current position, and applies the 30 s end margin to progress writes from then on. When the .NET state also becomes Partial/Saved, the chip updates; the two paths are idempotent.
- **VOD to live:** a finalized session can reopen as a new part (Saved/Partial → Recording/Reconnecting). The player reloads the same playlist at the current position, preserving the paused/playing state; it does not jump to the live edge, and progress keeps saving because the viewer is not following. The user can press **Live edge** afterwards.
- **Deleted recording:** `Home` owns the reaction. When `Find(id)` becomes null it stops/removes the player, shows a “Recording deleted” snackbar, and navigates to the next recording in the current query (the first remaining item across streamers), or `/` when none. The player's flush may 404 and is ignored. An unknown id on first load keeps the foundation's message and back link.
- Playback continues across part gaps and discontinuities; the stitched playlist supplies the correct init segments. `waiting` shows the spinner, not an error.

## 5. Control bar and seek bar

Control order, left to right, matches the design system:

1. Play/pause
2. Back 10 s
3. Forward 10 s
4. Mute + volume slider (slider revealed on hover/focus-within)
5. Current / total timecode
6. Live edge (Recording/Reconnecting only; dot plus label, label hidden at narrow widths)
7. Spacer
8. Speed menu
9. Static “Source” quality pill (E-20)
10. Stats toggle
11. Chat toggle (only while a chat pane exists; mirrors C)
12. Picture-in-picture (hidden when unsupported)
13. Theater
14. Fullscreen

- Controls are 40 px (`player-button`) round buttons in `player-text` on the flat `player-scrim`; pressed toggles (stats, chat, theater) take `player-accent`. Play/pause updates the icon and `aria-label`; the centre button shows `PlayArrow` when paused and `Replay` when ended. Mute shows `VolumeUp`/`VolumeOff`; fullscreen `Fullscreen`/`FullscreenExit`; picture-in-picture `PictureInPictureAlt`, then `PictureInPicture` while in PiP (Material Rounded has no "picture in picture off" glyph). Starting from the centre button moves focus to the control-bar play button. Replay starts at 0 and clears progress.
- Skip buttons clamp to `[0, duration]` and disable when no metadata is available.
- Timecode uses the JS twin of `Format.Timecode`; total grows while live and settles on ENDLIST.
- The seek bar is `role="slider"`, `tabindex="0"`, with `aria-valuemin`, `aria-valuemax`, `aria-valuenow`, and `aria-valuetext` (`1:02:13 of 3:41:12`). It is disabled until metadata exists.
- Pointer drag previews the position and commits a single seek on `pointerup`; a click seeks directly. The 4 px track is 24 % `player-text`; buffered ranges use `player-buffered`, played and the 14 px knob `player-accent`, chapter ticks `tertiary` (the stream's colour, as in Statistics) and the live-edge tick `rec`. The knob appears on hover/drag; the hover time is `timecode` on `player-surface`; touch uses `touch-action: none`.
- Hover shows a timecode tooltip plus the chapter title when one applies. Chapter ticks come from `RecordingView.Chapters`; invalid or out-of-range chapters are ignored.
- Keyboard on the seek bar: ←/→ ±5 s, Home/End to start/end; the player's global handler yields to the slider.
- Volume is a mute button plus a slider (`role="slider"`, 0–100, arrows ±5). It unmutes when raised above 0. At ≤520 px container width the whole volume group is hidden; the OS volume remains.
- Speed options follow the design preview: 0.5×, 1×, 1.5×, 2×; the pill shows the current value and the menu checks the active item. The menu takes a `MudMenu`'s shape (`radius-md`, `elevation-8`) on `player-surface` with `player-text`; every item carries the `Check` glyph in `player-accent`, shown only on the checked item.
- The quality pill is static text (“Source”) and is not a button; there is no quality menu or preference (E-20). It hides at ≤520 px.
- The stats toggle and the panel switch share one state; both use `aria-pressed`/`aria-checked`.
- The Chat toggle (`data-sk="chat"`, `aria-label="Chat"`, `aria-keyshortcuts="C"`, `aria-pressed`) is hidden until a chat pane announces itself through `sk:chat`, follows the pane's open state, and on click dispatches `sk:chat-toggle` (§3). It sits beside the stats toggle and is hidden again when the pane is disposed. The pane is outside `.sk-player`, so in fullscreen the button still toggles it, out of view, like C.
- Picture-in-picture is button-only. It is hidden when `document.pictureInPictureEnabled` is false or the API is absent, disabled while fullscreen, and its icon/aria follows enter/leave events. No custom controls exist inside the PiP window.
- Fullscreen targets the `.sk-player` container so custom controls remain. `:fullscreen` fills the viewport, contains the video, and drops the radius. If container fullscreen is unavailable, fall back to `video.requestFullscreen()`; native Safari may use its own controls. Entering fullscreen focuses the container; exiting returns focus to the fullscreen button. The chat sidecar is outside `.sk-player`, so it is not part of fullscreen and remains in its previous state when fullscreen exits.
- Theater calls `UiPreferences.SetTheater(!Theater)` and updates its pressed state through `Changed`; the shell owns hiding the drawer and the background change. Theater does not resize the player beyond the shell.
- Responsive control hiding uses the existing container queries: ≤720 px hides skip, PiP, and speed; ≤520 px hides theater, volume, the quality pill, and the live-edge label. Below Md the watch layout also hides the player's Chat and Stats toggles because the tabs own those panels, and the theater toggle because theater has no compact form. Keyboard shortcuts remain available, except T below Md.
- The status chip is the player's own (the module writes it): StatusChip's words, `data-status`/`data-status-icon` hooks and icons (`Sync` for Reconnecting, `Warning` for Partial, in `warning`) in overline type; REC is a filled `rec` chip with `rec-contrast-text`. Loading is a spinner in `player-accent`; the error overlay shows `Error` in `error`, a filled Retry (`player-accent`) and a text "Back to library".

## 6. Idle, overlays, and focus

- The title strip, centre control, and control bar fade after 3 s without pointer movement while playing. Idle is not applied while paused, while focus is inside the player, or while a menu is open; the cursor hides with the overlays.
- `pointermove`, `pointerdown`, `keydown`, and `focusin` reset the timer and reveal the overlays. A mouse/pen click on the video frame toggles play/pause; a touch tap only reveals the controls. The centre/control play button remains the keyboard and accessible play surface, and clicks on controls never bubble into a frame toggle.
- Reduced motion removes the fade transitions but not the hide behaviour.
- Focus order follows the DOM: centre play (when visible), seek bar, play, skip, volume, live edge, speed, stats, chat, PiP, theater, fullscreen. Every keyboard focus inside the player shows a solid 2 px `player-accent` ring with a 2 px offset (Keyboard card). Menus return focus to their trigger. The error overlay focuses Retry; the player regains focus after a successful retry.
- The status chip (`Rec`, `Reconnecting`, `Partial`) is a polite live region; timecode and stats values are not. Errors use `role="alert"`.

## 7. Keyboard

- The player listens on `document` while a recording is active. It ignores events with modifiers or `defaultPrevented`, editable targets (`input`, `textarea`, `select`, `[contenteditable]`), open menus/dialogs, keys handled by the seek/volume slider, and any target inside `[data-sk-chat]` or the settings sheet (`[data-settings-sheet]`, #183). The desktop sheet has no overlay, so with focus on the player beside it the keys still work.
- Space/K toggle play/pause. They do nothing when the target is a button or link, so native activation wins.
- J/L seek ±10 s; ←/→ seek ±5 s, except inside the drawer (tree keys win) or on a slider (the slider handles them).
- M toggles mute; F toggles container fullscreen; T toggles theater on Md and up (ignored below Md); I toggles stats; C toggles the chat replay sidecar when the recording has chat, as does the player's Chat button. C is ignored in editable fields and open menus/dialogs.
- `/` and Esc belong to `shortcuts.js`; the player never handles them.
- Menu keys: ↑/↓/Home/End move, Enter/Space choose, Esc/outside click close, Tab closes and continues; `role="menu"` with `menuitemradio` options and `aria-checked`.

## 8. Resume and progress

- Every recording starts at `ResumeSec` when it is set, clamped to `[0, duration - 1]`; there is no resume prompt, toast, or “start over” control. A null `ResumeSec` starts a Completed recording (Saved/Partial) at 0 and a Recording/Reconnecting one at the live edge. A library selection starts playing from that position (§4); a direct link or reload stays paused there.
- Recording/Reconnecting write progress only while the viewer is not following: a pause or a seek behind the live edge starts writing, with the same cadence and flushes as below. Opening at the live edge, whether it starts playing or stays paused, is following and writes nothing; playing at the edge also writes nothing. While live the 30 s end margin is suspended, on the player and the server, because the live edge sits inside it; once ENDLIST makes the duration finite the margin applies again. When the capture finishes, the backend drops a resume position left within 30 s of the final end, so the finished recording opens at 0.
- Save every 15 s while playing and once on pause through the component's `SaveProgress` call to `IProgressStore.SetAsync`; this keeps the library's resume bars updating through `LibraryView.Changed`.
- Flush on route change, component dispose, `pagehide`, and `visibilitychange` → hidden with `PUT /api/recordings/{id}/progress`, `Content-Type: application/json`, `keepalive: true`. Skip a flush within 2 s of the previous write.
- On `ended`, call `ClearProgress`. Within 30 s of the end, clear instead of writing a position. While the duration is unknown (still loading, or a pause fired while switching recordings), write nothing: clearing then would wipe a stored resume position the viewer never moved past. The backend clamps and enforces the same 30 s rule.
- No timeupdate or other high-frequency event crosses the circuit. No cross-tab progress sync; the last write wins.
- The `RecordingView.ResumeSec` used at load is never re-applied after a parameter update.

## 9. Playback preferences

- Stored as one versioned JSON object in `localStorage` under `sk-player`:
  ```json
  {"v":1,"volume":1,"muted":false,"speed":1}
  ```
- Defaults: volume 1, muted false, speed 1. Volume clamps to `[0,1]`; speed must be one of 0.5, 1, 1.5, or 2; invalid values fall back. Unknown fields are ignored.
- Read once at module init and applied on every `load`. Writes happen on change, not on timeupdate. A malformed value, disabled storage, or quota error falls back to in-memory defaults and never blocks playback.
- Layout state (`sk-ui`), viewer time zone (`sk-tz`), and player preferences (`sk-player`) stay separate. Player preferences are not cookies and are not sent to the server.
- No quality preference; E-20 defers it with the quality menu. No cross-tab sync.

## 10. Statistics and Viewers

- **Statistics** (renamed from "Stats for nerds"; design system `StatsPanel`). Md and up it is a card laid over the top-left of the video, rendered inside the player's frame (the element that goes fullscreen) after the title strip and before the control bar, so it shows in theater and fullscreen. It is a `section` named by its `h2` "Statistics" (`subtitle2`) in a 40 px header with two 32 px buttons: Copy stats (`ContentCopy`) and Close (`Close`, "Close statistics"). The card is at most 420 px wide, `player-surface` at 68 % with an 8 px backdrop blur, `radius-md`, `elevation-8`, text in `player-text`, `z-index` above the title strip and controls and below the speed menu. While the overlays show it sits between the title strip and the seek bar; idle, it moves to `space-3` from the top and may grow to `space-3` from the bottom (200 ms, none under reduced motion). Its body scrolls and is focusable (`tabindex="0"`) so the keyboard can scroll it. The pointer on the card neither wakes the overlays nor lets them go idle, because either would move the card out from under the pointer. In a player narrower than 560 px the card is half the width (at least 220 px) without sparklines. Below Md it is not shown; the Stats tab is used instead.
- Sections are headed in `overline` after an 8 px swatch in the series colour (`data-series`): **Video** (`primary`), **Network** (`secondary`) and, in the overlay only, **Stream** (`tertiary`, `data-sk="stats-stream"`): one row, "Viewers at ‹timecode›" (`stream-at`) and "‹count› · peak ‹peak›" (`stream-viewers`). The Stream row is `hidden` until `ViewersPanel` has samples for this recording; it reads them from `viewer-store.js` and follows `sk:playhead`.
- **Viewers** (design system `Viewers`). `ViewersPanel` is an outlined disclosure under Chapters (Md and up): a real button (`Groups`, "Viewers" in `h6` as the `h2`, a `caption` hint "1,489 at 54:40 · peak 1,658" that `ViewerChart.razor.js` writes, the chevron), open by default and remembered in `UiPreferences.ViewersOpen` (`viewers` in `sk-ui`). Its body is the viewer tile beside the chart. The panel is `hidden` until the chart island reports samples (`[JSInvokable] ViewerChart.SetHasSamples`); a recording without samples shows no panel. On a phone the same chart, without the header, ends the Stats tab.
- Viewer data (#186): `GET /api/recordings/{id}/viewers` is public like chat and media, sends `Cache-Control: no-store`, and returns 404 for an unknown recording. Body: `{"v":1,"recordingId":"…","durationSec":8047,"samples":[{"at":0,"count":1658}, …]}`. `at` is seconds from the recording start (0.1 s), so it shares the recording's timeline; `durationSec` is whole seconds (rounded to the nearest second): the time since the start while live and the final duration once complete. Samples are oldest first, at most one per 60 s while recording (denser in Mock seed data), with gaps where Twitch reported no count; a recording without samples returns `samples: []`. Malformed stored lines are skipped server-side. At most 50,000 samples. Storage is session-store contract §7.2.
- **Viewer tile and chart** (#211). `ViewerChart.razor.js` fetches `GET /api/recordings/{id}/viewers` once per recording; while the recording is live (Recording or Reconnecting) it refetches every 60 s, and a capture that finishes gets one last fetch. Each load is published to `viewer-store.js` for the Statistics overlay, which keeps only the latest recording's samples. Disposing aborts the request and stops the refresh; a recording change aborts the old request and resets the chart; a response is applied only if it is the latest request for the current recording and its `recordingId` matches. A 404 is no samples; any other failure is logged and keeps what is shown.
  - The tile reads "Viewers at ‹timecode›" in `overline` (`viewer-at`), the count at or immediately before the playhead in `stat-hero` (`viewer-count`, `—` before the first sample), then **Now** (latest sample, `viewer-now`), **Peak** ("1,658 at 38:00": the first maximum and its time, `viewer-peak`) and **Average** (rounded mean, `viewer-average`) as key/value rows. The playhead follows the player's `sk:playhead` events for this recording.
  - The chart (`data-sk="viewer-chart"`) is `role="img"` named by one sentence ("Viewer count over the recording: from 380 at the start to a peak of 1,658 at 38:00, 1,492 now." — "at the end" once finished). x is recording time from 0:00 to `durationSec` (or the last sample, if later), so it grows with each live refresh; y runs from 0 to the first 1/2/5 × 10ⁿ at or above the peak (at least 10). The SVG (`viewer-svg`, `aria-hidden`) draws one `tertiary` 2 px line (`data-series="line"`) over an 18 % `tertiary` area (`data-series="area"`) and three `divider` gridlines (`data-gridline`: 0, half, max) labelled in `caption` with compact numbers (0 · 1k · 2k); the x axis has four or five timecode labels (`data-axis="x"`, fewer only for a recording a few seconds long) ending at the duration. A dashed `primary` playhead line carries a knob on the series and a `primary` timecode tag; while live a `rec` dot marks the last sample.
  - Hover shows the nearest sample in a tooltip (`tip-viewers`, "38:00 · 1,658 viewers") and a crosshair; a live refresh repositions an active tooltip rather than leaving it stale. While pressed the chart carries `data-active`, so the tooltip also shows on touch. Pressing or dragging seeks: the module calls `[JSInvokable] ViewerChart.Seek(seconds)`, which calls `PlayerBridge.SeekAsync`; while a drag is in flight only the latest position is kept, so at most one seek call is outstanding. The chart is pointer-only (`role="img"`, not focusable); keyboard users seek with the player's seek bar and shortcuts, and read the samples in the table.
  - The samples are also a visually hidden table beside (not inside) the image (`table-viewers`, `rows-viewers`: Time, Viewers). In the phone Stats tab the tile stacks above the full-width chart.
- Video tiles: **Bitrate** (Mb/s) and **Frame rate** (fps). Network tiles: **Bandwidth** (Mb/s) and **Buffer** (s). Video sparklines are `primary` and network sparklines `secondary`. Each has a 20-sample, 2 px sparkline with a hover crosshair and a value tooltip; an active tooltip is repositioned rather than blanked when a sample redraws the sparkline.
- Video key/values: resolution (encoded → viewport when different), codecs, dropped frames (`dropped of total`), decoder (`MSE · hls.js` or `Native HLS`).
- Network key/values: last segment (`#index · duration · load time`), segments loaded, errors/retries (⚠ prefix and `error` text when non-zero), playlist URL (ellipsised, full value in `title`).
- Sampling runs every 2 s while the overlay is open (always, in the Stats tab) into a 20-sample ring. The timer stops when it closes; segment/error counters continue to accumulate. Opening resets the ring and starts a fresh window. The panel can sample while paused; values stay flat.
- Frame rate uses `level.attrs['FRAME-RATE']` when present; otherwise estimate with `requestVideoFrameCallback` over the open panel's sample window. The callback loop starts when the panel opens, resets the estimate at that point, and stops when the panel collapses or disposes. Bitrate uses the level bitrate, then measured `FRAG_LOADED` bytes/duration; otherwise `—`. Bandwidth comes from `hls.bandwidthEstimate`; native mode shows `—` for hls-only values. Resolution uses `video.videoWidth/Height` for the viewport and the level for the encoded size.
- The SVG is `aria-hidden`; each tile keeps a visually hidden table of the samples for screen readers. Values are not a live region. Problems are text plus ⚠, never colour alone.
- The player's stats button ("Statistics", `aria-keyshortcuts="I"`) is owned by `VideoPlayer.razor.js`: it updates its own pressed state immediately, announces `sk:stats-open` so `StatsPanel.razor.js` shows or hides the card and starts or stops the sampler before the circuit round-trip, and calls `SetStatsOpen` to persist. The card's Close button hides it, moves focus to the player's stats button, announces `sk:stats-state` so the button's pressed state follows, and calls `SetStatsOpen`. Both share `UiPreferences.StatsOpen`. There is no URL query key. Reload restores the state from `sk-ui` with no flash.
- “Copy stats” is the card header's icon button, and in the Stats tab a plain `<button>` drawn as a `MudButton Variant.Text` in `primary` under the sections. It writes a v1 JSON snapshot to the clipboard:
  ```json
  {
    "v": 1,
    "recordingId": "…",
    "capturedAt": "2026-11-10T18:00:00.000Z",
    "mode": "live|vod|native",
    "playlistUrl": "/media/…/index.m3u8",
    "level": {"index":0,"width":1920,"height":1080,"frameRate":59.94,"bitrate":6200000,"videoCodec":"…","audioCodec":"…"},
    "samples": [{"at":0,"bitrateMbps":6.2,"frameRate":59.94,"bandwidthMbps":48.6,"bufferSec":28.4,"droppedFrames":3}],
    "errors": [{"at":0,"type":"networkError","details":"fragLoadError","fatal":false}],
    "viewers": {"now":1492,"peak":1658,"peakAt":2280,"average":1288}
  }
  ```
  Samples are the last 20; errors the last 10; unavailable values are `null`. `viewers` (#211) carries the viewer figures for the current recording (from the samples the Viewers panel loaded), `peakAt` in recording seconds; all four are `null` without samples. The version stays 1: the field is additive. The clipboard call uses only public playback data. Success shows “Stats copied”; failure shows “Could not copy stats” as a snackbar (`ISnackbar.Show`). No legacy clipboard fallback.
- The overlay is one column; the Stats tab stacks its sections and tiles.

## 10a. Chat replay pane (island restyle, #180)

The pane's windows, polling, backfill, and emotes are unchanged; this section fixes its look and the parts the player relies on, plus the replay reset and footer below.

- **Replay reset (`#289`, `#279`).** On a paused → playing transition in `sk:playhead` (the `paused` detail, §3), the pane follows the playhead again: `following` is set, the unseen count is cleared, and the log lands on the current chat instantly, without animating through scrollback. A playhead move the pane's existing seek thresholds classify as a seek does the same; a forward seek within the kept window lands instantly, while a backward or far-forward seek keeps its fresh-window fetch. A pause by itself, a rate change, and ordinary playhead ticks do not reattach, and a move within the seek threshold leaves the log alone; a qualifying seek while paused lands on the seeked chat, as the window rules above describe. Opening a closed pane reattaches it to the current chat even when playback advanced less than the forward window while it was closed.
- **Footer and notice.** While the viewer is at the current position there is no footer element at all: no container, border, padding, or reserved height, and the log reaches the bottom of the pane. Scrolling away renders the footer with the centered "Jump to current time" pill, including when no new message arrived; the `(N)` count appears when it is greater than zero. It is not shown for an unavailable or not-yet-captured chat. The load-error notice is a separate row between the log and the footer, hidden when empty, so an error never makes the footer appear; it stays visible below `Md`, where the header is not rendered.

- Drawn like an outlined `MudPaper`: `surface`, `radius-lg`, a `lines-default` outline. Open on desktop it is `flex: 0 1 400px` with a 320 px minimum beside the player; closed it is not shown at all. It has no toggle of its own: the player's Chat button (§5) and C open and close it. With `HideHeader="true"` (the phone Chat tab) it renders no header, is always open and fills the tab. The header is a plain title (the decorative `Chat` icon and "Chat" in `subtitle1`) and the state in `caption` (`error` when unavailable). The log is `body2`; names are 700 weight; replies are quoted in `caption` behind a `divider` rule. Message and reply text render absolute `http(s)` URLs as links: the visible text stays as typed minus trailing sentence punctuation and an unmatched closing bracket, the anchor opens in a new tab (`rel="noopener noreferrer nofollow"`) in `primary` with an underline, and only explicit `http`/`https` schemes link (other schemes, `www.` hosts, and bare domains stay text). The pane never parses message text as HTML.
- **Chat sync offset.** The pane has no sync control of its own. The offset is one app-wide viewer preference set in Settings › Chat (`ChatSettings`, details-auth-settings contract §8) and kept in `UiPreferences` (`chatSync`). The init/load payload carries it as `syncOffsetSec`; the module clamps it to ±`maxSyncOffsetSec` and sends it with every fetch. When the preference changes, `ChatPane` calls the module's `setSyncOffset(root, offset)`, which refetches a fresh window around the playhead (a closed pane fetches when it next opens); the same offset again fetches nothing. The module never stores the offset; the per-recording `sk-chat` localStorage offsets an earlier version wrote are ignored.
- **Badges.** The init/load payload carries `badgesEnabled` and the viewer's `showBadges` (`UiPreferences.ChatShowBadges`, cookie key `chatShowBadgeText`, on by default). When enabled, the module fetches `/api/recordings/{id}/badges` once per recording load on its first open, in parallel with the emotes, and re-renders when the catalog arrives. A message badge whose set and version match renders an 18 px-high `<img>` in the chip's slot with `srcset`, `loading="lazy"`, `decoding="async"`, `referrerpolicy="no-referrer"`, and the catalog title as `alt` and `title`; an unknown set or version keeps the text chip, and an image error swaps that badge to its chip once with no retry. The module writes `showBadges` to the root's `data-badges`; the pane rule hides both `.sk-chat-badge` and `.sk-chat-badge-img` with `display: none`. When the preference changes, `ChatPane` calls the module's `setShowBadges(root, value)`, which only flips the attribute: no refetch and no log rebuild, so the viewer keeps their place.
- **Channel colour.** The init/load payload carries `channelLogin` (`RecordingView.StreamerLogin`) and `channelColor`, the 1–8 channel colour from `ChannelColor.For` (FNV-1a). The module never hashes logins itself. A message whose login equals `channelLogin` (case-insensitive) names its author in `var(--channel-N)` and carries `data-channel-color="N"` on its `[data-chat-name]`, the same number the drawer's avatar carries. With `fixedOpen` (the phone Chat tab) the pane ignores `sk:chat-toggle` and `C`, so the tab stays open.
- **Other chatters** keep their Twitch colour only where it reaches 4.5:1 on the pane's background; otherwise the name has no inline colour and keeps `text-primary`.
- Every keyboard focus in the pane shows the 2 px `primary` ring.

## 11. Chapter seek and play

- A scoped `PlayerBridge` is the only seek/play seam for sibling components:
  ```csharp
  public sealed class PlayerBridge
  {
      public bool IsReady { get; }
      public void Attach(Func<double, ValueTask> seek, Func<ValueTask>? play = null);
      public void Detach();
      public ValueTask SeekAsync(double seconds);
      public ValueTask PlayAsync();
  }
  ```
- A chapter seek only moves the playhead; it never starts playback.
- `VideoPlayer` attaches after JS init and detaches on dispose. `SeekAsync` clamps the lower bound to `0`; the player JS clamps the upper bound to the media duration it owns, so the bridge does not carry a second duration copy. If the player is not ready it stores the latest request and flushes it after attach. A click on a chapter in `RecordingDetails` calls it and leaves focus on the chapter button.
- `Home` uses `PlayAsync` when a library selection repeats the already-open recording: there is no new load payload, so the attached player starts from its current position (or replays from 0 when ended) without reloading the source. A play request that arrives before attach is kept and flushed, like a seek.
- Chapter ticks, hover chapter names, and the details list all use `Chapter.StartSeconds`; empty or invalid chapter data produces no ticks and no tooltip title.

## 12. Media URLs, caching, and security

- `RecordingView.PlaylistUrl` is the relative `/media/{channel}/{id}/index.m3u8`; the player resolves it against `document.baseURI` and never constructs or rewrites media paths. Segments resolve relative to the playlist.
- Media is public and same-origin: no auth header, cookie requirement, token query, CORS, or `crossorigin` attribute. The hosting allowlist is the only gate.
- Playlists are served `no-cache`; segments/init files/thumbnails are immutable. The player relies on hls.js refreshing the live playlist and must not append cache-busting query strings.
- Titles, chapter names, error text, and playlist URLs are written with `textContent`/attributes, never `innerHTML`. The `PlaylistUrl` is used only for the media source and `title`.
- No secrets, tokens, or personal data enter `localStorage`, copied stats, logs, or the DOM.

## 13. Loading, waiting, and errors

- On load the poster shows; `aria-busy` is set until metadata and the first buffered range exist. A spinner appears after 500 ms of loading, with a visually hidden “Loading video”.
- **First load** (no live playlist with segments has loaded yet): while Recording/Reconnecting and the playlist is missing, empty, or fails to load, parse, or time out, show “Waiting for the first segment…” with the spinner and retry the manifest every 3 s. This is not an error and has no timeout. It becomes an error only if the session is Complete before any segment arrives.
- hls.js allows 30 s to first byte and 30 s total per live playlist request (raised from its 10 s/20 s defaults). It still retries at the defaults: two timeout retries at 0 ms and two error retries at 1 s/8 s. The 60 s forward buffer keeps playback running while a slow playlist is fetched; the fatal-error retries below are separate.
- **Mid-stream playlist refresh failure:** after a live playlist with fragments has loaded, a live playlist load, parse, or timeout error is not first-load waiting. Show “Reconnecting…”; keep the source and buffered playback, and never show the overlay while media remains. The capture chip reads Reconnecting while the playlist is unhealthy; the spinner appears only when the buffered range at the playhead is 0.5 s or less. A successful `LEVEL_LOADED` with fragments or `FRAG_LOADED` clears the reconnect status and restores the normal capture chip. An empty mid-stream playlist does not return to the first-segment state.
- `video.waiting` spins only when metadata is absent or the buffered range at the playhead is 0.5 s or less; a seek into an unbuffered region alone never shows Reconnecting.
- Fatal network errors (non-playlist) retry `hls.startLoad()` after 1 s, 2 s, and 4 s (three attempts). Fatal media errors call `recoverMediaError()` twice, then `swapAudioCodec()`. A successful `FRAG_LOADED` resets the attempt count. After three fatal attempts, show the error overlay.
- Non-fatal errors are counted for stats and never show the overlay. `bufferStalledError` is non-fatal: it shows a spinner only when the buffer is empty and never the overlay.
- The overlay is `role="alert"`, focuses Retry, and offers Retry plus a link back to `/`. Retry reloads at the last known position (live and following: the live edge) and resets attempts. A Complete recording whose playlist stays 404 reads as unavailable; the Home deletion path may already be navigating away. A live capture that becomes Complete while still on the first-load wait reads as unavailable; a mid-stream reconnect does not. While buffered media remains, a fatal live playlist error never shows the overlay.
- The unsupported-browser state uses the same overlay without Retry. Playback/permission failures from `video.play()` leave the centre play button visible; they are not errors.

## 14. Accessibility and known limitations

- Every control has an accessible name; focus uses the 2 px ring: `player-accent` inside the player, `primary` in stats and chat; the player container is keyboard focusable; the seek and volume sliders expose values and value text; menus, the stats switch, and the panel use the ARIA patterns above.
- Reduced motion removes REC pulse, player fades, collapse animation, and any non-essential transition. Forced-colors and high-contrast use the design-system tokens.
- Known and accepted for v3:
  - One source level; no quality menu or quality preference (E-20).
  - No captions: the recorder does not capture a subtitle track and the player has no caption control. The axe media-caption best-practice rule is not a v3 gate.
  - The live edge is about one target duration behind the true playlist end.
  - Native HLS (iOS Safari) may use native fullscreen/controls and exposes limited stats.
  - Resume/progress and playback preferences are shared per recording/browser, not per user or tab.
  - Fullscreen, PiP, and native-HLS transitions are unit-tested only. There is no manual iPhone Safari pass: it was dropped on 2026-11-12 because no iOS device is available (#108).

## 15. Test matrix

bUnit:

- `VideoPlayer` renders the skeleton once, initializes with the correct payload (including the one-shot `autoplay` flag), never re-renders, calls `load` on an id change and `update` on a same-id state/title/chapter change, ignores `ResumeSec` on update, and disposes. `Home` consumes `autoplay` into the player and strips it from the URL.
- `UiPreferences.Changed` drives `setStatsOpen`/`setTheater`; the component's buttons call the .NET setters; `Notify` shows a snackbar (`ISnackbar.Show`).
- The player dispatches `sk:playhead` with the recording id, time, duration, paused/rate/live flags, throttles `timeupdate` to 250 ms, and emits immediately on the state events above.
- `PlayerBridge` queues and flushes seeks and play requests, clamps seeks, and clears on detach.
- `StatsPanel`: the overlay's hooks, its "Statistics" region and header buttons, hidden while closed, the series swatches, the Stream row hidden until samples, and recording changes through `SetRecordingAsync`; its tab form has no header and is always open. `ViewersPanel`: the chart hooks, hidden until samples, the disclosure and `ViewersOpen`, the tab form without a header, the chart's `role="img"` with the sample table outside it, and `Seek` through `PlayerBridge` (#211).
- `ChatPane` has no toggle (`data-sk="chat-toggle"`) in either form. Tab form: no header, `data-open="true"`, body not inert. Its payload carries `badgesEnabled` and `showBadges`; a badge change calls `setShowBadges` without `setSyncOffset`, and a sync change leaves it alone.
- `Home` watch layout: Md and up renders player/details/stats in order; below Md the player stays outside `MudTabs`, the summary is outside the tabs, Chat is first when the recording has chat, and the player instance survives the breakpoint.
- `Home` deletion: `Find` null shows the snackbar, opens the next item, or `/` when none; unknown id keeps the message.

Vitest:

- Timecode, chapter lookup/ticks, seek math, prefs parse/clamp, ring buffer, retry policy, copy JSON, and title/URL text handling.
- Module with a fake hls loader and fake media: init/load/update/dispose, source swap and live/VOD transitions, live edge/follow, selection autoplay at a resume position, the live edge and 0, repeat selection without a reload, a rejected `play()` leaving the paused UI, keyboard scope, idle, progress cadence/pagehide/dispose, retries and overlay, native fallback, stats sampler/sparkline/copy, stats/theater sync.
- Waiting and reconnect policy (#247): first-load manifest, parse, timeout and empty-playlist waits; a buffered mid-stream playlist failure shows Reconnecting without a spinner and keeps playing; an empty-buffer failure spins with Reconnecting; `LEVEL_LOADED`/`FRAG_LOADED` recovery restores the capture chip; `waiting` with and without a buffer; a seek into an unbuffered region; the 0.5 s boundary; `bufferStalledError`; and Complete-without-a-segment versus mid-stream reconnect.
- Menu and slider ARIA/keyboard behaviour; reduced motion paths; playhead throttle/immediate events; global keys ignored inside `[data-sk-chat]`.
- Viewer chart (#211): summary figures, count at the playhead, tooltip/compact/label text, geometry, gridlines and x labels, pointer mapping; the module's fetch on open, once per finished recording, 60 s and reopen refresh while live, abort on close/dispose, stale and mismatched responses, 404 and empty state, playhead, hover, click/drag seek, live edge, and the snapshot's `viewers`.
- Islands (#180): icon swaps by `data-icon`; the player's Chat toggle follows `sk:chat`, reads a pane already in the page, and asks with `sk:chat-toggle`; the pane announces `sk:chat` and toggles on `sk:chat-toggle`; channel-coloured streamer names and the 4.5:1 name fallback.
- Badges (#276): the payload applies at load and on a reload; the live toggle flips `data-badges` with no fetch and no log rebuild; the catalog image replaces its chip, an unknown version and an image error keep it, and the catalog is fetched once per open. The image carries its title as `alt`, lazy/async decoding, and `referrerpolicy="no-referrer"`.
- Badge text (#273): an unknown badge set's chip carries the class the pane rule hides.

xUnit/bUnit (islands):

- `IslandIconTests`: every glyph in `island-icons.js` equals its MudBlazor constant; `IslandIcon.Svg` markup and its unknown-name failure; the player's Reconnecting and Partial chips carry StatusChip's hooks, words and glyph.
- Each player control draws its Material Rounded glyph; the Chat toggle is hidden, named, `aria-keyshortcuts="C"`, beside stats; speed items carry `Check`. Stats section icons, sparkline colours, the Stream empty state and a plain Copy button. The chat payload's `channelColor` matches the drawer's colour for the same login.

Playwright (Chrome channel against the Mock profile):

- Selecting a finished recording opens it at 0 and playing; pause/play/seek; resume across reload; progress clears near the end.
- Selecting a drawer row or Home card starts playback, strips `autoplay` from the URL, and a reload, direct link, or back/forward stays paused; re-selecting the open recording plays without reloading; a rejected `video.play()` leaves Play and no overlay; deleting opens the next recording paused.
- Growing capture: a selection opens playing at the live edge; leave the edge and pause, the row shows a partial resume bar, and a reload opens paused at that position, not following; Live edge then restores the full bar and the next reload opens at the live edge.
- Growing capture: open near the live edge, duration grows, seek back and press Live edge, finalize and observe the live→VOD handoff; reopen a finalized session and observe VOD→live.
- Delete the open recording: snackbar and next item (or empty library).
- Stats: open/close, sampler values, player/panel sync, copy with clipboard permission and snackbar; reload restores `sk-ui`.
- Viewer chart (#211): on a finished Mock recording with seeded samples, the tile's Now matches the endpoint, the chart and sample table render, hover shows the tooltip, a click seeks the video and the tile follows; axe clean at 1280 and in the 375 Stats tab.
- Idle after 3 s, pointer restore, reduced-motion emulation; preferences (volume/mute/speed) across reload; theater and keyboard shortcuts; fullscreen/PiP controls present and correctly hidden when unsupported.
- axe clean and keyboard-only on the player page (closed and open stats, and open chat); no automatic retries.
- Watch layout (#175): at 375×812 the edge-to-edge player stays sticky under the bar while the main column scrolls, and the Chat/Chapters/Stats tabs switch their panels (Chapters only for a recording with chapters); at 1280 theater mode's player is the view's full width with the title block and chat below; open desktop chat is a 320–400 px column from the top of the main column to the bottom of the view, sticky while the left column scrolls; crossing the breakpoint keeps the same `<video>` element; axe clean and no sideways scroll at both widths.
- The player's Chat button and C toggle the same pane and the button mirrors it; the streamer's own message shows the channel colour the drawer shows; focus rings are `player-accent` in the player and `primary` in chat; the Stream section's empty state or viewer tile. Show badges hides the pane's badge images and chips live, restores them, and persists across a reload without a chat refetch; axe clean.

Native HLS, iOS fullscreen, and PiP transitions are covered by Vitest/bUnit stubs only; the manual iPhone pass was dropped (#108), and the [iPhone Safari checklist](manual/iphone-safari-checklist.md) is kept for anyone who can test on a device; the E2E job installs branded Chrome because Playwright's bundled Chromium lacks H.264 (E-21). The E2E media fixture is a short H.264 fMP4 generated at test time; no large binaries are committed.

## 16. Binding versus implementation freedom

Binding: static-player/JS ownership, module loading and native fallback, source lifecycle and payload, selection-only autoplay and its paused fallback, live-edge/follow semantics, live/VOD transitions, delete reaction, control order and responsive hiding, seek commit and ARIA, keyboard scope, idle rules, fullscreen/PiP/theater behaviour, resume cadence/skip/clear rules, `sk-player` schema, stats sampling/metrics/copy JSON, `PlayerBridge`, the watch layout (§2a), media/auth assumptions, error/waiting policy, and the test matrix.

Freedom: exact markup beyond the `sk-` classes, JS helper style and internal function names, error wording, menu animation, sparkline path details, and test implementation.

## 17. Related

- `docs/backend-ui-contract.md` for `RecordingView`, `ProgressStore`, and `ILibraryView`.
- `docs/frontend-foundation-contract.md` for the shell, `UiPreferences`, primitives, and reconnect.
- `docs/library-contract.md` for the F8-T3 boundary and library live updates.
- `docs/session-store-contract.md` for the stitched EVENT playlist and chapters.
- `docs/hosting-contract.md` for `/media`, caching, and public access.
- `docs/reviews/implementation-plan-amendments.md` (E-20, E-21).
