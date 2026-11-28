# Stream Keepr Frontend Foundation Contract

Status: accepted (2026-11-10); amended 2026-11-15 for the MudBlazor foundation (ADR 0012, #173) and the MudBlazor shell (#174); amended 2026-11-27 for the one-sheet library drawer
Source: Implementation plan Part 2, F0/F1; `docs/backend-ui-contract.md`; `docs/hosting-contract.md`; `docs/twitch-auth-contract.md`; `design-system/`; `docs/design-assets.md`.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

The frontend foundation is the Blazor Web App host, routes, app shell, design-system primitives, UI preferences, viewer clock, scoped `LibraryView`, Mock profile, reconnect/auth states, and JS loading. Feature UIs (library rows, player, stats, details, settings) belong to the later frontend tickets.

## 2. Project and rendering

- `src/StreamKeepr/StreamKeepr.csproj` is `Microsoft.NET.Sdk.Web`; one project holds `Components/`, `Ui/`, `Mock/`, and `wwwroot/`, plus `Program.cs` and `appsettings.json`.
- Blazor Web App on .NET 11 with Interactive Server and prerendering.
- `App.razor` is the static document shell: `HeadOutlet`, `<ImportMap />`, the template reconnect UI, and the CSS links.
- `Routes.razor` is rendered with `@rendermode="InteractiveServer"`; one interactive boundary for the shell and page. `Home.razor` handles both routes.
- `MapStaticAssets()` serves CSS/JS/fonts; `MapRazorComponents<App>().AddInteractiveServerRenderMode()` maps the UI. No `UseHttpsRedirection`; the proxy terminates TLS.
- CSS order in `App.razor`: `_content/MudBlazor/MudBlazor.min.css`, `tokens.css`, `fonts.css`, `components.css`, using `@Assets[...]` fingerprinted URLs, so the app's CSS wins over MudBlazor's on equal specificity. No Google Fonts, no CSS isolation for shared styles, no CSS-in-JS.
- Script order: `blazor.web.js`, then `_content/MudBlazor/MudBlazor.min.js`, then the app modules.
- MudBlazor is pinned to exactly `9.12.0` (ADR 0012); each upgrade is its own PR.
- Node is dev-only. The runtime image has no Node; JS is plain ES modules.

## 2a. MudBlazor theme and providers

- `Program.cs` calls `AddMudServices(options => StreamKeeprSnackbars.Configure(options.SnackbarConfiguration))`, so the snackbar defaults in §8 apply app-wide.
- `Routes.razor`, the one interactive boundary, renders `MudThemeProvider` (`Theme="StreamKeeprTheme.Theme"`, `IsDarkMode="true"`), `MudPopoverProvider`, `MudDialogProvider` and `MudSnackbarProvider` once, before the `Router`. The app is dark only: there is no theme preference, and `PaletteLight` keeps MudBlazor's defaults, unused.
- `Ui/StreamKeeprTheme.cs` is hand-written from `design-system/tokens.json`: the `PaletteDark` values, MudBlazor's default `Typography` with the one override `Button.TextTransform = "none"`, and `LayoutProperties` (8px radius, 64px app bar, 320px drawer, 72px rail). `StreamKeeprThemeTests` compares every Palette value, the layout sizes and every MudBlazor type style with `tokens.json`, and fails when the theme sets a palette value other than MudBlazor's default that no token describes.
- Components name MudBlazor types fully qualified (`MudBlazor.MudThemeProvider`) until `@using MudBlazor` can go into `_Imports.razor`: the app's own `SnackbarService` that once made it ambiguous is gone (#177), and adding the using is left to the cleanup (#181).

## 3. Routing and query state

- `/` and `/r/{RecordingId}` are handled by `Home.razor`; `RecordingId` is optional.
- Library query state lives in the URL: `?q=` search, `?state=` filter, `?sort=` sort. Invalid or missing values fall back to no search, no filter, `newest`.
- `Home` resolves the selected recording through `ILibraryView.Find`; an unknown id shows a message and a link back, not a 500.
- Navigation uses `NavigationManager`; filters/search/sort update the URL and the `LibraryView` query without a full reload.

## 4. App shell

MudBlazor's Layout 2 wireframe (design system: **AppShell**, **TopAppBar**, **BottomNav**, **TwitchAccount**, **PhoneLayout**), in `Components/Layout/AppShell.razor`:

```text
MudBreakpointProvider
  [data-shell] (data-layout, data-breakpoint-known, data-watching, data-theater-mode, data-drawer-frame, data-drawer-modal-open)
    MudLayout
      MudAppBar (header; Dense below Md)
      MudDrawer (ClipMode.Always; Mini on Md and up, Temporary below)
        nav#sk-drawer[aria-label=Library][data-drawer]
      MudMainContent
        ReconnectNotice
        main
          .sk-main-inner: Home rows/Settings, or the /r/{id} watch view
                          (player, title block, Chapters, stats + chat drawer; below Md: player, summary, tabs)
      BottomNav (compact or breakpoint not yet known; not on watch pages)
```

- **Breakpoints.** Compact is MudBlazor's `Xs` and `Sm` (below 960px); wide is `Md` and up. `MudBreakpointProvider` reports the breakpoint after the circuit starts and the shell mirrors it as `data-layout="compact|wide"`, adding `data-breakpoint-known` once it has reported. The server renders the wide layout; `components.css` hides the wide-only controls and the mini drawer below 960px, so a phone's first paint already fits.
- **Watch pages** are `/r/{id}` (`LibraryUrl.RecordingId`); the shell marks them `data-watching`.
- **Top app bar**, in order: the labeled Library toggle (wide only; a `MudButton` with `VideoLibrary` and a stable accessible name, `aria-expanded`, `aria-controls="sk-drawer"`; disabled in theater), the wordmark linking to `/` (the logo mark `img/streamkeepr-mark.svg`, then "Stream**Keepr**"; the logo carries no state and no red), the recording tally (wide only; `[data-recording-tally]` with a pulsing `rec` dot `[data-rec-dot]`, "N channels recording" in sentence case, tabular numerals; opens the library filtered to `state=recording` on the current page; a status span in theater), then the actions: a Search icon (`[data-library-search]`; opens the library and focuses its search field), Settings (wide only), and the auth slot last. Compact watch pages replace the wordmark with an `ArrowBack` link to `/` named "Back to Home" and the recording title (`[data-watch-title]`); the library toggle and tally are hidden below 960px. There is no capture schedule control.
- **Links.** GitHub (`Icons.Custom.Brands.GitHub`) and Twitch live in Settings › About, not the app bar. They open in a new tab (`target="_blank"`, `rel="noopener"`) and are named "StreamKeepr on GitHub (opens in a new tab)" and "Twitch (opens in a new tab)". Their URLs come from configuration (`Links:GitHub`, `Links:Twitch`, `LinksOptions`), validated at startup as absolute http(s) URLs. The Twitch icon is Twitch's Glitch mark from `wwwroot/img/twitch-glitch.svg`, used unmodified in its supplied full colour; the mark is never redrawn.
- **Settings** is a real link to `/settings`. With the circuit it toggles the settings sheet over the current page instead (`aria-expanded`; `aria-controls="sk-settings"` while it is open; a `primary` tint while open; `docs/details-auth-settings-contract.md` §8a, #183), except on `/settings` itself. Every link in the bars, the wordmark, the back arrow, and Connect with Twitch are real `<a>` elements that work before the circuit connects. The logout `<form>` is a real antiforgery POST in the page, but its trigger, Disconnect in the account menu, needs the circuit.
- **Search** lives in the library toolbar at every width. The app bar's Search icon and `/` open the library and focus `[data-search-input]`; Esc clears it. There is no app-bar search field and no `[data-search-open]` row. A query-only change (search, filter, sort) keeps the library open at every width, including the temporary drawer, so typing does not dismiss the field.
- The wordmark returns to the default library home: it clears `q`/`state`/`sort`, closes the selected recording, and exits theater on the empty home because theater hides the drawer.
- **Live capture** is the app bar tally wide and the bottom bar Library dot compact. Disk usage is in the library drawer's footer (`docs/library-contract.md` §5), not the app bar.
- The main column (`main`) is the scroll container, with 24px (`--space-6`) top padding below the app bar on every route and breakpoint; theater mode on a watch page and phone landscape drop the gutters (player contract §2a).
- **Drawer, wide.** A `Mini` drawer that is the 72px rail on every page. The Library toggle, a rail avatar, the rail's storage button, Home's "Show all", the app bar search shortcut, or the tally opens the full library **over** the page (design system: LibraryDrawer): one sheet that widens to 320px of channels plus a `clamp(360px, 28vw, 440px)` recordings pane while the main column keeps the rail's margin, so the page never reflows; there is no backdrop. Before a channel is picked, the pane shows the "Choose a channel" prompt; an empty or no-match library stays 320px. A rail avatar opens the drawer on that channel and clears any retained search/state first, so a stale filter cannot hide the channel picked from the unfiltered rail. The open library is temporary and never saved: its Close button, Escape (not while a menu, popover or dialog takes it, so a row's "Delete recording?" closes alone), a press outside it (`AppShell.razor.js`; the rest of the app bar, Settings included, is outside; not outside are the Library toggle, search trigger, tally, drawer, and a MudBlazor popover or dialog), or a path change closes it, while a query-only change (a filter chip, the search, the sort) keeps it open. Focus is not trapped. `nav#sk-drawer` carries `data-drawer` (`expanded` while open, else `rail`) and the `rail` class; the shell carries `data-library-open`. The Library toggle keeps the stable name "Library" with `aria-expanded`. `AppShell` cascades `LibraryRail`, `LibraryOpen` (open at any width) and itself to the drawer (which calls `OpenLibrary()`/`CloseModalDrawer()`), and itself to the page, whose Home "Show all" calls `ShowLibraryAsync(url)`, opening the library once the navigation lands (`docs/library-contract.md` §10). Theater hides the drawer, rail included (`data-drawer-frame="hidden"`), and sets the main column to `background-gray`. There is no saved drawer preference; an older cookie's `drawer` and `watch` values are read and ignored.
- **Drawer, compact.** A `Temporary` drawer over `overlay-dark` at `elevation-4`, always the full library, opened from the bottom bar's Library button or the app bar search shortcut; `[data-shell][data-drawer-modal-open]` marks it open. `AppShell.razor.js` moves focus into it (the selected/first channel, or the search field when search opened it), traps Tab, and closes it on Escape, returning focus to the opener; the overlay closes it too; a path change (picking a recording) closes it, while a query-only change keeps it open.
- **Bottom bar (compact, not on watch pages).** A fixed `MudAppBar Bottom` (`bottom-bar-height`, with the safe-area inset) holding `nav[aria-label=Main]`: Home (link to `/`), Library (a button with `aria-controls="sk-drawer"` and `aria-expanded`, and a `rec` dot while anything records) and Settings (link to `/settings`, which opens the settings dialog over the page with the circuit, with `aria-expanded`, #183). The current destination has `aria-current="page"`; Library is current while the drawer is open, and Settings while the settings dialog is open. The main column is padded so nothing hides under it.
- **Phone landscape** (PhoneLayout): below 500px height the top bar hides and the player fills the screen (player contract §2a, #175).
- The watch layout (player contract §2a, #175, #332): Md and up the content is one column — player → title block → Chapters → stats — with the open chat in a 400px right drawer fixed from the app bar's bottom to the viewport's bottom; the watch page has no max width, while the library keeps its 1100px. Below Md the player is edge to edge above `RecordingDetailsSummary`, the export row and the Chat/Chapters/Stats tabs.
- Collapse/expand follows MudBlazor's drawer transition; `prefers-reduced-motion` turns the shell's transitions and the REC pulse off.
- Every interactive element has the 2px `primary` focus ring. New shell styles read `tokens.css` variables, not the legacy `--sk-*` aliases.

## 5. `UiPreferences`

```csharp
public enum DrawerMode { Expanded, Rail, Hidden }

public sealed class UiPreferences
{
    public DrawerMode Drawer { get; }        // Home and other pages
    public DrawerMode WatchDrawer { get; }   // watch pages (/r/{id})
    public DrawerMode DrawerFor(bool watching);
    public bool Theater { get; }
    public bool StatsOpen { get; }
    public bool DetailsOpen { get; }
    public bool ChatOpen { get; }
    public bool IsCollapsed(string streamerLogin);
    public event Action? Changed;
}
```

- Scoped per circuit; reads the `sk-ui` cookie at prerender through `IHttpContextAccessor`; missing/malformed values fall back to defaults.
- Cookie JSON is versioned: `{"v":1,"theater":false,"stats":false,"details":true,"chat":false,"chatSync":0,"viewers":true,"chatShowBadgeText":true,"chatFontScale":100,"chatFontWeight":400}`. `stats` is the Statistics overlay, `viewers` the Viewers panel. `chatSync` is the viewer's chat sync offset in seconds (Settings › Chat), the same for every recording, clamped to ±`ChatReader.MaxSyncOffsetSeconds` and snapped to `UiPreferences.ChatSyncStepSeconds` (0.5 s, also the slider's step); a cookie without it gets 0, so it needs no version bump. `chatShowBadgeText` is the viewer's "Show badges" preference (Settings › Chat), on by default; it shows or hides badge images and the text chips that stand in for unresolved badges in chat rows. The C# property is `UiPreferences.ChatShowBadges`; the cookie key stays `chatShowBadgeText` for compatibility. A cookie without it gets `true`, so it needs no version bump either. `chatFontScale` is the chat log's font size as a percentage of `body2` (Settings › Chat), 85–145 in steps of 5, default 100, clamped and snapped; `chatFontWeight` is the message text weight, one of 400/500/700, default 400. A cookie without either gets its default, so neither needs a version bump. The keys version 1 used to carry for the tree library and its remembered drawer (`drawer`, `watch`, `collapsed`) are read and ignored, and no longer written.
- Defaults: theater off, stats closed, details open, viewers open, chat collapsed, chat sync 0, badges on, font scale 100, font weight 400.
- Cookie attributes: `Path=/; SameSite=Lax; Max-Age=31536000`; `Secure` when HTTPS. It is not `HttpOnly` because `prefs.js` writes it. No secrets.
- The state is a few fixed fields, far under the 4 KB cookie limit, which `prefs.js` still enforces. Unknown fields are ignored.
- Writes go through `prefs.js`; `UiPreferences` updates in memory and raises `Changed`. A reload reads the cookie at prerender, so the first paint matches.
- Layout state is not stored in localStorage. Player preferences (volume, mute, speed, quality) remain localStorage in the player module later. The chat sync offset is part of this cookie (`chatSync`); the `sk-chat` localStorage per-recording offsets are no longer read or written.
- Components subscribe to `Changed` and re-render through `InvokeAsync`; dispose unsubscribes.

## 6. `ViewerClock` and formatting

- Scoped `ViewerClock` reads the `sk-tz` cookie at prerender; the value is an IANA time-zone id. Invalid/missing falls back to `UTC` and is corrected after the circuit starts.
- `prefs.js` sets `sk-tz` from `Intl.DateTimeFormat().resolvedOptions().timeZone` on first load. A `ViewerClockSync` component calls the module after the first render; if the zone differs, it updates the service/cookie and re-renders once. If JS fails, UTC remains.
- Cookie attributes match `sk-ui`; no secret.
- `ViewerClock` exposes `TimeZone`, `Now`, `ToLocal`, and `Changed`; use `TimeZoneInfo` for DST. Components dispose subscriptions.
- `Format.Timecode` (`1:02:13`), `Duration` (`2h 14m`), `DayHeader` (`Today`, `Yesterday`, `Wed, Nov 4`), `Bytes` (`9.8 GB`), and `Percent` (a resume strip's CSS width, `33.3%`, clamped to 0–100%, `0%` without a positive whole) are pure helpers. The player module later gets the JS timecode twin.

## 7. `LibraryView`

- Scoped `LibraryView : ILibraryView` over `RecordingLibrary`, `DvrStatus`, `ProgressStore`, and `StorageMonitor`; it builds the view models from `docs/backend-ui-contract.md`.
- `Changed` is throttled to at most every 500 ms; subscriptions and the throttle dispose with the circuit.
- `Streamers` includes streamers with at least one recording; `RecordingCount`, `LastRecordedAt?`, `IsWatched`, and current recording/paused state.
- `Recordings` applies state/search/sort with the deterministic rules; `Find` resolves `/r/{id}`. `Storage` reads `StorageMonitor.Current`.
- The state derivation is Recording/Reconnecting/Partial/Saved exactly as the backend-ui contract defines.
- The shell uses `Streamers` for the REC dot and the library drawer uses `Storage` for its footer meter; components never read files or backend stores directly.

## 8. Design-system primitives

Generic controls are plain MudBlazor components (`MudButton`, `MudIconButton`, `MudIcon`…); the hand-built `Button`, `IconButton`, `Icon`, `Collapsible` and `Switch` primitives were removed with the Material Symbols webfont (#181). The primitives left in `Components/Primitives/` use the design-system role tokens (`ConfirmDialog`, `Tooltip`, `StatusChip` and snackbars are MudBlazor components restyled in `components.css`); no inline colours or font sizes; accessible names and keyboard behavior are mandatory. Icons are `Icons.Material.Rounded` SVGs: `MudIcon` in Blazor markup, `IslandIcon` and `wwwroot/js/island-icons.js` where a module owns the DOM.

| Primitive | Contract |
|---|---|
| `StatusChip` | A passive `MudChip` (#176): Rec / Reconnecting / Processing / Partial / Saved / Error, plus the Settings-only Healthy (`success`, `CheckCircle`; #179); overline label in `text-primary`, icon in the kind's colour, REC filled; REC pulse stops under reduced motion |
| `Avatar` | Image URL or initials; `Name` for alt; recording ring. The library, the account menu and Settings (#179) use `ChannelAvatar` (channel colour, #176) instead; Details moves to it with its surface |
| `Tooltip` | A `MudTooltip` (#177; top, restyled to `tooltip-background`/`tooltip-text`, `caption`, `radius-sm`) around a wrapper that takes the additional attributes. MudTooltip renders its bubble only while shown, so the wrapper is `aria-describedby` a visually hidden `role="tooltip"` copy that is always present. Hover or focus shows it; no interactive content |
| `ConfirmDialog` | A destructive confirmation (#177): an inline `MudDialog` rendered by the `MudDialogProvider` while the owning component keeps its state, with `ConfirmDialogOptions.Value` (extra small, full width, no close button). Title `h6` names the `dialog`; focus lands on Cancel (`DefaultFocus.FirstChild`); Cancel (text) before the filled Error confirm (`ConfirmLabel`); `Busy` disables both and sets `aria-busy`; `ConfirmDisabled` disables the confirm alone. MudBlazor 9.12 renders an open dialog with the options it was shown with, so MudBlazor's own Escape closing is off and `ConfirmDialog` closes on Escape (from its content) and the backdrop itself, only while idle. Its content wrapper holds the body and the buttons and carries the hooks: `HookPrefix="delete"` gives `data-delete-dialog`, `data-delete-cancel` and `data-delete-confirm` (MudDialog applies its own attributes only when it opens). MudBlazor traps focus (its sentinels hand focus back through .NET, so the trap lags by one round trip) and returns it on close. Restyled to `surface`, `radius-lg`, `elevation-24` over `overlay-dark` |
| Snackbars | MudBlazor `ISnackbar` and the `MudSnackbarProvider` in `Routes.razor` (#177). `StreamKeeprSnackbars.Configure` sets the Feedback card's defaults for `Program.cs` and the tests: bottom left (above the phone's bottom navigation), one visible at a time with the rest queued, 4s, duplicates allowed, no close icon. `ISnackbar.Show(text, actionLabel, action)` adds at most one action; a blank text or half an action throws. A snackbar is a polite `alert` |

- `RequiresTwitch`: renders `AuthorizeView`; when signed out, the child stays visible but is wrapped with `inert` and a tooltip “Connect Twitch to use this”; never hidden. The service/UI layer also enforces authorization. In private mode (non-empty allowlist, ADR-0013) no signed-out visitor reaches the app, so the signed-out branch never renders there.

## 9. Mock profile

- `Mock` launch profile (`dotnet watch --launch-profile Mock`) registers `MockSeeder`, `MockPoller`, fake auth, and the fake Streamlink; it runs without Twitch credentials.
- `MockSeeder`: four streamers, eight recordings (finished VOD, two-part partial, growing EVENT capture), thumbnails, chapters, and a selected recording.
- `MockPoller`/fake EventSub: deterministic observations and push notifications; the growing capture uses `fake-streamlink`.
- Fake auth: a signed-in admin by default, with a documented signed-out toggle for E2E.
- Mock data lives in a temp/development recordings root; it never touches production data or secrets.
- The Mock profile is dev/test only; production uses the real recorder/poller/auth.

## 10. Reconnect, loading, empty, and auth states

- `App.razor` keeps the framework-owned reconnect `<dialog id="components-reconnect-modal">` (`ReconnectModal.razor`) for a hard circuit loss. It must work while the circuit is down, so it stays a plain `<dialog>` with no MudBlazor components or Blazor events, styled like a MudDialog (design system Reconnect card; #177): `surface`, `radius-lg`, `elevation-24` over `overlay-dark`, about 360px wide. It has one section per hard loss (`[data-reconnect-state]`), shown by `[data-reconnect-shown]`, which `reconnect.js` sets: a 32px icon (MudBlazor's `WifiOff`/`Pause`/`Error` SVG, written into the static document), an `h2` title in `h6` type, one `body1` sentence in `text-secondary`, then the actions:
  - `failed`: “Couldn’t rejoin the server” — Reload · Retry;
  - `paused`: “Session paused” — Resume;
  - `resume-failed`: “Couldn’t resume the session” — Reload · Resume.
- `reconnect.js` mirrors the state onto `body[data-sk-reconnect]`, opens the dialog only for those three states (a brief `show`/`retrying` never does), names it by the shown state's title (`aria-labelledby`), reloads on `rejected`, and serves every reconnect button through one capturing document click listener: `[data-reconnect-retry]` (`Blazor.reconnect()`, then `resumeCircuit()`, else reload; if reconnecting throws, Retry in an alert reloads the page while the modal's Retry waits for the tab to become visible), `[data-reconnect-resume]` and `[data-reconnect-reload]`.
- `UpdatesPausedAlert` is “Live updates paused”: a `MudAlert` Warning Dense (`role="status"`) with a Retry served by `reconnect.js` (`data-reconnect-retry`). The drawer’s note and `ReconnectNotice` (shown for `sk:updates-paused` or an SSR/backend error with its own message) both render it. `ReconnectNotice` is not shown while the reconnect modal is visible; never both.
- `LibraryView`/`RecordingLibrary.IsLoaded` gates skeleton rows. Empty library shows “No recordings yet” with one action (`docs/library-contract.md`). Unknown `/r/{id}` (`[data-unknown-recording]`) leads with a `Search` icon (`data-empty-icon="search"`), then an `h1` in `h4` type, “Recording not found”, and “That recording is not in the library.” with a “Back to library” link to `/`.
- The top bar auth slot uses `TwitchAccount`/`AuthorizeView`: signed out shows Connect with Twitch (link to `/api/auth/twitch/login?returnUrl=<current>`); signed in shows the account avatar, a `MudMenu` activator (see `docs/details-auth-settings-contract.md` §7). Settings and admin actions stay visible but disabled with the `RequiresTwitch` tooltip. Viewing, playback, media, health, and progress remain public.
- The actual OAuth/token flow belongs to the auth ticket; the foundation renders the shell states and the Mock fake.

## 11. JavaScript

- `wwwroot/js/prefs.js` is a plain ES module loaded from `App.razor`; it exposes `window.skPrefs` with cookie read/write and the browser time zone, and sets `sk-tz` on first load.
- `AppShell.razor.js` is a collocated module for the `.scrolled` observer, the settings links (a capture-phase click listener that turns a plain click on a link to `/settings` from another page into `ToggleSettingsAsync`; `docs/details-auth-settings-contract.md` §8a), the compact drawer's focus handling (`setModalDrawer`: focus moves in on open unless it is already in the drawer, as after → on a rail avatar, and back to the trigger on close, but only if focus is still in the drawer or was lost with it, so an action that moved focus on keeps it; Tab trap; Escape → `CloseModalDrawer`) and the paused-updates bridge (`sk:updates-paused`, the reconnect modal's visibility); Retry belongs to `reconnect.js`; it disposes on component dispose. The drawer's open state lives in `AppShell.razor`.
- Every JS→.NET call goes through `callDotNet(reference, method, ...args)` from `wwwroot/js/circuit.js`, never `invokeMethodAsync` directly. Blazor does not handle its own send while the circuit's connection is down, so a direct call then logs an `Uncaught (in promise)` error that no caller can catch, and its promise never settles. `circuit.js` follows `components-reconnect-state-changed` (only `hide` means connected) and rejects with `CircuitDownError` while down, so the caller's own catch handles it.
- `UiPreferences`/`ViewerClock` call `prefs.js` through `IJSRuntime`; no JS reads or writes layout state directly.
- `Editor`/player modules load later; hls.js is imported only by the player module, never by the shell.
- `ChatPane.razor.js` owns the chat log, polling and auto-scroll, fills the newest rows with the endpoint's `tail=true` query, and applies the chat sync offset .NET sends it; it listens to `sk:playhead` and never crosses the circuit for time updates.
- The log keeps its row elements between renders: new chat is appended, dropped scrollback is removed from the top, and only a fresh tail, a state change, a new emote catalog, a channel login or colour change, or a new pane background redraws every row.
- Gap rows ("Chat unavailable for …") show once the playhead reaches them, as messages do, so the newest row is always last and new chat appends at the end.
- While following, new chat at the end glides into view (instantly under `prefers-reduced-motion: reduce`). The glide is the pane's own animation-frame loop, a critically damped spring on the distance to the end, not a native smooth scroll: rows that arrive mid-glide move its end without resetting its speed, and it never overshoots. A fresh tail, a seek of more than 2 s ahead within the kept window, backfill adding older rows above, an end more than one log height away, and "Jump to current time" land at the end instantly. Scroll events from the pane's own glide never stop following. Wheel-up, a touch drag, a press on the scrollbar, or ArrowUp/PageUp/Home/Shift+Space on the log hands scrolling back to the viewer, and the 24 px near-bottom rule applies again from then on; clicks on rows and other keys keep following.
- Chat scrollback persists while playback runs on, until playback (re)starts or the pane reopens. A paused → playing transition, a playhead move the pane classifies as a seek (more than 2 s), and opening a closed pane set following, clear the unseen count, and land instantly on the current row; a pause, a rate change, and ordinary playhead ticks leave the log alone. A seek back, a seek more than 120 s ahead, a sync change, or a seek before the kept rows fetches a fresh newest-N tail at the playhead, then backfills earlier chat from the tail's oldest row in 120 s chunks until the log overflows, the chat starts, a chunk hits the limit, or it reaches 30 minutes back from the tail's oldest row. The tail is authoritative for its returned range: rows it omits there (deleted, cleared or redacted) leave the log, while older scrollback outside the range stays. Otherwise each fetch continues from what is loaded: live panes poll the tail at the playhead every 0.5 s, and a complete recording reads ahead with the non-tail window query when the playhead is within 30 s of the loaded end. The log keeps the newest 1000 rows; older rows drop off the top. While following there is no footer element at all; scrolling away renders it, and a load-error notice sits in its own row between the log and footer so an error never keeps the footer alive.
- `ChatPane.razor.js` also owns optional third-party emote matching and rendering: it fetches the catalog once per recording load, matches only text outside Twitch emote ranges, renders server-provided static `src`/`srcset` images with `loading="lazy"`, `decoding="async"`, and `referrerpolicy="no-referrer"`, and falls back to text on image error. A catalog failure never blocks chat.
- `ChatPane.razor.js` renders absolute `http(s)` URLs in message and reply text as anchors: the visible text stays as typed minus trailing sentence punctuation and an unmatched closing bracket, each opens in a new tab (`target="_blank"`, `rel="noopener noreferrer nofollow"`), and only explicit `http`/`https` schemes link; `www.` hosts, bare domains, and other schemes stay text. Message text is never parsed as HTML.
- `<ImportMap />` is kept; no CDN and no Node for application assets at runtime. Chat emote images are content and may load from the Twitch and third-party emote CDNs; the pane never imports JavaScript or CSS from a CDN.
- Anything changing more than once a second stays in JS (player/stats/scrubbing/pointer); shell state crosses the circuit only on user actions.

## 12. CSS and design-system source

- `design-system/tokens.json` is the single source of design values. `tokens.css`, `components.css` and `fonts.css` are committed under `src/StreamKeepr/wwwroot/css/`.
- `tokens.css` is generated: `npm run build:tokens` in `tests/js` (or `node scripts/build-tokens.mjs`) writes one `:root` block of the colour, spacing, radius, shadow, size and opacity tokens, `--font-sans`, and a `font` shorthand `--text-<style>` plus `--text-<style>-letter-spacing` per type style. A `{name}` reference becomes `var(--name)`. `tests/js/tokens.test.js` fails when the committed file differs from the generator's output. Never edit `tokens.css` by hand. Node is dev-only and never enters the image.
- Changing a design value means editing `tokens.json`, regenerating `tokens.css` and updating `StreamKeeprTheme.cs`; CI fails if any of the three disagree.
- `components.css` is hand-written: the app's rules over MudBlazor components and the hand-built islands. New rules read `tokens.css` directly. A few older rules still read `--sk-*` aliases, declared in one `:root` block at its top and pointed at the `tokens.css` variables; the block keeps only aliases with a reader (#181), and an alias goes with its last reader. Exception: the library row's Delete tooltip is a CSS `::after` on `data-tip` (`RecordingRow.razor`), not the `Tooltip` primitive, because a channel's column can hold a thousand rows and a `MudTooltip` per row is too heavy; it uses the same tooltip tokens. Exception: `StatsPanel.razor` and the sparkline modules (`StatsPanel.razor.js`, `sparkline.js`) pass `var(--primary)` and `var(--tertiary)` as series colours, which now resolve straight to the new tokens; network series read indigo until the stats migration moves them to `secondary`.
- MudBlazor's base stylesheet applies app-wide, ahead of `components.css`: a reset on every element (`*{margin:0;padding:0;border-width:0;border-style:solid}`) and bare `body`, `a`, `label` and `button` rules (Roboto `body2` type on `body`, 14px/20.02px; no underline on links). `components.css` restores `letter-spacing: normal` on `body`; everything else it does not style shows the reset. For example, the not-found page's bare `<h1>` and `<p>` (`Routes.razor`) lose their UA margins, and the `<h1>` sizes from the 14px body (28px). `QualityGateJourneyTests` pins the body type, its tracking and link decoration, so a surface PR that changes them updates that test and this list.
- No Google Fonts or other CDN request. `fonts.css` declares Roboto (one variable `woff2`, weights 100–900, Latin subset with `tnum`, `font-display: swap`) for text, and nothing else: there is no icon font. Numeric styles (`timecode`, `stat-value`, `stat-hero`) add `font-variant-numeric: tabular-nums`.
- New shared styles use tokens; no raw hex, px colours, or hard-coded font sizes. Text uses `var(--text-<style>)`. Exception: the chat log's font size and weight are viewer preferences applied as CSS custom properties on the pane root (`--sk-chat-font-scale`, `--sk-chat-font-weight`), not design tokens; the log's size is `calc(0.875rem * var(--sk-chat-font-scale) / 100)` and its weight `var(--sk-chat-font-weight)`. The badge chip's size is `max(10px, 0.75rem, calc(0.75rem * var(--sk-chat-font-scale) / 100))`; the badge image's height is `max(10px, 18px, calc(18px * var(--sk-chat-font-scale) / 100))`; both grow above 100% and never shrink, with a 10px floor. All other text uses `var(--text-<style>)`.
- The design-system previews in `design-system/components/` are the visual reference.

## 13. Test matrix

bUnit:

- Shell layout, the rail and the library over the page (opening, closing, not saved), theater, compact layout (through a test `IBrowserViewportService`), bottom bar, top bar slots and links, REC dot.
- `UiPreferences` parse/defaults/write/Changed/dispose; malformed cookie; an older version's keys ignored and not written back.
- `ViewerClock` cookie/invalid zone/UTC fallback/DST; `Format` values.
- `LibraryView` state/sort/search/query/throttle/dispose; unknown id.
- Primitive markup/ARIA/keyboard; `RequiresTwitch` visible-but-inert; dialog/snackbar.
- Loading/empty/unknown/reconnect notice; Development-only dev page.
- `StreamKeeprTheme` against `design-system/tokens.json` (unit test, no rendering).

Vitest:

- `prefs.js` cookie read/write, `sk-tz` first-load, malformed values, time zone helper.
- `tokens.css` drift against `scripts/build-tokens.mjs` and the generator's output format.

Playwright:

- Library opens over the page without reflow and closes on Escape, outside press and navigation; theater; reload preserves `sk-ui`; responsive 960/600.
- At 375×812 with touch: the bottom bar navigates; Library opens the temporary drawer and picking a recording closes it. Axe and no horizontal scroll at 1280 and 375 for Home, a recording and Settings; the bar links work before the circuit connects. At 1280 and 375 the first paint already has the settled bar height and main-column padding.
- Reconnect notice/retry; auth disabled controls; unknown route.
- The home page loads self-hosted Roboto and requests nothing off-origin; first-paint JS stays within the 200 KB gzipped budget with MudBlazor's script.
- axe clean and keyboard-only use on the shell. Visual regression is dropped for now (#106).

No automatic retries; flaky tests are fixed or quarantined per the testing contract.

## 14. Binding versus implementation freedom

Binding: project/render model, routes and query keys, shell structure/breakpoints, the `data-*` shell hooks and accessible names in §4, cookie names/schema/defaults/caps, clock behavior, `LibraryView` interface/throttle/state rules, primitive APIs/ARIA, Mock profile behavior, reconnect/auth states, JS module loading, and CSS order.

Freedom: component internals, exact markup beyond the design-system classes, JS helper style, error wording, and test implementation.

## 15. Related

- `docs/backend-ui-contract.md` for view models and services.
- `docs/hosting-contract.md` for host/middleware/CSS assets.
- `docs/twitch-auth-contract.md` for sign-in and auth states.
- `docs/testing-contract.md` for test layers and CI.
- `docs/design-assets.md` and `design-system/` (`README.md`, `mudblazor-theme.md`, `component-map.md`) for tokens, components, previews, and fonts.
- `docs/adr/0012-mudblazor-incremental-migration.md` for the migration plan.
