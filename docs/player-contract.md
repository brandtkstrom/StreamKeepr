# Stream Keepr Player Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 2, F3/F4/F8; `docs/backend-ui-contract.md`; `docs/frontend-foundation-contract.md`; `docs/library-contract.md`; `docs/hosting-contract.md`; `docs/session-store-contract.md`; `docs/testing-contract.md`; `design-system/components/VideoPlayer` and `StatsPanel`; plan review E-20/E-21.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract covers the recording player, its hls.js engine, control bar and seek bar, live-edge and live-to-VOD behaviour, resume progress, playback preferences, the Stats for nerds panel, the chapter-seek bridge, and the player's error/loading states. The details card, export, delete, and auth UX belong to the next ticket; this contract defines only the `PlayerBridge` they call and the player's reaction to deletion.

The stitched `index.m3u8` is a **single media playlist**, not a multi-variant master: one Streamlink quality is captured, with `#EXT-X-MAP` per part and `#EXT-X-DISCONTINUITY` between parts. The player therefore exposes one source level. Quality switching and a quality preference are deferred (E-20).

## 2. Composition and ownership

- `Home.razor` resolves the selected recording through `ILibraryView.Find` and renders the player column in the fixed order player → stats → details.
- `VideoPlayer.razor` renders the complete player skeleton once: a `<video>` with no native controls, `playsinline`, `preload="metadata"`, and the thumbnail as poster; the title strip, centre play/replay, seek bar, control bar, menus, spinner, and error overlay. A mouse/pen click on the video frame toggles play/pause; controls, links, and menus keep their own click behaviour.
- `VideoPlayer.razor.js` owns hls.js, every player control, all dynamic player DOM, idle state, keyboard handling, progress timing, and error recovery. It calls .NET only for progress, `sk-ui` preferences, and the stats snackbar.
- After the first interactive render, `VideoPlayer` returns `ShouldRender() => false`. Parameter changes call the JS module (`load`/`update`) but never re-render the player markup, so Blazor cannot overwrite JS-owned attributes, classes, text, or SVG children.
- `StatsPanel.razor` likewise renders its skeleton once; `stats.js` owns the open state, values, sparklines, and copy button. The panel's switch exposes `aria-checked` from `UiPreferences.StatsOpen`; the section carries a non-ARIA `data-open` hook for the 300 ms collapse. A section-level `aria-expanded` is deliberately not used because ARIA does not allow it on a generic section (axe reports `aria-allowed-attr`); the switch and the inert body carry the state. The switch is a button containing only phrasing content, with the panel heading outside it, because a heading is invalid button content.
- `RecordingDetails` remains a normal Blazor component. It injects the scoped `PlayerBridge` to seek; it never manipulates the player DOM.
- The shared `IconButton`, `Menu`, `Switch`, and `StatusChip` primitives remain the default for non-player UI. Player controls use raw `sk-` markup because their events are JS-owned; their ARIA and keyboard behaviour follows the foundation contract.

## 3. Engine and source lifecycle

- On the first interactive render, `VideoPlayer.razor.js` is imported; it dynamically imports the vendored hls.js from `wwwroot/lib/hls.js/hls.mjs` only when `Hls.isSupported()`. No hls.js request occurs on `/` or before a recording page.
- If hls.js is unsupported but the browser reports native HLS support, the module sets `video.src` and uses the native pipeline. If neither is supported, the error overlay explains that the browser cannot play HLS; there is no retry.
- `load(view)` runs when the recording id changes (including `Home` navigating to the next recording after a delete):
  1. Flush the previous recording's progress when eligible.
  2. Destroy the previous hls instance and abort its listeners/timers.
  3. Reset media, error, idle, stats, seek, and menu state.
  4. Apply volume, mute, and speed preferences.
  5. Create hls.js or set the native source; attach the new poster, title, streamer login, chapters, and state.
  6. Restore the resume position for Completed recordings, or the live edge for Recording/Reconnecting.
- `update(view)` runs for the same recording when `LibraryView.Changed` supplies a new `RecordingView`. It updates the title, streamer login, poster, chapters, and state chip. It never applies `ResumeSec` again and never resets playback. The component skips `update` when the `RecordingView` instance is unchanged.
- The init/update payload uses camelCase:
  - `recordingId`, `playlistUrl`, `title`, `streamerLogin`, `thumbnailUrl`
  - `state`: `recording`, `reconnecting`, `partial`, `saved`
  - `durationSec`, `resumeSec`, `chapters[]`, `statsOpen`, `theater`
  - `chapters[]` is `{ startSeconds, title, game }` from `RecordingView.Chapters`.
