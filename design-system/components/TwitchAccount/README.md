The app-bar control that connects a Twitch account, shows who is connected, and recovers an expired session. Connected, it is just the avatar; the name is in its menu.

## Build it from

- **Disconnected:** `<MudButton Href="@LoginHref" Variant="Variant.Filled" Class="sk-twitch" StartIcon="@Icons.Material.Rounded.Link">Connect with Twitch</MudButton>`, with the fill restyled to `twitch` (hover `twitch-hover`) and the label in `twitch-contrast-text`. It renders a real link, so it works without the circuit (`data-enhance-nav="false"`).
- **Connecting:** the same button with a 18px spinner and "Connecting…", set by `TwitchAccount.razor.js`.
- **Connected:** a `MudMenu` (`AnchorOrigin="Origin.BottomRight"`, `TransformOrigin="Origin.TopRight"`) whose `ActivatorContent` is **only the avatar**: a 40px round hit area holding a 32px `MudAvatar` (the account's image, or its initial on its channel colour (`channel-*`)), with a `MudBadge Dot Color="Color.Success" Overlap Bordered` at the bottom right meaning "connected". The activator's `aria-label` is "Twitch account: Pixel Wren", so the name is never lost to screen readers.
- **The menu shows who is signed in.** A header (not a menu item): a 40px avatar, the display name in `subtitle1` at Medium, the login in `caption` when it differs, and "Connected to Twitch" beside a `success` dot. A divider, then Manage Twitch connection (`ManageAccounts`), Settings (`Settings`, which opens the settings sheet without leaving the page) and Disconnect (`Logout`, which submits the hidden antiforgery form). The Settings page repeats the name and login in its Twitch account section.
- **Expired:** an error `StatusChip` "Twitch session expired" plus a text button "Reconnect" (`Refresh` icon).

## When the app is private

When `Auth:AllowedTwitchLogins` lists logins, the app is behind the `SignIn` page, so inside it the account is always signed in. Only the connected avatar ever shows; Disconnect is labelled **Sign out** and leads to the sign-in page; the Expired chip never shows, because an expired session goes to the sign-in page instead.

## Rules

- `twitch` purple appears nowhere else. It is told apart from `primary` by its white label and the word Twitch.
- When shipping, replace the `Link` icon with Twitch's official Glitch mark from their brand assets (the same asset as the app bar's Twitch link). Never redraw it.
- No name, login or chevron in the app bar: the avatar alone, at every width.
