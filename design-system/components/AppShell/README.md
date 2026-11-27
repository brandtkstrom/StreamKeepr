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

- The main column is padded `space-6` at the top, right and bottom and `space-4` at the left (beside the rail), with no max-width.
- **One content column.** It holds, top to bottom, the player, the title block (`RecordingDetails`: title and meta on the left, the live chip and More actions menu at the top right, outside any panel), `Chapters` (only when the recording has chapters) and `Viewers` (only when there are viewer samples), `space-4` apart. Statistics is no longer a panel here: it is an overlay inside the player (`StatsPanel`). The player keeps 16:9 at the content width.
- **Chat is a docked right drawer**, when the recording has chat and the pane is open: `chat-width` (400px) wide, fixed from the app bar's bottom to the viewport's bottom, flush with the right edge. Its surface is the flat `drawer-background` with one 1px left divider, square corners, and no shadow or inset. While it is open the main column reserves its width, so the player and the title block end 24px to its left; opening and closing are instant, with no animation. Closed, the drawer leaves no width, strip, rail or gap, and the content takes the full column.
- The player's height is capped at `100dvh − appbar-height − 2 × space-6 − player-peek`, so the title block's first line always shows below it without scrolling.
- The drawer is the 72px rail while watching: Home, then 32px streamer avatars (the open one ringed in `primary`, a live one with the pulsing `rec` dot; see `LibraryDrawer`), then the disk icon at the foot. The menu button opens the full library over the same clipped area: 320px of channels, widening to 680px with the current channel's recordings column. Clicking a rail avatar opens it on that channel.

## The consumer provides

- `Body` from the routed page.
- The drawer state, theater mode and the chat open state, persisted through `UiPreferences`.
- `RecordingActive`, which shows the `rec` dot before the wordmark.

## Rules

- Settings opens over the page (`SettingsSheet`), never by navigating, so the player is never unmounted while it plays.
- Nothing sits above the player. In theater mode the player spans the view to the open chat drawer's divider, and the content below keeps 16px side padding; the chat drawer keeps its docked geometry (see `TheaterMode`).
- Escape closes the library first. The chat drawer yields Escape to an open library, dialog, menu, popover or settings sheet, so the library closes and chat stays open; a second Escape then closes chat (see `ChatPane`, #333).
- Below `Breakpoint.Md` the layout is `PhoneLayout`: dense top bar, bottom bar, edge-to-edge player and tabs.
- Keep `MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider` and `MudSnackbarProvider` above the layout, rendered once.