- `dispose()` flushes progress, destroys hls/native media, clears timers and listeners, and releases the module reference. The `DotNetObjectReference` is disposed by the component.
- The .NET reference exposes `SaveProgress(string recordingId, double)`, `ClearProgress(string recordingId)`, `SetStatsOpen(bool)`, `SetTheater(bool)`, and `Notify(string)`. The progress calls carry the recording id so a save in flight across a recording change lands on the recording it belongs to. `Notify` raises a `SnackbarService` message; it is used only for copy-stats confirmation/failure.
- `UiPreferences` is the single source for stats/theater state. JS applies `setStatsOpen(bool)`/`setTheater(bool)` when `UiPreferences.Changed` fires; the player's own buttons call .NET to persist.

## 4. Playback model

- No autoplay. A recording page shows the poster and centre play button; the playhead is positioned at the resume point (Completed) or the live edge (Recording/Reconnecting). The user presses play.
- hls.js configuration is the defaults plus:
  - `enableWorker: true`
  - `lowLatencyMode: false`
  - `backBufferLength: 90`
  - `liveSyncDurationCount: 1`
  - `liveMaxLatencyDurationCount: Infinity`
  - `maxLiveSyncPlaybackRate: 1`
- The playlist target duration is 12 s, so the live sync position is about one target duration behind the true end. This is the player's **live edge** and avoids a guaranteed stall at the last segment.
- The red live tick marks the seekable end (`seekable.end`), not the sync position. The **Live edge** button seeks to `hls.liveSyncPosition` and enables following.
- Following: while Recording/Reconnecting, playing, and following is on, the player keeps the playhead within roughly one target duration of the sync position; if it falls more than 2 s behind `liveSyncPosition`, it seeks forward to it. A manual seek more than 2 s behind the sync position, or a pause, turns following off. Pressing **Live edge**, seeking to within 2 s of the sync position, or seeking to the red tick (within 2 s of `seekable.end`) turns it on; a seek to the true end snaps back to `liveSyncPosition` so playback starts buffered.
- The player opens at `hls.liveSyncPosition` (or `seekable.end - 1`, clamped, before hls.js reports a position) for Recording/Reconnecting; it ignores `ResumeSec`.
- **Live to VOD:** when hls.js loads a playlist with `details.live === false` (ENDLIST), the player hides the live tick and button, stops following, updates the duration to the finite media duration, keeps the current position, and begins progress saving. When the .NET state also becomes Partial/Saved, the chip updates; the two paths are idempotent.
- **VOD to live:** a finalized session can reopen as a new part (Saved/Partial → Recording/Reconnecting). The player reloads the same playlist at the current position, preserving the paused/playing state; it does not jump to the live edge. The user can press **Live edge** afterwards.
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
11. Picture-in-picture (hidden when unsupported)
12. Theater
13. Fullscreen

- Play/pause updates the icon and `aria-label`; the centre button shows `play_arrow` when paused and `replay` when ended. Starting from the centre button moves focus to the control-bar play button. Replay starts at 0 and clears progress.
- Skip buttons clamp to `[0, duration]` and disable when no metadata is available.
- Timecode uses the JS twin of `Format.Timecode`; total grows while live and settles on ENDLIST.
- The seek bar is `role="slider"`, `tabindex="0"`, with `aria-valuemin`, `aria-valuemax`, `aria-valuenow`, and `aria-valuetext` (`1:02:13 of 3:41:12`). It is disabled until metadata exists.
- Pointer drag previews the position and commits a single seek on `pointerup`; a click seeks directly. Buffered ranges use `secondary`, played uses `primary`; the knob appears on hover/drag; touch uses `touch-action: none`.
- Hover shows a timecode tooltip plus the chapter title when one applies. Chapter ticks come from `RecordingView.Chapters`; invalid or out-of-range chapters are ignored.
- Keyboard on the seek bar: ←/→ ±5 s, Home/End to start/end; the player's global handler yields to the slider.
- Volume is a mute button plus a slider (`role="slider"`, 0–100, arrows ±5). It unmutes when raised above 0. At ≤520 px container width the whole volume group is hidden; the OS volume remains.
- Speed options follow the design preview: 0.5×, 1×, 1.5×, 2×; the pill shows the current value and the menu checks the active item.
- The quality pill is static text (“Source”) and is not a button; there is no quality menu or preference (E-20). It hides at ≤520 px.
- The stats toggle and the panel switch share one state; both use `aria-pressed`/`aria-checked`.
- Picture-in-picture is button-only. It is hidden when `document.pictureInPictureEnabled` is false or the API is absent, disabled while fullscreen, and its icon/aria follows enter/leave events. No custom controls exist inside the PiP window.
- Fullscreen targets the `.sk-player` container so custom controls remain. `:fullscreen` fills the viewport, contains the video, and drops the radius. If container fullscreen is unavailable, fall back to `video.requestFullscreen()`; native Safari may use its own controls. Entering fullscreen focuses the container; exiting returns focus to the fullscreen button.
- Theater calls `UiPreferences.SetTheater(!Theater)` and updates its pressed state through `Changed`; the shell owns hiding the drawer and the background change. Theater does not resize the player beyond the shell.
- Responsive control hiding uses the existing container queries: ≤720 px hides skip, PiP, and speed; ≤520 px hides theater, volume, the quality pill, and the live-edge label. Keyboard shortcuts remain available.

