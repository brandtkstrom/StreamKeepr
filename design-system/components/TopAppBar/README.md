The `MudAppBar` across the top: 64px on desktop with a labeled library toggle, wordmark, recording tally, search shortcut, settings and the account avatar; `Dense` (48px) below `Breakpoint.Md` with the wordmark, search shortcut and account.

## Build it from

```razor
<MudAppBar Elevation="0" Fixed="true" Dense="@compact" Class="sk-appbar">
    <MudTooltip Text="@(open ? "Close library" : "Open library")">
        <MudButton Variant="Variant.Text" StartIcon="@Icons.Material.Rounded.VideoLibrary"
                   data-drawer-toggle aria-controls="sk-drawer" aria-expanded="@open"
                   Disabled="@theater" OnClick="ToggleLibrary">Library</MudButton>
    </MudTooltip>
    <a class="sk-wordmark" href="/" aria-label="StreamKeepr home">
        <span class="sk-logo"><img src="img/streamkeepr-mark.svg" alt="" width="28" height="28" /></span>
        Stream<b>Keepr</b>
    </a>
    <div class="sk-appbar-actions">
        @if (recordingCount > 0)
        {
            <button class="sk-tally" data-recording-tally aria-label="@($"{count} channels recording. Open the library.")">
                <span class="sk-tally-dot" data-rec-dot aria-hidden="true"></span>@countLabel
            </button>
        }
        <MudIconButton Icon="@Icons.Material.Rounded.Search" data-library-search aria-label="Search library" OnClick="OpenSearch" />
        <MudIconButton Icon="@Icons.Material.Rounded.Settings" Href="/settings" aria-label="Settings" data-settings-toggle />
        <TwitchAccount />
    </div>
</MudAppBar>
```

## Rules

- The app bar is flat: `Elevation="0"` on `appbar-background`, with a `divider` hairline under it. It gains `elevation-4` only after the main column scrolls.
- **Library** is a labeled `MudButton`, not a hamburger: `VideoLibrary` plus the word, a stable accessible name, `aria-expanded` and `aria-controls="sk-drawer"`. It is disabled in theater mode, where the drawer is hidden. Below Md the bottom bar owns Library; the app bar button is CSS-hidden.
- The wordmark leads below the toggle: the mark (28px; 24px dense) and "Stream" in `text-primary` / "Keepr" in `primary`, in `wordmark` type. The logo carries no state and no red.
- **The tally is the one loud element.** A pill on `rec` at 12% tint, a pulsing 8px `rec` dot and sentence case: "2 channels recording" (singular "1 channel recording"), tabular numerals. It opens the library filtered to `state=recording` on the current page, and is wide only; the bottom bar's Library dot carries the state below Md. In theater mode it is a status span, not a button.
- **Search lives in the library**, with its results. The app bar carries only a `Search` icon button: it opens the library and focuses `[data-search-input]`. `/` does the same; `SearchField.razor.js` owns the debounce. The app bar has no search field.
- **Settings** is a real link to `/settings`, so it works before the circuit connects; with the circuit the shell turns the click into the `SettingsSheet` toggle (`aria-expanded`, `aria-controls="sk-settings"`, a `primary` tint while open). On the `/settings` page it is a plain link.
- **The account is an avatar only** (see `TwitchAccount`). The name appears in its menu and on Settings, never in the bar.
- GitHub and Twitch are reference links, not app-bar actions. They live in Settings › About (`Links:GitHub`, `Links:Twitch`, new tab, `rel="noopener"`).
- Disk usage lives at the bottom of the library drawer, not here. There is no capture schedule control.
- **Below `Breakpoint.Md`** it is `Dense` (48px): the wordmark, the `Search` shortcut and the account avatar. Settings moves to `BottomNav`. On a watch page an `ArrowBack` link to Home and the recording title (`subtitle1` at Medium, ellipsised) replace the wordmark. The tally and library toggle are hidden; the bottom bar's Library button carries the live dot.
