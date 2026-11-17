The whole StreamKeepr screen, built on MudBlazor's Layout 2 wireframe (an app bar with a drawer clipped beneath it), responsive from phone to desktop. The card shows the desktop watch view.

## Build it from

```razor
<MudBreakpointProvider OnBreakpointChanged="bp => _compact = bp is Breakpoint.Xs or Breakpoint.Sm">
<MudLayout Class="sk-app">
    <MudAppBar Elevation="0" Fixed="true" Dense="_compact" Class="sk-appbar"> … TopAppBar … </MudAppBar>

    <MudDrawer @bind-Open="_drawerOpen"
               Variant="@(_compact ? DrawerVariant.Temporary : DrawerVariant.Mini)"
               ClipMode="DrawerClipMode.Always" Elevation="0" Class="sk-drawer" aria-label="Library">
        <LibraryDrawer Mini="!_compact && !_drawerOpen" />
    </MudDrawer>

    <MudMainContent Class="@($"sk-main {(Theater ? "theater" : null)}")">
        @Body   @* Home rows at /, the watch view at /r/{id}, Settings at /settings *@
    </MudMainContent>

    @* The end drawer Md and up, a full-screen MudDialog below; open while the URL has ?settings=open *@
    <SettingsSheet Open="@SettingsOpen" Compact="@_compact" NowPlaying="@NowPlaying" OnClose="CloseSettingsAsync">
        @SettingsContent   @* MainLayout passes SettingsSections *@
    </SettingsSheet>

    @* Not MudHidden: it renders nothing until the circuit knows the breakpoint, so a phone's
       prerender would lose the bar's links. CSS hides the bar at 960px and up. *@
    @if (!Watching && !_wideKnown) { <BottomNav /> }   @* MudAppBar Bottom *@
</MudLayout>
</MudBreakpointProvider>
```

## The watch view on desktop (Md and up)

- `MudMainContent` has `pa-4` and no max-width.
- The first row is the player plus, when the recording has chat and it is open, the chat sidecar. The player takes the remaining width (`flex: 1`) at 16:9. The sidecar is `flex: 0 1 400px` with a 320px minimum, so on wide screens it widens instead of leaving empty space.
- The player's height is capped at `100dvh − appbar-height − 2 × space-4 − player-peek`. The recording title, its actions and its meta line (`RecordingDetails`, expanded by default) sit directly below, always visible without scrolling. Stats for nerds follows.
- The drawer is the 72px rail while watching: Home, then streamer avatars (the open one ringed in `primary`), then the disk icon at the foot. The menu button opens the full 320px library over the same clipped area.

## The consumer provides

- `Body` from the routed page.
- The drawer state, theater mode and the chat open state, persisted through `UiPreferences`.
- `RecordingActive`, which shows the `rec` dot before the wordmark.

## Rules

- Settings opens over the page (`SettingsSheet`), never by navigating, so the player is never unmounted while it plays.
- Nothing sits above the player. In theater mode the player spans the full width of the view, and chat moves below it (see `TheaterMode`).
- Below `Breakpoint.Md` the layout is `PhoneLayout`: dense top bar, bottom bar, edge-to-edge player and tabs.
- Keep `MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider` and `MudSnackbarProvider` above the layout, rendered once.