## 6. Idle, overlays, and focus

- The title strip, centre control, and control bar fade after 3 s without pointer movement while playing. Idle is not applied while paused, while focus is inside the player, or while a menu is open; the cursor hides with the overlays.
- `pointermove`, `pointerdown`, `keydown`, and `focusin` reset the timer and reveal the overlays. A mouse/pen click on the video frame toggles play/pause; a touch tap only reveals the controls. The centre/control play button remains the keyboard and accessible play surface, and clicks on controls never bubble into a frame toggle.
- Reduced motion removes the fade transitions but not the hide behaviour.
- Focus order follows the DOM: centre play (when visible), seek bar, play, skip, volume, live edge, speed, stats, PiP, theater, fullscreen. Menus return focus to their trigger. The error overlay focuses Retry; the player regains focus after a successful retry.
- The status chip (`Rec`, `Reconnecting`, `Partial`) is a polite live region; timecode and stats values are not. Errors use `role="alert"`.

## 7. Keyboard

- The player listens on `document` while a recording is active. It ignores events with modifiers or `defaultPrevented`, editable targets (`input`, `textarea`, `select`, `[contenteditable]`), open menus/dialogs, and keys handled by the seek/volume slider.
- Space/K toggle play/pause. They do nothing when the target is a button or link, so native activation wins.
- J/L seek ±10 s; ←/→ seek ±5 s, except inside the drawer (tree keys win) or on a slider (the slider handles them).
- M toggles mute; F toggles container fullscreen; T toggles theater; I toggles stats.
- `/` and Esc belong to `shortcuts.js`; the player never handles them.
- Menu keys: ↑/↓/Home/End move, Enter/Space choose, Esc/outside click close, Tab closes and continues; `role="menu"` with `menuitemradio` options and `aria-checked`.

## 8. Resume and progress

- Completed recordings (Saved/Partial) start at `ResumeSec`, clamped to `[0, duration - 1]`; there is no resume prompt, toast, or “start over” control. A null `ResumeSec` starts at 0.
- Recording/Reconnecting ignore resume entirely. Progress writes are skipped while the media is live; they begin when ENDLIST makes the duration finite. VOD→live stops writes again.
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

## 10. Stats for nerds

