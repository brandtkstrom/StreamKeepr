# Stream Keepr Library Contract

Status: accepted (2026-11-10)
Source: Implementation plan Part 2, F2/F7/F8; `docs/backend-ui-contract.md`; `docs/frontend-foundation-contract.md`; `design-system/components/LibraryDrawer`, `RecordingRow`, `StatusChip`; plan review E-19; amended for the MudBlazor library drawer (#176, ADR 0012) and the Home rows (#182; `design-system/components/Home`, `RecordingCard`).
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

This contract covers the library drawer, grouping and day headers, search/filter/sort, keyboard navigation, long-list rendering, selection, live updates, and the Home rows at `/` (§10), which are two fixed views of the same library. The player's reaction to live changes belongs to the player ticket; the shared library/status changes are exposed here.

## 2. Data and view models

- `LibraryDrawer` consumes `ILibraryView` (`StreamerView`, `RecordingView`, `StorageView`, `LibraryQuery`). Components never read `RecordingLibrary`, `DvrStatus`, `ProgressStore`, or `StorageMonitor` directly.
- `Streamers` includes channels with at least one recording. A watched channel with zero recordings appears in Settings (`ChannelsView`), not the library. `StreamerView.LastRecordedAt?` and `RecordingCount` remain nullable/zero-safe.
- `RecordingView` state rules:
  - `Recording`: manifest `Recording` and `DvrStatus.ActiveJob.SessionId == Id`.
  - `Reconnecting`: manifest `Recording`, no active job for the session.
  - `Partial`: Complete with more than one part.
  - `Saved`: Complete with one part.
- Status chips: Recording → `Rec`; Reconnecting → `Reconnecting` warning; Partial → `Partial` warning; Saved → no chip. `Processing` exists in the primitive for finalize/export but is not a library state in v3.
- `StatusChip` is a passive `MudChip` (never a control, never in the Tab order): `overline` type, a `tint-opacity` fill of the kind's colour (the neutral Saved kind: 8% `text-primary`, icon `text-secondary`), the label in `text-primary` and only the icon coloured; REC is the one filled chip (`rec`, label `rec-contrast-text`) and its dot stops under reduced motion. A chip changes kind at once, without MudChip's colour fade, so its label never sits on a half-changed fill. Hooks: `data-status` (the kind), `data-status-icon` (the icon name), `data-status-dot` on REC. Callers name icons as Material Symbols names (`check`, `check_circle`, `hourglass_top`, `pause`, `remove`, `schedule`, `sync`, `visibility`, `warning`); an unknown name fails fast. Settings adds one kind, `Healthy` (`data-status="healthy"`: `success` with `CheckCircle`), for a healthy service, a Connected EventSub session, full coverage and the Push chip; Polling is the amber Partial shape with `Schedule` (#179, details-auth-settings contract §8). The expired account chip is the `Error` kind labelled “Twitch session expired” with `role="status"` (#181).
- `Chapters`, `ResumeSec`, `HasChat`, `ThumbnailUrl`, `PlaylistUrl`, `DurationSec`, `SizeBytes`, and counts come from `ILibraryView` as defined.

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
- `q` matches title, category, streamer display name, and login; trimmed, case-insensitive, no regex. Matches in titles and streamer names are highlighted by `MudHighlighter` as `mark.sk-mark`.
- Search is debounced 250 ms in JS before crossing the circuit. The app-bar field updates the URL; `/` focuses it, Esc clears it; ignore the shortcut while typing.
- URL updates use `NavigationManager.NavigateTo(..., replace: true)` so filtering does not fill browser history; a copied URL restores the view.
- Filter chips under the drawer header: a single-select `MudChipSet` (outlined, check mark when selected) of All (`ViewList`), Recording (`FiberManualRecord` in `rec`) and Saved (`Inventory2`); each chip is a `role="button"` with `aria-pressed` and `[data-filter]`. Recording also matches Reconnecting; Saved also matches Partial.
- Empty groups and day headers hide under a filter/search. An empty result shows the drawer's no-match state (`[data-library-no-match]`, design system: EmptyStates, #185): a `Search` icon (`data-empty-icon="search"`), a `subtitle1` headline (`[data-library-empty]`), one `body2` line and one text button. With a search the headline is “No recordings match “{q}””, the line is “Try another title, streamer or category.” and **Clear search** removes only `q`, then focuses the search field while it is shown. On a phone the navigation closes the temporary drawer; if the search row was put away, the drawer hands focus back to its Library button, because a hidden field cannot take focus. With a filter alone the headline is “No recordings match”, the line names the filter (“Nothing is recording right now.” or “No saved recordings.”) and **Show all recordings** removes only `state`, then focuses the All chip. Both go through `LibraryUrl` with `replace: true`, like the chips and the search field.
- Day headers: `Today`, `Yesterday`, then `Wed, Nov 4` in the viewer's zone (`ViewerClock`). Day headers are not focusable items.

## 5. Drawer structure

```text
nav.sk-drawer#sk-drawer
  rail only: Home icon button, divider
  .sk-drawer-head: h2 "Library" (MudText h6), [data-sort] order caption, sort MudMenu ("Library options")
  MudAlert Warning Dense [data-updates-paused] (polite status; shown while reconnecting) with a Retry [data-reconnect-retry]
  MudChipSet .sk-filters ([data-filter] chips)
  [data-drawer-list] [role=listbox] "Channels" [data-channel-list]
    button.sk-channel [role=option] [data-library-channel] (ChannelAvatar, name, meta, count, chevron)
  section.sk-rec-col [data-recordings-column] (the selected channel; Md and up beside the list, below Md over it)
    .sk-rec-col-head (avatar, h3 name, meta; Close, or ArrowBack "All channels" below Md)
    [role=list] [data-column-body]
      .sk-day
      .sk-row-item [role=listitem]: a.sk-row (recording), button [data-row-delete]
  .sk-drawer-foot: StorageMeter ([data-storage]; rail: [data-storage-rail] icon button)
```

- **Channels** (design system: LibraryDrawer): a single-select `listbox` of `option` buttons, replacing the old tree. Each option: a `ChannelAvatar` (`MudAvatar Size.Large`: the channel's image, or its initial in `channel-contrast-text` on its **channel colour**; a `rec` `MudBadge` dot while any item is Recording, with an empty `aria-hidden` `[data-live-ring]` hook beside it); name in `subtitle1`; the meta in `caption` (`[data-channel-meta]`: "Recording now", "Paused", or the day of the latest matching recording); the count of matching recordings (`[data-channel-count]`); a `ChevronRight`. Picking one sets `aria-selected` (on `selection` with a 3px `primary` bar) and opens its **recordings column**. Options do not expand.
- **Recordings column** (`RecordingsColumn`, `[data-recordings-column]`, 360px): the selected channel's header (avatar, name as `h3`, "Recording now · 3 recordings") and its rows under day headers in a `list`. Md and up the whole library opens over the page from the rail, and the column sits to the right of the 320px channels, so the page never reflows; a `Close` icon button ("Close {name}'s recordings") closes it and focuses the channel. Below Md it drills in over the list, with an `ArrowBack` "All channels" button. Picking a recording closes the library; while watching it opens next time on the watched recording's channel (a deep link picks it once the library has loaded). Elsewhere it opens on nothing until a channel is picked, and then on that channel for the session. A channel the search or filter drops closes its column. The open channel is session state in the drawer, not `UiPreferences` (the old collapsed-groups preference is unused).
- **Deleting from a row** (design system: RecordingRow): every row has a `button[data-row-delete]` beside its link ("Delete recording: {title}", a 32px `Delete` icon at the top right, shown on hover, focus in the row, the selected row, and always under `hover: none`; hidden is `opacity: 0`). It opens the shared `DeleteRecordingDialog` (the title block's dialog); Delete on a focused row does the same. When blocked (`RecordingDelete.BlockedReason`: live, exporting; or signed out, "Connect Twitch to use this") it is `aria-disabled` with the reason as its tooltip and `aria-describedby`. It is plain markup with a CSS tooltip, not a `MudIconButton` in a `MudTooltip`, because a column can hold a thousand rows. After a delete the drawer stays open, the counts drop, a snackbar says "Recording deleted" (the watch page says it instead when the watched recording goes), and focus moves to the row in its place, the one before, or Close.
- `RecordingRow`: thumbnail (16:9, `thumb-width`, `surface-raised` placeholder; the `ThumbnailUrl` image as a decorative `<img alt="" loading="lazy">` with `object-fit: cover`, or the `movie` placeholder icon when it is null; duration badge in timecode on `player-scrim`), resume bar in `player-accent` when `ResumeSec` exists, live bar in `rec` while Recording, two-line `subtitle2` title, `caption` meta line, status chip except Saved; hover lays `text-primary` at `hover-opacity` over the row; selected row `aria-current="true"` on `selection` with every line `text-primary`; the row is `<a href="/r/{id}">` and works with or without the circuit.
- **Channel colour**: `ChannelColor.For(login)` picks `channel-1` … `channel-8` by 32-bit FNV-1a over the trimmed, lower-cased login's UTF-8 bytes, mod 8. It belongs to the login, so the drawer, the rail and (later) chat agree and a removed channel's recordings keep it; the pinned values in `ChannelColorTests` must never change. The avatar carries `data-channel-color`.
- `DayHeader`: not focusable (`role="presentation"`).
- `LibraryRail`: a Home icon button and a short divider, then the channel options as 32px avatars (`Size.Medium`) named in a tooltip; the watched channel's avatar ringed in `primary`; a capturing channel's badge is a 14px `rec` dot that pulses like the logo's (still under reduced motion). Clicking an avatar expands the drawer with that channel selected and its column open. The footer shrinks to a `Storage` icon button (`Warning` in `warning` under 10% free) named by its tooltip text; it expands the drawer. `AppShell` cascades the rail state to the drawer (`LibraryRail`).
- The sort menu is a `MudMenu` (`MoreVert`, "Library options") with Newest first, Oldest first and A–Z by streamer as `menuitem`s, the current one marked by a check icon and `aria-current="true"`. In the desktop rail it opens rightward (anchor bottom-left), elsewhere leftward (anchor bottom-right); MudBlazor renders it in the popover provider, so the drawer never clips it. Opening it focuses the current choice (`LibraryDrawer.razor.js`), so one Escape closes it and returns focus to the trigger. The desktop drawer switches between library and rail without a slide, so an opened menu is never placed against a moving trigger.
- **Disk usage footer** (`StorageMeter`, `[data-storage]` `ok|low`): pinned below the channels above a `divider` rule; the `Storage` icon, "1.2 TB free" and "of 4 TB", and a `MudProgressLinear` of the space used in `primary`; under 10% free the icon becomes `Warning` and the figure and bar turn `warning`. `title` gives "Recording disk: 1.2 TB free of 4 TB". While the size is unknown the meter renders nothing (the footer keeps only its rule).
- The drawer lives in the layout and survives navigation; selecting a recording preserves drawer scroll. Changing search/filter/sort resets the list to the top; on load, the selected row scrolls into view.
- Loading: six `MudSkeleton` channel rows (`[data-library-skeleton]`) until `RecordingLibrary.IsLoaded`. Empty library: the drawer says “No recordings yet” and `/` shows `LibraryEmptyState` in the main column (`[data-library-empty-home]`): a 48px `VideoLibrary` icon in `action-default` (`data-empty-icon="video_library"`), an `h4` “No recordings yet”, one `body1` sentence, and one action. Signed in it is a filled “Add a channel” link to `/settings#channels`; signed out it is the Connect with Twitch link instead (`[data-twitch-connect]`, so it shows the top bar's “Waiting for Twitch…”), because adding a channel needs Twitch. No-results: the no-match state above. Neither empty state shows while loading. The `AppShell`/`Home` owns the unknown `/r/{id}` message.

## 6. Keyboard

- The channels are one tab stop (roving `tabindex` on the options); ↑/↓ move, Home/End jump to the first and last.
- Enter, Space or → select the channel (the option's own click, the only call to .NET), open its column and move focus to its first recording.
- In the column ↑/↓ move between recordings and Home/End jump; ← or Escape go back to the selected channel (Escape stops there and does not close a temporary drawer); Enter opens the recording (it is a link); Delete opens its "Delete recording?" unless blocked.
- Escape on the channels is left to the shell, which closes the temporary drawer.
- `library-keys.js` does this on the rendered DOM (replacing `tree-nav.js`). Focus ring is the 2 px `primary`, inset.
- Global `/` focuses search; Esc clears search; typing in an input is never intercepted. The player's arrow shortcuts ignore keys inside the drawer.

## 7. Long lists (F2-T7 revision)

The plan's `<Virtualize>` over a flattened list of variable-height group heads, day headers, and rows breaks focus and scroll behavior. Replace it with:

- The open column renders all of its channel's rows; `.sk-day`/`.sk-row-item` use `content-visibility: auto` and `contain-intrinsic-size` estimates. With two columns only one channel's rows are in the DOM, so the largest list is now a channel's, not the library's.
- Keyboard, focus, `aria-current`, and scroll position work against real DOM.
- Revisit virtualization only if measurements exceed roughly 2,000 flattened items or a real performance issue appears; then use a measured variable-height virtualizer, not a fixed `ItemSize`.

This is recorded as E-19 in the plan-review register.

**Measured 2026-11-11 (#110).** `LibraryScaleJourneyTests` seeds 1,008 recordings (2,019 flattened items: 4 groups, 1,007 day headers, 1,008 rows) and writes `library-scale.txt` on each E2E run. Local Chrome, 1280×820:

| Measure | Result | Budget |
| --- | --- | --- |
| Cold first byte, first page after the app starts (measured 2026-11-12) | 112 ms | 2 s |
| Warm load, all rows and circuit ready (median of 3) | 312 ms (first byte 17 ms, 525 KB HTML) | 2 s |
| Search to one row, including the 250 ms debounce | 281 ms | 2 s |
| Clear search back to all rows (re-renders the whole list) | 115 ms | 3 s |
| State filter to Saved | 71 ms | 3 s |
| Sort to Oldest first, including opening the menu | 159 ms | 3 s |
| Scroll top to bottom in 60 steps, slowest frame | 18.5 ms; no frame over 50 ms | 250 ms |

Timings include Playwright round trips, so they err slow. Budgets are several times the measured values, so they catch a regression without failing on CI jitter. Clearing a search re-renders all 2,019 items, which is also the worst case for a live `Changed` update; the live feed is off during the run so the counts stay fixed. In a one-off run with `content-visibility` removed, the library was ready in 379 ms with the same search and scroll numbers. **E-19 stands:** nothing here calls for virtualization. An earlier run reported about 6 s to the first byte of the first page. That time was spent in Chrome before it connected: it was looking up the Mac's auto-detect proxy (WPAD/PAC). Timed with `curl` right after `/health/ready`, the app answers its first request in about 85 ms in both Debug and Release; the table's 112 ms is the browser's view, which adds Chrome's own request setup. The E2E browser now launches with `--no-proxy-server`.

## 8. Live updates

- `ILibraryView.Changed` coalesces `RecordingLibrary`, `DvrStatus`, `ProgressStore`, and `StorageMonitor` changes and fires at most every 500 ms, only when the visible state differs.
- The drawer (with its disk-usage footer), REC dot, and channel counts re-render through `InvokeAsync`; rows (`RecordingRow`) and channel options (`ChannelOption`) use `@key` and skip rendering when what they show is unchanged. The shell re-renders the drawer on every preference change, so this matters beyond the library: without it, rapid input elsewhere (the chat sync slider) lost key presses.
- `LibraryView` reuses view-model instances when the underlying values are equal.
- The Mock profile produces started/updated/ended/deleted/storage changes on a timer for bUnit/E2E.
- F8-T3 (open capture live→VOD, deleted recording opens next) is owned by the player ticket; the library raises `Changed` and exposes the updated list.
- F8-T4: while the circuit is reconnecting, a quiet “Live updates paused” note appears in the drawer header, with a Retry that `reconnect.js` serves through its `data-reconnect-retry` hook (no Blazor event fires while the circuit is down; #177); playback continues; after reconnect the drawer renders current state, or the page reloads if the server restarted and the circuit is gone. The template reconnect modal and `ReconnectNotice` follow the foundation rule; the note and modal are never both prominent.
- Components subscribe in `OnInitialized`, dispose subscriptions in `Dispose`, and marshals through `InvokeAsync`.

## 9. Search and keyboard JS

- `SearchField.razor.js` debounces input 250 ms and calls .NET with the trimmed query; it does not call the server per keystroke.
- `shortcuts.js` owns `/` (focus search) and Esc (clear/exit); it ignores editable elements and leaves player shortcuts to the player module.
- `library-keys.js` owns the channels' roving focus and the column's keys (§6), and moves focus after a delete once the row is gone and the dialog has closed.
- `LibraryDrawer.razor.js` (collocated) focuses the sort menu's current choice once the popover renders it.
- All modules are plain ES modules (under `wwwroot/js`, or collocated with their component); no Node at runtime.

## 10. Home rows

`Home` renders `HomeRows` at `/` when no recording is open; a watch page (`/r/{id}`) has no rows.

- **Recording now** holds every open session (`state=recording`: `Recording` and `Reconnecting`), never truncated. **Recent recordings** holds the 20 newest complete sessions (`state=saved`: `Saved` and `Partial`). Both come from `ILibraryView.AllRecordings`, which merges every streamer's matching recordings in the library's order (§3: `StartedAt` descending, ties by `Id`).
- The rows re-read the library on `ILibraryView.Changed`, so a session moves from the first row to the second when it completes, without a reload.
- Each row is a `section` labelled by its `h2` (`h6` type): `Sensors` in `rec` and "Recording now" with the count, `History` and "Recent recordings" with "Newest first", a "Show all" text button, and two chevrons (`[data-row-scroll]` `-1`/`1`) that page the row by its visible width through `HomeRows.razor.js` (`scrollRow`; a jump instead of a glide under reduced motion). The chevrons are mouse only: `aria-hidden`, `tabindex="-1"`, absent while loading, and hidden below Md. The cards are a `ul` labelled by the same heading, one `li` per card.
- With no open session, the first row keeps its heading (count 0) and shows one dashed note, "Nothing is recording right now. A watched channel starts recording when it goes live." (`[data-nothing-recording]`). With no recordings at all, no rows render; the "No recordings yet" state is #185's (see §12).
- While `IsLoaded` is false, each row shows three `MudSkeleton` cards (`[data-card-skeleton]`, hidden from assistive technology).
- "Show all" calls `AppShell.ShowLibraryAsync("/?state=saved")` (the shell cascades itself to the page): it navigates and then opens the library (over the page Md and up, the temporary drawer below Md). Outside the shell it only navigates.
- Rows scroll sideways and never wrap (`scroll-snap-type: x`, `space-4` gap); the page itself never scrolls sideways, and the main column has no width cap on Home. Cards are 360px in Recording now and 264px in Recent recordings; below Md (960px) they are 85% and 60% of the screen width (`vw`: there is no persistent drawer below Md).
- `RecordingCard` is a real link to `/r/{id}` (`data-recording-id`, `data-state` = the drawer row's `rec`/`reconnecting`/`partial`/`saved`, from `RecordingStates.Hook`, `data-card-size` = `large`/`default`). Its name comes from its content: the title, then a visually hidden ", streamer, state" ("Late-night speedrun practice, Nightjar TV, recording"). The thumbnail (latest frame, lazy, or `Movie` on `surface-raised`; REC or Reconnecting chip top left; the timecode duration, elapsed while open; a 3px strip, full `rec` while open or `player-accent` to `ResumeSec`) is `aria-hidden`. The body has the decorative `ChannelAvatar` (32px), the title (`subtitle2`, `subtitle1` at Medium on large cards, two lines) and the meta line, which is the link's `aria-describedby`: "Name · Category · Started 9:04 PM" while open, "Name · Yesterday, 8:30 PM" once complete, in the viewer's time zone and the drawer's 12-hour time. A Partial chip sits under the meta line; Saved has none.

## 11. Test matrix

bUnit:

- Grouping, recording-first, newest/oldest/A-Z, ties, day headers, empty groups.
- Search/filter/state/sort query parsing; invalid values; no-results.
- Row/group markup, status chips, selected `aria-current`, thumbnails/resume/live bars, rail behavior, channel colour per login, disk-usage footer and rail storage button.
- Collapse persistence, skeleton/empty states, `Changed`/targeted re-render, reconnect note, dispose.
- Channels listbox ARIA, the column, row Delete (blocked reasons, confirm, focus call), the rail and the drill-in, with `library-keys.js` interop mocked.
- Home rows: row sets and order, the 20 cap, the nothing-recording note, skeletons, a session moving rows on `Changed`, Show all, the chevrons; `RecordingCard` name, chips, meta, strip and sizes; `AppShell.ShowLibraryAsync` wide and compact.

Vitest:

- `library-keys.js` channel and row movement, select and focus-in, back, Delete, focus after delete; `shortcuts.js` `/`/Esc and editable guards; search debounce; `LibraryDrawer.razor.js` focus once present.

Playwright:

- Drawer expand/rail/theater; search debounce/highlight/clear; filters; sort; keyboard channels and column; rail jump; row delete; reload restores `sk-ui`/URL; live update/reconnect note; axe clean; keyboard-only.
- Home: an open session stopped from another tab moves from Recording now to Recent recordings without a reload; at 375 a touch swipe scrolls a row with no sideways page or main-column scroll and the cards are 85%/60% of the screen; at 1280 the chevron pages the row; Show all opens the Saved library at 1280 and 375; axe clean.

Vitest: `HomeRows.razor.js` `scrollRow` (both directions, reduced motion, a missing row).
- No visual regression; it is dropped for now (#106).

No automatic retries; flaky tests are fixed or quarantined per the testing contract.

## 12. Binding versus implementation freedom

Binding: grouping/sort/query/search/filter rules, day-header behavior, state/chip mapping, drawer structure and selected/rail/loading states, keyboard and focus rules, all-DOM long-list decision, `Changed`/targeted-render contract, F8-T3/F8-T4 boundary, JS module ownership, and the Home rows' sets, order, cap, card link and name, and Show all.

Deviations from the Home and RecordingCard cards (#182), also recorded on the cards themselves:

- **Empty library (handoff).** With no recordings, `/` renders only its hidden `h1`: the "No recordings yet" empty state belongs to #185 (PR #194). Merge #194 with or right after this, so a first run never shows a blank Home.
- Show all opens the library; nothing about the drawer is saved.
- The chevrons never disable at the ends of a row (the preview disables the left one at the start); a click at an end does nothing.
- Times use the drawer's 12-hour format rather than the card's 24-hour examples, and the meta line shows the display name rather than the login.
- Headings are `h2` elements in `h6` type, as in the preview, under the page's hidden `h1`.

Freedom: component internals beyond design-system classes, exact skeleton markup, JS implementation, error wording, and test harness.

## 13. Related

- `docs/backend-ui-contract.md` for view models and services.
- `docs/frontend-foundation-contract.md` for the shell, `LibraryView`, preferences, and reconnect.
- `design-system/components/LibraryDrawer`, `StatusChip`, `Buttons`, `Home`, `RecordingCard`.
- `docs/reviews/implementation-plan-amendments.md` (E-19).
