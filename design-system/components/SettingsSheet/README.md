Settings opens **over** the current page, never instead of it: a side sheet on desktop and a full-screen dialog on phones. A recording that is playing keeps playing.

## Why

The player lives on the watch page. Navigating to the `/settings` route replaces that page in `MainLayout`, which removes the player and stops playback. So the app's own Settings controls never leave the page. They open this sheet, which lives in the layout above whatever page is showing.

## Build it from

`SettingsSheet` (`Components/Features/Settings`) sits in `AppShell`'s `MudLayout`. `MainLayout` passes it the shared `SettingsSections` (the same components the `/settings` page renders) and the open recording, for the strip.

**Desktop (Md and up)**: a second drawer:

```razor
<MudDrawer Open="@open" Anchor="Anchor.End" Variant="DrawerVariant.Temporary"
           ClipMode="DrawerClipMode.Always" Overlay="false" Width="440px" Elevation="24"
           Class="sk-settings-sheet" id="sk-settings" aria-labelledby="sk-settings-title">
    @* header: Settings icon, "Settings" (h2 in h6), Close (Esc) *@
    <CascadingValue Name="SettingsSheet" Value="true"><SettingsSections /></CascadingValue>
</MudDrawer>
```

- 440px wide, clipped under the app bar, on `surface` with `elevation-24` and a `lines-default` rule on its left edge. It is an `aside` labelled "Settings".
- **No overlay.** The video stays visible on the left, and its controls stay usable, so you can pause or seek while changing a setting. Clicking outside does not close the sheet. The close button and Esc do.
- The header (56px) holds a `Settings` icon, "Settings" as an `h2` in `h6`, and a `Close` icon button ("Close settings", `aria-keyshortcuts="Escape"`, tooltip "Close (Esc)" on hover only: opening moves focus to it, and a focus tooltip would pop on every open, in the popover layer outside any landmark). Below it, the sections scroll as one column. Inside the sheet, `SettingsSection` (told by the `SettingsSheet` cascade) drops the outlined paper for `divider` rules and heads each section with an `h3` in `subtitle1` at Medium after its icon:
  - **Twitch account** (`ManageAccounts`): avatar, name, login and status, plus Disconnect.
  - **Channels** (`LiveTv`): the same rows as the page (state chip, push chip, Stop recording or Pause, Resume, Remove), wrapping at 440px, then the add field.
  - **Connection status** (`Wifi`): key/value rows.

**Phones and tablets (below Md)**: an inline `MudDialog` in the same component, shown while the sheet is open, with `DialogOptions { FullScreen = true, NoHeader = true, CloseOnEscapeKey = false, BackdropClick = false }` and `AriaLabelledBy="sk-settings-title"`:

- A dense top bar: an `ArrowBack` button ("Close settings") and "Settings" (`h2`). The arrow, the system back gesture (Back) or Esc closes it.
- The same sections, then a **now-playing strip** (`role="group"`, "Now playing") pinned to the bottom on a watch page. It holds a 64px thumbnail with its progress (in `primary`; `rec` stays reserved for live capture), the title, "streamer · 54:40 · playing" (or "paused"), a Pause/Play button and an `ExpandLess` "Back to the video" button, which closes the sheet. Audio continues underneath.
- The strip follows the player's `sk:playhead` events (formatting time with `/js/timecode.js`, never loading the player module, so opening Settings off a recording page loads no player code) and plays or pauses by clicking the player's own Play button, so the player stays the only code that drives the `<video>`. `SettingsSheet.razor.js` owns its time, state and progress.

## Opening and closing

- The app bar's Settings control, the bottom bar's Settings destination and the account menu's Settings item are real links to `/settings`, so they reach the page before the circuit connects. With the circuit, `AppShell.razor.js` claims a plain click on a link to the plain `/settings` (no query or fragment) from any other page and **toggles** the sheet instead (a capture-phase listener that prevents the default; a .NET location-changing handler would make Blazor replay every browser Back). New tabs, modified clicks, code, browser history and deep links into a section (`/settings#twitch`, `/settings#channels`) still reach the page.
- The app bar's and the bottom bar's Settings carry `aria-expanded`, and `aria-controls="sk-settings"` while the sheet is open (it is mounted only then); the app bar's shows a `primary` tint while the sheet is open, and the bottom bar marks Settings `aria-current="page"`. On the `/settings` page they are plain links, with no `aria-expanded`.
- Opening writes `?settings=open` to the URL with `NavigationManager.NavigateTo(…)` (a new history entry) on the same page, so Back closes it and the state can be shared. Only the query string changes, so the page and the player stay; a fragment (`#…`) is dropped, as `LibraryUrl` drops it. Other query keys (the library's `q`, `state`, `sort`) are kept.
- Closing an entry this circuit pushed is `history.back()`, so Back never reopens it; a shared or reloaded `?settings=open` is replaced instead. Going to another page (picking a recording, Home) closes it, since the new URL has no key. The sheet never opens on the `/settings` page, which ignores the key.
- Focus moves to the sheet's Close button (the back arrow on phones) when it opens, and returns to the control that opened it, else the first visible Settings toggle (the app bar's, or the bottom bar's on a phone), else the player (a phone's watch page has neither), when it closes, unless the viewer has already moved focus elsewhere (the desktop sheet leaves the player usable). Single-letter player shortcuts are paused while focus is inside the sheet (`[data-settings-sheet]` in `player-keys.js`); with focus on the player beside it they work.
- Esc closes the sheet while focus is inside it, except in a text field, on a key repeat, with a modifier held, or while a confirmation, menu or popover opened from it is open.

## The `/settings` page

It stays, for bookmarks and direct visits, and renders the same `SettingsSections` full width (see `SettingsPage`). The app's Settings controls open the sheet instead. The account menu's "Manage Twitch connection" still links to `/settings#twitch`, so it is the one control that leaves a playing recording (out of #183's scope).

## Rules

- Never navigate away from a playing player to show something secondary. Settings, dialogs and menus open over the page.
- The sheet never covers the whole player on desktop. At 440px it leaves the video and its controls on the left.
- Changes take effect immediately, as on the page. The sheet has no Save button.

## Later

Not in #183: compact channel rows with a `MoreVert` menu (Stop recording or Pause, Resume, Remove) inside the sheet, and opening "Manage Twitch connection" in the sheet at its section.