- `StatsPanel.razor` renders the design-system panel once, directly under the player: a 48 px switch header, two columns (Video in `primary`, Network in `tertiary`), two tiles per column, and the key/value lists. The header is a switch button containing only phrasing content; a visually hidden `h2` outside the button names the panel and keeps heading order valid. The body is `inert` when collapsed; closing while focus is inside moves focus to the switch first.
- Video tiles: **Bitrate** (Mb/s) and **Frame rate** (fps). Network tiles: **Bandwidth estimate** (Mb/s) and **Buffer health** (s). Each has a 20-sample, 2 px sparkline with a hover crosshair and a value tooltip; an active tooltip is repositioned rather than blanked when a sample redraws the sparkline.
- Video key/values: resolution (encoded → viewport when different), codecs, dropped frames (`dropped of total`), decoder (`MSE · hls.js` or `Native HLS`).
- Network key/values: last segment (`#index · duration · load time`), segments loaded, errors/retries (⚠ prefix and `error` text when non-zero), playlist URL (ellipsised, full value in `title`).
- Sampling runs every 2 s while the panel is open into a 20-sample ring. The timer stops when collapsed; segment/error counters continue to accumulate. Opening resets the ring and starts a fresh window. The panel can sample while paused; values stay flat.
- The header hint is `hls.js · source` for the single media playlist, `hls.js · level N / M` only if a future master playlist exposes more, and `Native HLS` otherwise.
- Frame rate uses `level.attrs['FRAME-RATE']` when present; otherwise estimate with `requestVideoFrameCallback` over the open panel's sample window. The callback loop starts when the panel opens, resets the estimate at that point, and stops when the panel collapses or disposes. Bitrate uses the level bitrate, then measured `FRAG_LOADED` bytes/duration; otherwise `—`. Bandwidth comes from `hls.bandwidthEstimate`; native mode shows `—` for hls-only values. Resolution uses `video.videoWidth/Height` for the viewport and the level for the encoded size.
- The SVG is `aria-hidden`; each tile keeps a visually hidden table of the samples for screen readers. Values are not a live region. Problems are text plus ⚠, never colour alone.
- The player's stats button is owned by `VideoPlayer.razor.js`: it updates its own pressed state immediately, announces `sk:stats-open` so `StatsPanel.razor.js` applies the same state to the panel and sampler before the circuit round-trip, and calls `SetStatsOpen` to persist. The panel switch updates the switch, player button, and body, then calls `SetStatsOpen`. Both controls therefore share the same `UiPreferences.StatsOpen` state. There is no URL query key. Reload restores the state from `sk-ui` with no flash.
- “Copy stats” is a Text button in the panel body (never inside the switch row). It writes a v1 JSON snapshot to the clipboard:
  ```json
  {
    "v": 1,
    "recordingId": "…",
    "capturedAt": "2026-11-10T18:00:00.000Z",
    "mode": "live|vod|native",
    "playlistUrl": "/media/…/index.m3u8",
    "level": {"index":0,"width":1920,"height":1080,"frameRate":59.94,"bitrate":6200000,"videoCodec":"…","audioCodec":"…"},
    "samples": [{"at":0,"bitrateMbps":6.2,"frameRate":59.94,"bandwidthMbps":48.6,"bufferSec":28.4,"droppedFrames":3}],
    "errors": [{"at":0,"type":"networkError","details":"fragLoadError","fatal":false}]
  }
  ```
  Samples are the last 20; errors the last 10; unavailable values are `null`. The clipboard call uses only public playback data. Success shows “Stats copied”; failure shows “Could not copy stats” through `SnackbarService`. No legacy clipboard fallback.
- The panel stacks to one column below 600 px; tiles remain two per section. Collapse motion is the design-system 300 ms and is removed under reduced motion.

## 11. Chapter seek

- A scoped `PlayerBridge` is the only seek seam for sibling components:
  ```csharp
  public sealed class PlayerBridge
  {
      public bool IsReady { get; }
      public void Attach(Func<double, ValueTask> seek);
      public void Detach();
      public ValueTask SeekAsync(double seconds);
  }
  ```
- `VideoPlayer` attaches after JS init and detaches on dispose. `SeekAsync` clamps the lower bound to `0`; the player JS clamps the upper bound to the media duration it owns, so the bridge does not carry a second duration copy. If the player is not ready it stores the latest request and flushes it after attach. A click on a chapter in `RecordingDetails` calls it and leaves focus on the chapter button.
- Chapter ticks, hover chapter names, and the details list all use `Chapter.StartSeconds`; empty or invalid chapter data produces no ticks and no tooltip title.

## 12. Media URLs, caching, and security

- `RecordingView.PlaylistUrl` is the relative `/media/{channel}/{id}/index.m3u8`; the player resolves it against `document.baseURI` and never constructs or rewrites media paths. Segments resolve relative to the playlist.
- Media is public and same-origin: no auth header, cookie requirement, token query, CORS, or `crossorigin` attribute. The hosting allowlist is the only gate.
- Playlists are served `no-cache`; segments/init files/thumbnails are immutable. The player relies on hls.js refreshing the live playlist and must not append cache-busting query strings.
- Titles, chapter names, error text, and playlist URLs are written with `textContent`/attributes, never `innerHTML`. The `PlaylistUrl` is used only for the media source and `title`.
- No secrets, tokens, or personal data enter `localStorage`, copied stats, logs, or the DOM.

## 13. Loading, waiting, and errors

