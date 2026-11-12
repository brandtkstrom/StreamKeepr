# Stream Keepr Library Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 2, F2/F7/F8; `docs/backend-ui-contract.md`; `docs/frontend-foundation-contract.md`; `design-system/components/LibraryDrawer`; plan review E-19.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract covers the library drawer, grouping and day headers, search/filter/sort, keyboard navigation, long-list rendering, selection, and live updates. The player's reaction to live changes belongs to the player ticket; the shared library/status changes are exposed here.

## 2. Data and view models

- `LibraryDrawer` consumes `ILibraryView` (`StreamerView`, `RecordingView`, `StorageView`, `LibraryQuery`). Components never read `RecordingLibrary`, `DvrStatus`, `ProgressStore`, or `StorageMonitor` directly.
- `Streamers` includes channels with at least one recording. A watched channel with zero recordings appears in Settings (`ChannelsView`), not the library. `StreamerView.LastRecordedAt?` and `RecordingCount` remain nullable/zero-safe.
- `RecordingView` state rules:
  - `Recording`: manifest `Recording` and `DvrStatus.ActiveJob.SessionId == Id`.
  - `Reconnecting`: manifest `Recording`, no active job for the session.
  - `Partial`: Complete with more than one part.
  - `Saved`: Complete with one part.
- Status chips: Recording → `Rec`; Reconnecting → `Reconnecting` warning; Partial → `Partial` warning; Saved → no chip. `Processing` exists in the primitive for finalize/export but is not a library state in v3.
- `Chapters`, `ResumeSec`, `ThumbnailUrl`, `PlaylistUrl`, `DurationSec`, `SizeBytes`, and counts come from `ILibraryView` as defined.

## 3. Grouping and sort

- Streamer groups: a streamer with any `Recording` item is first. Remaining groups follow the selected sort:
  - `newest`: latest `LastRecordedAt` descending.
  - `oldest`: earliest `LastRecordedAt` ascending.
  - `az`: display name/login ordinal ascending.
- Recordings inside a group:
  - `newest` and `az`: `StartedAt` descending.
  - `oldest`: `StartedAt` ascending.
- Ties break by login (groups) or recording `Id` ordinal (rows). The order is deterministic.
- `newest` is the default. The drawer header shows `↓ Newest first`, `↑ Oldest first`, or `A–Z by streamer`.
- Recording streamers remain first in every sort; “A–Z” sorts the remaining groups.

## 4. Query, search, filters, and URL

- URL query keys: `q` (search), `state` (filter), `sort` (sort). Missing/invalid values fall back to no search, no filter, `newest`.
- `state=recording` matches `Recording` and `Reconnecting`; `state=saved` matches `Saved` and `Partial`; anything else means no filter.
- `q` matches title, category, streamer display name, and login; trimmed, case-insensitive, no regex. Matches are highlighted with `mark.sk-hl`.
- Search is debounced 250 ms in JS before crossing the circuit. The app-bar field updates the URL; `/` focuses it, Esc clears it; ignore the shortcut while typing.
- URL updates use `NavigationManager.NavigateTo(..., replace: true)` so filtering does not fill browser history; a copied URL restores the view.
- Filter chips under the drawer header: All, Recording, Saved; selected chip uses `aria-pressed`; Recording also matches Reconnecting; Saved also matches Partial.
- Empty groups and day headers hide under a filter/search. An empty result shows “No recordings match” in the drawer.
- Day headers: `Today`, `Yesterday`, then `Wed, Nov 4` in the viewer's zone (`ViewerClock`). Day headers are not focusable items.

## 5. Drawer structure

```text
.sk-drawer#sk-drawer
  header (sort label, filter chips)
  .sk-group (streamer)
    .sk-group-head (avatar, name, count · state, chevron)
    .sk-day
      .sk-row (recording)
```

- `StreamerGroup`: avatar/image or initials; red ring while any item is Recording; name; `14 recordings · Recording now` (or `· Reconnecting`, or `· last capture`); chevron; `aria-expanded`; independent collapse stored in `sk-ui`.
- `RecordingRow`: thumbnail (16:9, duration badge in timecode), resume bar in `primary` when `ResumeSec` exists, live bar in `rec` while Recording, two-line title, meta line, status chip except Saved; selected row `aria-current="true"` with `secondary-container` and the thumbnail ring; the row is `<a href="/r/{id}">` and works with or without the circuit.
- `DayHeader`: not focusable; hidden when its group/filter is empty.
- `LibraryRail`: avatars only, live ring; clicking expands the drawer and scrolls to the group; keyboard-reachable; one tab stop per rail item or roving focus consistent with the tree.
- The drawer lives in the layout and survives navigation; selecting a recording preserves drawer scroll. Changing search/filter/sort resets the list to the top; on load, the selected row scrolls into view.
- Loading: skeleton rows until `RecordingLibrary.IsLoaded`. Empty library: “No recordings yet”. No-results: “No recordings match”. The `AppShell`/`Home` owns the unknown `/r/{id}` message.

## 6. Keyboard tree

