The `/settings` page, for bookmarks and direct visits. The app's own Settings controls open the same sections in a sheet instead (`SettingsSheet`), so playback never stops. Signed in, it holds Twitch account, Channels and Connection status, stacked in a column capped at `form-max-width` (760px).

## Build it from

- **The page header:** an exit button, then `MudText Typo.h5` "Settings". The exit is a `MudIconButton Icon="@Icons.Material.Rounded.ArrowBack"` labelled "Exit settings", with a tooltip ("Exit settings (Esc)"). It goes back to the page the viewer came from in StreamKeepr (browser history), or to Home (`/`) when the page was opened directly or from a bookmark. Esc does the same, and so does pressing the app bar's Settings button again. Below `Breakpoint.Md` the dense top bar's back arrow is the exit.
- Then the plain-HTTP `MudAlert Severity.Info Dense` when it applies.
- Sections are `MudPaper Elevation="0" Outlined="true" Class="sk-panel"`, each titled in `h6` after a meaningful icon, `gap-4` apart, in this order:
  1. **Twitch account** (`ManageAccounts`): `MudAvatar Size.Large` filled with the account's channel colour (`channel-*`), the display name in `subtitle1`, the login in `caption`, a `success` dot with "Connected", and an outlined "Disconnect" (a real form post).
  2. **Channels** (`LiveTv`): `ChannelSettings`.
  3. **Connection status** (`Wifi`): `ConnectionStatus`.
- Signed out, a single panel remains: "Connect Twitch to manage channels" with the Twitch button (and the expired chip when the session lapsed).
- Render the sections as shared components (`SettingsSections`), so the page and the sheet never drift.
- The app bar's settings button shows as current (`aria-current="page"`, `primary` tint). Below `Breakpoint.Md`, Settings is current in the bottom bar instead, and the page pads its bottom by `bottom-bar-height`.

Everything maps to today's `Settings.razor`. The app is dark only, so there is no theme setting.
