The `/settings` page, for bookmarks and direct visits. The app's own Settings controls open the same sections in a sheet instead (`SettingsSheet`), so playback never stops. Signed in, it holds Twitch account, Channels and Connection status, stacked in a column capped at `form-max-width` (760px).

## Build it from

- **The page header:** an exit button (`SettingsExit`), then `MudText Typo.h5` "Settings" as the page's `h1`. The exit is a `MudIconButton Icon="@Icons.Material.Rounded.ArrowBack"` labelled "Exit settings" (`aria-keyshortcuts="Escape"`), with a tooltip ("Exit settings (Esc)") on a tight root. It goes back through browser history to the StreamKeepr page the viewer came from, or to Home (`/`) when the page was opened directly, from a bookmark, or after any full-page load (returning from Twitch sign-in, Disconnect). Esc does the same, except while typing in a field, with a modifier held, while a dialog, menu or other popover is open, or when the key repeats. One exit at a time: a double click or Esc with the button goes back once.
- Then the plain-HTTP `MudAlert Severity.Info Dense` (`role="note"`) when it applies.
- Sections are `SettingsSection`s: `MudPaper Elevation="0" Outlined="true" Class="sk-panel"` holding a `section` labelled by its `h2` (`MudText Typo.h6`) after a meaningful icon, `gap-4` apart, in this order:
  1. **Twitch account** (`ManageAccounts`): the account's `ChannelAvatar` (`Size.Large`, its login's channel colour `channel-*`), the display name in `subtitle1`, the login in `caption`, a `success` dot with "Connected", and an outlined "Disconnect" (a real form post).
  2. **Channels** (`LiveTv`): `ChannelSettings`.
  3. **Connection status** (`Wifi`): `ConnectionStatus`.
- Signed out, a single panel remains: "Connect Twitch to manage channels" with the Twitch button (and the expired chip when the session lapsed).
- Render the note, the sections and the signed-out panel as one shared component (`SettingsSections`), so the page and the sheet never drift. Each section is its own component, so the sheet can place them.
- Below `Breakpoint.Md`, Settings is current in the bottom bar, and the page pads its bottom by `bottom-bar-height`.

Everything maps to `Settings.razor`. The app is dark only, so there is no theme setting.

## Rules

- "Previous page" means a page this tab's StreamKeepr session (the Blazor circuit) has shown; only a change of path counts. A viewer who opens Settings directly, goes Home, then uses the browser's Back button and exits leaves the app: accepted.

## Later (not built in #179)

- Pressing the app bar's Settings button again exits.
- The app bar's Settings button shows as current (`aria-current="page"`, `primary` tint) on Md and up.
- Below `Breakpoint.Md`, the dense top bar's back arrow is the exit.
