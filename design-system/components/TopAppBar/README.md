The `MudAppBar` across the top: 64px on desktop with the drawer toggle, wordmark, recording search, the GitHub and Twitch links, settings and the account avatar; `Dense` (48px) below `Breakpoint.Md`.

## Build it from

```razor
<MudAppBar Elevation="0" Fixed="true" Class="sk-appbar">
    <MudIconButton Icon="@Icons.Material.Rounded.Menu" aria-label="@MenuLabel" OnClick="ToggleDrawer" Disabled="Theater" />
    <a class="sk-wordmark" href="/" aria-label="StreamKeepr home"><span class="sk-logo @(RecordingActive ? "rec" : null)"><img src="streamkeepr-mark.svg" alt="" width="28" height="28" /></span>Stream<b>Keepr</b></a>
    <SearchField />  @* MudTextField Variant.Outlined Margin.Dense Adornment.Start, 360px max (480px while focused), centred *@
    <MudSpacer />
    <MudTooltip Text="StreamKeepr on GitHub">
        <MudIconButton Icon="@Icons.Custom.Brands.GitHub" Href="@Links.GitHub" Target="_blank" Rel="noopener" aria-label="StreamKeepr on GitHub (opens in a new tab)" />
    </MudTooltip>
    <MudTooltip Text="Twitch">
        <MudIconButton Icon="@TwitchGlitch" Href="@Links.Twitch" Target="_blank" Rel="noopener" aria-label="Twitch (opens in a new tab)" />
    </MudTooltip>
    <MudIconButton Icon="@Icons.Material.Rounded.Settings" Href="/settings" aria-label="Settings" aria-controls="@(open ? "sk-settings" : null)" aria-expanded="@expanded" />   @* toggles the sheet; never leaves the page *@
    <TwitchAccount />   @* avatar only; signed out, the Connect with Twitch button *@
</MudAppBar>
```

## Rules

- The app bar is flat: `Elevation="0"` on `appbar-background`, with a `divider` hairline under it.
- The logo leads: the mark (`streamkeepr-mark.svg`, 28px; 24px dense), then the wordmark in `wordmark` type, "Stream" in `text-primary` and "Keepr" in `primary`. While anything records, a pulsing `rec` badge sits on the mark's top-right corner.
- The search field is compact: up to 360px wide and 36px tall (`Margin.Dense`), centred in the bar. It widens to 480px while focused, over 200ms, and shrinks back on blur. The bar's spare width stays empty rather than stretching the field.
- Search placeholder: "Search recordings, streamers, categories". It filters the drawer live (`Immediate`, `DebounceInterval="150"`).
- **Links:** GitHub opens the project's repository, and Twitch opens twitch.tv. Both open in a new tab (`Target="_blank"`, `Rel="noopener"`), are 40px icon buttons in `action-default` with a tooltip, and read their URLs from configuration (`Links:GitHub`, `Links:Twitch`), not from code.
- **The GitHub icon** is MudBlazor's own `Icons.Custom.Brands.GitHub`. **The Twitch icon** must be Twitch's official Glitch mark, taken unmodified from Twitch's brand assets and kept single-colour in `action-default`. MudBlazor has no Twitch icon, and the mark is never redrawn. Until the asset is added, the previews show `Icons.Material.Rounded.LiveTv` as a stand-in.
- **Settings toggles the side sheet** (`SettingsSheet`) over the current page, so a playing recording keeps playing. It is a real link to `/settings`, so it works before the circuit connects; with the circuit the shell turns the click into the toggle (`aria-expanded`, `aria-controls="sk-settings"` while open, a `primary` tint while open). On the `/settings` page it is a plain link.
- **The account is an avatar only** (see `TwitchAccount`). The name appears in its menu and on the Settings page, never in the bar.
- Disk usage lives at the bottom of the library drawer, not here (see `LibraryDrawer`). There is no capture schedule control.
- **Below `Breakpoint.Md`** it is `Dense` (48px): the wordmark, a `Search` icon button that expands into a full-width search row, the GitHub and Twitch links (except on a watch page, where the title needs the room; see `PhoneLayout`), and the account avatar. Settings moves to the bottom bar (`BottomNav`). On a watch page, an `ArrowBack` button to Home and the recording title (`subtitle1` at Medium, ellipsised) replace the wordmark.