- On load the poster shows; `aria-busy` is set until metadata and the first buffered range exist. A spinner appears after 500 ms of loading, with a visually hidden “Loading video”.
- While Recording/Reconnecting and the playlist is missing, empty, or has no complete segment, show “Waiting for the first segment…” with the spinner and retry the manifest every 3 s. This is not an error and has no timeout. It becomes an error only if the session is Complete.
- Fatal network errors retry `hls.startLoad()` after 1 s, 2 s, and 4 s (three attempts). Fatal media errors call `recoverMediaError()` twice, then `swapAudioCodec()`. A successful `FRAG_LOADED` resets the attempt count. After three fatal attempts, show the error overlay.
- Non-fatal errors are counted for stats and never show the overlay. `bufferStalledError` while live is a spinner, not an error.
- The overlay is `role="alert"`, focuses Retry, and offers Retry plus a link back to `/`. Retry reloads at the last known position (live: the live edge) and resets attempts. A Complete recording whose playlist stays 404 reads as unavailable; the Home deletion path may already be navigating away.
- The unsupported-browser state uses the same overlay without Retry. Playback/permission failures from `video.play()` leave the centre play button visible; they are not errors.

## 14. Accessibility and known limitations

- Every control has an accessible name; focus uses the 2 px `primary` ring; the player container is keyboard focusable; the seek and volume sliders expose values and value text; menus, the stats switch, and the panel use the ARIA patterns above.
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

- `VideoPlayer` renders the skeleton once, initializes with the correct payload, never re-renders, calls `load` on an id change and `update` on a same-id state/title/chapter change, ignores `ResumeSec` on update, and disposes.
- `UiPreferences.Changed` drives `setStatsOpen`/`setTheater`; the component's buttons call the .NET setters; `Notify` reaches `SnackbarService`.
- `PlayerBridge` queues until ready, clamps, flushes, and clears on detach.
- `StatsPanel` initial open/closed markup, placeholders, inert collapsed body, and switch/player-button ARIA.
- `Home` deletion: `Find` null shows the snackbar, opens the next item, or `/` when none; unknown id keeps the message.

Vitest:

- Timecode, chapter lookup/ticks, seek math, prefs parse/clamp, ring buffer, retry policy, copy JSON, and title/URL text handling.
- Module with a fake hls loader and fake media: init/load/update/dispose, source swap and live/VOD transitions, live edge/follow, keyboard scope, idle, progress cadence/pagehide/dispose, retries and overlay, native fallback, stats sampler/sparkline/copy, stats/theater sync.
- Menu and slider ARIA/keyboard behaviour; reduced motion paths.

Playwright (Chrome channel against the Mock profile):

- Open a finished recording and play/pause/seek; resume across reload; progress clears near the end.
- Growing capture: open near the live edge, duration grows, seek back and press Live edge, finalize and observe the live→VOD handoff; reopen a finalized session and observe VOD→live.
- Delete the open recording: snackbar and next item (or empty library).
- Stats: open/close, sampler values, player/panel sync, copy with clipboard permission and snackbar; reload restores `sk-ui`.
- Idle after 3 s, pointer restore, reduced-motion emulation; preferences (volume/mute/speed) across reload; theater and keyboard shortcuts; fullscreen/PiP controls present and correctly hidden when unsupported.
- axe clean and keyboard-only on the player page (closed and open stats); no automatic retries.

Native HLS, iOS fullscreen, and PiP transitions are covered by Vitest/bUnit stubs only; the manual iPhone pass was dropped (#108), and the [iPhone Safari checklist](manual/iphone-safari-checklist.md) is kept for anyone who can test on a device; the E2E job installs branded Chrome because Playwright's bundled Chromium lacks H.264 (E-21). The E2E media fixture is a short H.264 fMP4 generated at test time; no large binaries are committed.

## 16. Binding versus implementation freedom

Binding: static-player/JS ownership, module loading and native fallback, source lifecycle and payload, no autoplay, live-edge/follow semantics, live/VOD transitions, delete reaction, control order and responsive hiding, seek commit and ARIA, keyboard scope, idle rules, fullscreen/PiP/theater behaviour, resume cadence/skip/clear rules, `sk-player` schema, stats sampling/metrics/copy JSON, `PlayerBridge`, media/auth assumptions, error/waiting policy, and the test matrix.

Freedom: exact markup beyond the `sk-` classes, JS helper style and internal function names, error wording, menu animation, sparkline path details, and test implementation.

## 17. Related

- `docs/backend-ui-contract.md` for `RecordingView`, `ProgressStore`, and `ILibraryView`.
- `docs/frontend-foundation-contract.md` for the shell, `UiPreferences`, primitives, and reconnect.
- `docs/library-contract.md` for the F8-T3 boundary and library live updates.
- `docs/session-store-contract.md` for the stitched EVENT playlist and chapters.
- `docs/hosting-contract.md` for `/media`, caching, and public access.
- `docs/reviews/implementation-plan-amendments.md` (E-20, E-21).
