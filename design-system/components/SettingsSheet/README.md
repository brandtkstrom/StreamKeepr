Settings opens **over** the current page, never instead of it: a side sheet on desktop and a full-screen dialog on phones. A recording that is playing keeps playing.

## Why

The player lives on the watch page. Navigating to the `/settings` route replaces that page in `MainLayout`, which removes the player and stops playback. So the app's own Settings controls never navigate. They open this sheet, which lives in the layout above whatever page is showing.

## Build it from

**Desktop (Md and up)**: a second drawer in `AppShell`'s `MudLayout`:

```razor
<MudDrawer @bind-Open="_settingsOpen" Anchor="Anchor.End" Variant="DrawerVariant.Temporary"
           ClipMode="DrawerClipMode.Always" Overlay="false" Width="440px" Elevation="24"
           Class="sk-settings-sheet" aria-labelledby="sk-settings-title">
    <SettingsSheetHeader OnClose="CloseSettings" />   @* Settings icon, "Settings" in h6, Close (Esc) *@
    <SettingsSections Compact="true" />               @* the same components the /settings page renders *@
</MudDrawer>
```

- 440px wide, clipped under the app bar, on `surface` with `elevation-24` and a `lines-default` rule on its left edge.
- **No overlay.** The video stays visible on the left, and its controls stay usable, so you can pause or seek while changing a setting. Clicking outside does not close the sheet. The close button and Esc do.
- The header (56px) holds a `Settings` icon, "Settings" in `h6`, and a `Close` icon button. Below it, the three sections scroll as one column, separated by `divider` rules and headed in `subtitle1` at Medium after their icons:
  - **Twitch account** (`ManageAccounts`): avatar, name, login and status, plus Disconnect.
  - **Channels** (`LiveTv`): compact rows with the state chip and a `MoreVert` menu (Stop recording or Pause, Resume, Remove), then the add field.
  - **Connection status** (`Wifi`): key/value rows.

**Phones and tablets (below Md)**: `DialogService.ShowAsync<SettingsSections>(…, new DialogOptions { FullScreen = true, NoHeader = true })`:

- A dense top bar: an `ArrowBack` close button and "Settings". Back (the arrow, the system back gesture or Esc) closes it.
- The same sections, then a **now-playing strip** pinned to the bottom when a recording is playing. It holds a 64px thumbnail with its progress, the title, "streamer · 54:40 · playing", a Pause/Play button and an `ExpandLess` "Back to the video" button. Audio continues underneath.

## Opening and closing

- The app bar's Settings button **toggles** the sheet (`aria-expanded`, `aria-controls="sk-settings"`) and shows pressed while it is open. The account menu's Settings item and the bottom bar's Settings destination open it too.
- Opening it writes `?settings=open` to the URL with `NavigationManager.NavigateTo(…, replace: false)` on the same page, so Back closes it and the state can be shared. This changes only the query string, so the page and the player stay.
- Focus moves into the sheet's header when it opens and returns to the Settings button when it closes. Single-letter player shortcuts are paused while focus is inside the sheet.

## The `/settings` page

It stays, for bookmarks and direct visits, and renders the same `SettingsSections` full width (see `SettingsPage`). Nothing in the app links to it while a recording is open.

## Rules

- Never navigate away from a playing player to show something secondary. Settings, dialogs and menus open over the page.
- The sheet never covers the whole player on desktop. At 440px it leaves the video and its controls on the left.
- Changes take effect immediately, as on the page. The sheet has no Save button.
