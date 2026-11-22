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
- **Two columns.** The left column holds, top to bottom, the player, the title block (`RecordingDetails`: title and meta on the left, the actions in a vertical stack on the right, outside any panel), `Chapters` (only when the recording has chapters) and `Viewers` (only when there are viewer samples), `space-4` apart. Stats for nerds is no longer a panel here: it is an overlay inside the player (`StatsPanel`). It takes the remaining width (`flex: 1`), and the player in it keeps 16:9.
- **Chat is the right column**, when the recording has chat and the pane is open: `flex: 0 1 400px` with a 320px minimum, so on wide screens it widens instead of leaving empty space. It takes **all the vertical space** of the view: from the top of the main column to the bottom of the viewport (`100dvh − appbar-height − 2 × space-4`), `position: sticky` at the top, so it stays put while the left column scrolls. Nothing sits beside it on the right; everything else stays to its left. Closed, it is not shown and the left column takes the full width.
- The player's height is capped at `100dvh − appbar-height − 2 × space-6 − player-peek`, so the title block's first line always shows below it without scrolling.
- The drawer is the 72px rail while watching: Home, then 32px streamer avatars (the open one ringed in `primary`, a live one with the pulsing `rec` dot; see `LibraryDrawer`), then the disk icon at the foot. The menu button opens the full 320px library over the same clipped area.

## The consumer provides

- `Body` from the routed page.
- The drawer state, theater mode and the chat open state, persisted through `UiPreferences`.
- `RecordingActive`, which shows the `rec` dot before the wordmark.

## Rules

- Settings opens over the page (`SettingsSheet`), never by navigating, so the player is never unmounted while it plays.
- Nothing sits above the player. In theater mode the player spans the full width of the view, and the two columns move below it (see `TheaterMode`).
- Below `Breakpoint.Md` the layout is `PhoneLayout`: dense top bar, bottom bar, edge-to-edge player and tabs.
- Keep `MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider` and `MudSnackbarProvider` above the layout, rendered once.