- One tab stop for the whole flattened list; roving `tabindex` on items.
- `role="tree"` on the list; group heads and rows are `treeitem` with `aria-level`/`aria-expanded`/`aria-current`.
- ↑/↓ move between visible group heads and rows (day headers skipped); Home/End jump to first/last.
- ←/→ collapse/expand a group (the only calls back to .NET); Enter opens the selected recording; Enter on a group toggles it.
- Focus movement is handled by `tree-nav.js` on the rendered DOM, without a server round-trip. Focus ring is the design-system 2 px `primary`.
- Global `/` focuses search; Esc clears search; typing in an input is never intercepted. The player's shortcuts (later) must ignore editable targets and not conflict; the library tree handles keys only while focus is inside the drawer.
- Collapse/expand and selection changes re-render through the normal Blazor state; focus returns to the same logical item after a re-render.
- #56 ships the drawer with ordinary navigation semantics (`nav` plus real links and `aria-current`) and defers `role="tree"`, roving tabindex, and the key handling above to #58. Until then the drawer remains keyboard-usable through its normal links and group buttons.

## 7. Long lists (F2-T7 revision)

The plan's `<Virtualize>` over a flattened list of variable-height group heads, day headers, and rows breaks focus and scroll behavior. Replace it with:

- All items rendered in one flattened container; `.sk-group`/`.sk-day`/`.sk-row` use `content-visibility: auto` and `contain-intrinsic-size` estimates.
- Keyboard, focus, `aria-current`, and scroll position work against real DOM.
- Revisit virtualization only if measurements exceed roughly 2,000 flattened items or a real performance issue appears; then use a measured variable-height virtualizer, not a fixed `ItemSize`.

This is recorded as E-19 in the plan-review register.

**Measured 2026-11-11 (#110).** `LibraryScaleJourneyTests` seeds 1,008 recordings (2,019 flattened items: 4 groups, 1,007 day headers, 1,008 rows) and writes `library-scale.txt` on each E2E run. Local Chrome, 1280×820:

| Measure | Result | Budget |
| --- | --- | --- |
| Warm load, all rows and circuit ready | 315 ms (first byte 18 ms, 525 KB HTML) | 2 s |
| Search to one row, including the 250 ms debounce | 282 ms | 2 s |
| Clear search back to all rows | 128 ms | 3 s |
| Scroll top to bottom, slowest frame | 18.4 ms; no frame over 50 ms | 250 ms |

Without `content-visibility` the same run was ready in 379 ms with the same search and scroll numbers. **E-19 stands:** nothing here calls for virtualization. The first page after the app starts takes about 6 s to send its first byte with 8 recordings as well as 1,008, so that time is app warm-up, not library size. It is not budgeted here.

## 8. Live updates

- `ILibraryView.Changed` coalesces `RecordingLibrary`, `DvrStatus`, `ProgressStore`, and `StorageMonitor` changes and fires at most every 500 ms, only when the visible state differs.
- The drawer, storage meter, REC dot, and group counts re-render through `InvokeAsync`; rows/groups use `@key` and skip rendering when their reused `RecordingView`/`StreamerView` instance is unchanged.
- `LibraryView` reuses view-model instances when the underlying values are equal.
- The Mock profile produces started/updated/ended/deleted/storage changes on a timer for bUnit/E2E.
- F8-T3 (open capture live→VOD, deleted recording opens next) is owned by the player ticket; the library raises `Changed` and exposes the updated list.
- F8-T4: while the circuit is reconnecting, a quiet “Live updates paused” note appears in the drawer header; playback continues; after reconnect the drawer renders current state, or the page reloads if the server restarted and the circuit is gone. The template reconnect modal and `ReconnectNotice` follow the foundation rule; the note and modal are never both prominent.
- Components subscribe in `OnInitialized`, dispose subscriptions in `Dispose`, and marshals through `InvokeAsync`.

## 9. Search and keyboard JS

- `SearchField.razor.js` debounces input 250 ms and calls .NET with the trimmed query; it does not call the server per keystroke.
- `shortcuts.js` owns `/` (focus search) and Esc (clear/exit); it ignores editable elements and leaves player shortcuts to the player module.
- `tree-nav.js` owns roving focus, arrow keys, Home/End, and calls .NET only for collapse/expand and Enter activation.
- All modules are plain ES modules under `wwwroot/js`; no Node at runtime.

## 10. Test matrix

bUnit:

- Grouping, recording-first, newest/oldest/A-Z, ties, day headers, empty groups.
- Search/filter/state/sort query parsing; invalid values; no-results.
- Row/group markup, status chips, selected `aria-current`, thumbnails/resume/live bars, rail behavior.
- Collapse persistence, skeleton/empty states, `Changed`/targeted re-render, reconnect note, dispose.
- Keyboard ARIA/roving tabindex with `tree-nav.js` interop mocked.

Vitest:

- `tree-nav.js` movement/Home/End/collapse callbacks/one tab stop; `shortcuts.js` `/`/Esc and editable guards; search debounce.

Playwright:

- Drawer expand/rail/theater; search debounce/highlight/clear; filters; sort; keyboard tree; rail jump; reload restores `sk-ui`/URL; live update/reconnect note; axe clean; keyboard-only.
- Visual regression follows when baselines are available.

No automatic retries; flaky tests are fixed or quarantined per the testing contract.

## 11. Binding versus implementation freedom

Binding: grouping/sort/query/search/filter rules, day-header behavior, state/chip mapping, drawer structure and selected/rail/loading states, keyboard tree and focus rules, all-DOM long-list decision, `Changed`/targeted-render contract, F8-T3/F8-T4 boundary, and JS module ownership.

Freedom: component internals beyond design-system classes, exact skeleton markup, JS implementation, error wording, and test harness.

## 12. Related

- `docs/backend-ui-contract.md` for view models and services.
- `docs/frontend-foundation-contract.md` for the shell, `LibraryView`, preferences, and reconnect.
- `design-system/components/LibraryDrawer`, `StatusChip`, `Buttons`.
- `docs/reviews/implementation-plan-amendments.md` (E-19).
