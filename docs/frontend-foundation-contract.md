# Stream Keepr Frontend Foundation Contract

Status: accepted (2026-11-10); amended 2026-11-15 for the MudBlazor foundation (ADR 0012, #173) and the MudBlazor shell (#174)
Source: Implementation plan Part 2, F0/F1; `docs/backend-ui-contract.md`; `docs/hosting-contract.md`; `docs/twitch-auth-contract.md`; `design-system/`; `docs/design-assets.md`.
Binding: yes. Where this contract differs from the plan, this contract wins.

## 1. Scope

The frontend foundation is the Blazor Web App host, routes, app shell, design-system primitives, UI preferences, viewer clock, scoped `LibraryView`, Mock profile, reconnect/auth states, and JS loading. Feature UIs (library rows, player, stats, details, settings) belong to the later frontend tickets.

## 2. Project and rendering

- `src/StreamKeepr/StreamKeepr.csproj` is `Microsoft.NET.Sdk.Web`; one project holds `Components/`, `Ui/`, `Mock/`, and `wwwroot/`, plus `Program.cs` and `appsettings.json`.
- Blazor Web App on .NET 10 with Interactive Server and prerendering.
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
          .sk-main-inner: player slot, details slot, stats slot
      BottomNav (compact or breakpoint not yet known; not on watch pages)
```

- **Breakpoints.** Compact is MudBlazor's `Xs` and `Sm` (below 960px); wide is `Md` and up. `MudBreakpointProvider` reports the breakpoint after the circuit starts and the shell mirrors it as `data-layout="compact|wide"`, adding `data-breakpoint-known` once it has reported. The server renders the wide layout; `components.css` hides the wide-only controls and the mini drawer below 960px, so a phone's first paint already fits.
- **Watch pages** are `/r/{id}` (`LibraryUrl.RecordingId`); the shell marks them `data-watching`.
- **Top app bar**, in order: the menu toggle (wide only; disabled in theater), the wordmark linking to `/` (the logo mark `img/streamkeepr-mark.svg`, then "Stream**Keepr**"; a pulsing `rec` badge `[data-rec-dot]` on the mark while any streamer is recording), the compact search field (360px, 480px while focused), then the actions: a Search icon (compact only), the GitHub and Twitch links, Settings (wide only), and the auth slot last. Compact watch pages replace the wordmark with an `ArrowBack` link to `/` named "Back to Home" and the recording title (`[data-watch-title]`), and hide the GitHub and Twitch links to give the title room (PhoneLayout). There is no capture schedule control.
- **Links.** GitHub (`Icons.Custom.Brands.GitHub`) and Twitch open in a new tab (`target="_blank"`, `rel="noopener"`) with tooltips, named "StreamKeepr on GitHub (opens in a new tab)" and "Twitch (opens in a new tab)". Their URLs come from configuration (`Links:GitHub`, `Links:Twitch`, `LinksOptions`), validated at startup as absolute http(s) URLs. The Twitch icon is `LiveTv` until Twitch's official Glitch mark is added from its brand assets; the mark is never redrawn.
- **Settings** is a real link to `/settings` (the settings sheet is feature F2). Every link in the bars, the wordmark, the back arrow, and Connect with Twitch are real `<a>` elements that work before the circuit connects. The logout `<form>` is a real antiforgery POST in the page, but its trigger, Disconnect in the account menu, needs the circuit.
- **Compact search.** The field is hidden until the Search icon (`aria-expanded`, `aria-controls="sk-search"`) opens it as a full-width row under the bar (`[data-search-open]`). It starts open when the URL has `q`.
- The wordmark returns to the default library home: it clears `q`/`state`/`sort`, closes the selected recording, preserves the drawer mode, and exits theater on the empty home because theater hides the drawer.
- REC badge is lit while any streamer is recording. Disk usage is in the library drawer's footer (`docs/library-contract.md` §5), not the app bar.
- The main column (`main`) is the scroll container, with 24px (`--space-6`) top padding below the app bar on every route, breakpoint, and theater mode.
- **Drawer, wide.** A `Mini` drawer: the 320px library when open, the 72px rail when closed, with the saved `hidden` mode taking it out of the layout. `nav#sk-drawer` carries `data-drawer` (`expanded`, `rail`, `hidden`) and the `rail` class for the library's CSS. The menu toggles expanded ↔ rail, at once rather than with MudDrawer's width slide (see `docs/library-contract.md` §5). `AppShell` cascades `LibraryRail` to the drawer's contents. The choice is remembered per kind of page (§5): Home and other pages open the full library by default, and watch pages show the rail by default. Theater hides the drawer (`data-drawer-frame="hidden"`) and sets the main column to `background-gray`.
- **Drawer, compact.** A `Temporary` drawer over `overlay-dark` at `elevation-4`, always the full library, opened from the bottom bar's Library button; `[data-shell][data-drawer-modal-open]` marks it open. `AppShell.razor.js` moves focus into it, traps Tab, and closes it on Escape, returning focus to the opener; the overlay closes it too; navigating (picking a recording) closes it.
- **Bottom bar (compact, not on watch pages).** A fixed `MudAppBar Bottom` (`bottom-bar-height`, with the safe-area inset) holding `nav[aria-label=Main]`: Home (link to `/`), Library (a button with `aria-controls="sk-drawer"` and `aria-expanded`, and a `rec` dot while anything records) and Settings (link to `/settings`). The current destination has `aria-current="page"`; Library is current while the drawer is open. The main column is padded so nothing hides under it.
- **Phone landscape** (PhoneLayout: below 500px height the top bar hides and the player fills the screen) is part of the watch layout, #175; this shell does not implement it yet.
- The player column order is fixed: player → details → stats. The player slot is wrapped in `.sk-player-row`; an optional chat pane is its second child. The row is two columns at ≥960 px and stacks below 960 px. In two columns the open pane takes the player's height (at least 240 px) and its log scrolls; messages never grow the row, so the sync footer stays put. Stacked, the pane sizes to its content up to 50vh. Short chat is anchored to the bottom of the log and grows upward. The recording main column may grow to 1280 px while chat is open; the library stays at 1100 px. (The watch layout slice, #175, replaces this column.)
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
- Cookie JSON is versioned: `{"v":1,"drawer":"expanded","watch":"rail","theater":false,"stats":false,"details":true,"chat":false,"collapsed":["login"]}`. `watch` is the watch-page drawer; a cookie without it (or with an unknown value) gets the rail.
- Defaults: drawer expanded on Home, rail on watch pages, theater off, stats closed, details open, chat collapsed, no collapsed streamers. `SetDrawer`/`ToggleDrawer` take the page kind; without it they change the Home choice.
- Cookie attributes: `Path=/; SameSite=Lax; Max-Age=31536000`; `Secure` when HTTPS. It is not `HttpOnly` because `prefs.js` writes it. No secrets.
- Cap: 4 KB and at most 50 collapsed streamers; drop extras/oldest so the cookie is never rejected. Unknown fields are ignored.
- Writes go through `prefs.js`; `UiPreferences` updates in memory and raises `Changed`. A reload reads the cookie at prerender, so the first paint matches.
- Layout state is not stored in localStorage. Player preferences (volume, mute, speed, quality) remain localStorage in the player module later. Chat sync offsets are a viewing preference in `sk-chat` localStorage, separate from layout state.
- Components subscribe to `Changed` and re-render through `InvokeAsync`; dispose unsubscribes.

## 6. `ViewerClock` and formatting

- Scoped `ViewerClock` reads the `sk-tz` cookie at prerender; the value is an IANA time-zone id. Invalid/missing falls back to `UTC` and is corrected after the circuit starts.
- `prefs.js` sets `sk-tz` from `Intl.DateTimeFormat().resolvedOptions().timeZone` on first load. A `ViewerClockSync` component calls the module after the first render; if the zone differs, it updates the service/cookie and re-renders once. If JS fails, UTC remains.
- Cookie attributes match `sk-ui`; no secret.
- `ViewerClock` exposes `TimeZone`, `Now`, `ToLocal`, and `Changed`; use `TimeZoneInfo` for DST. Components dispose subscriptions.
- `Format.Timecode` (`1:02:13`), `Duration` (`2h 14m`), `DayHeader` (`Today`, `Yesterday`, `Wed, Nov 4`), and `Bytes` (`9.8 GB`) are pure helpers. The player module later gets the JS timecode twin.

## 7. `LibraryView`

- Scoped `LibraryView : ILibraryView` over `RecordingLibrary`, `DvrStatus`, `ProgressStore`, and `StorageMonitor`; it builds the view models from `docs/backend-ui-contract.md`.
- `Changed` is throttled to at most every 500 ms; subscriptions and the throttle dispose with the circuit.
- `Streamers` includes streamers with at least one recording; `RecordingCount`, `LastRecordedAt?`, `IsWatched`, and current recording/paused state.
- `Recordings` applies state/search/sort with the deterministic rules; `Find` resolves `/r/{id}`. `Storage` reads `StorageMonitor.Current`.
- The state derivation is Recording/Reconnecting/Partial/Saved exactly as the backend-ui contract defines.
- The shell uses `Streamers` for the REC dot and the library drawer uses `Storage` for its footer meter; components never read files or backend stores directly.

## 8. Design-system primitives

All primitives use the design-system `sk-` classes and role tokens; no inline colours or font sizes; accessible names and keyboard behavior are mandatory.

| Primitive | Contract |
|---|---|
| `Button` | `Variant` Filled/Tonal/Outlined/Text/Danger; `Type`, `Disabled`, `OnClick`, `ChildContent`; `AdditionalAttributes`; one Filled action per view; Danger requires confirmation |
| `IconButton` | `Icon`, `Label` (aria-label), `Active`, `Disabled`, `OnClick`; 40px target, 24px glyph |
| `Icon` | Material Symbols name, size 18/20/24, optional fill; decorative by default, labelled when meaningful |
| `StatusChip` | A passive `MudChip` (#176): Rec / Reconnecting / Processing / Partial / Saved / Error; overline label in `text-primary`, icon in the kind's colour, REC filled; REC pulse stops under reduced motion |
| `Avatar` | Image URL or initials; `Name` for alt; recording ring. The library and the account menu use `ChannelAvatar` (channel colour, #176) instead; Settings and Details move to it with their surfaces |
| `Collapsible` | `Open`, `Header`, `ChildContent`, `OnToggle`; `aria-expanded`; 300ms; reduced motion |
| `Switch` | `Checked`, `Label`, `OnChange`; `role="switch"` |
| `Tooltip` | A `MudTooltip` (#177; top, restyled to `tooltip-background`/`tooltip-text`, `caption`, `radius-sm`) around a wrapper that takes the additional attributes. MudTooltip renders its bubble only while shown, so the wrapper is `aria-describedby` a visually hidden `role="tooltip"` copy that is always present. Hover or focus shows it; no interactive content |
| `ConfirmDialog` | A destructive confirmation (#177): an inline `MudDialog` rendered by the `MudDialogProvider` while the owning component keeps its state. `ConfirmDialogs.Options(busy)` sets the Dialog card's options (extra small, full width, Esc and backdrop close only while idle). Title `h6` names the `dialog`; Cancel (text) before the filled Error confirm (`ConfirmLabel`); `Busy` disables both and sets `aria-busy`; `ConfirmDisabled` disables the confirm alone. `HookPrefix="delete"` gives `data-delete-dialog` (on a wrapper inside the content, because MudDialog applies its own attributes only when it opens), `data-delete-cancel` and `data-delete-confirm`. MudBlazor traps focus (its sentinels hand focus back through .NET, so the trap lags by one round trip) and returns it on close. Restyled to `surface`, `radius-lg`, `elevation-24` over `overlay-dark` |
| Snackbars | MudBlazor `ISnackbar` and the `MudSnackbarProvider` in `Routes.razor` (#177). `StreamKeeprSnackbars.Configure` sets the Feedback card's defaults for `Program.cs` and the tests: bottom left (above the phone's bottom navigation), one visible at a time with the rest queued, 4s, duplicates allowed, no close icon. `ISnackbar.Show(text, actionLabel, action)` adds at most one action; a blank text or half an action throws. A snackbar is a polite `alert` |

- `RequiresTwitch`: renders `AuthorizeView`; when signed out, the child stays visible but is wrapped with `inert` and a tooltip “Connect Twitch to use this”; never hidden. The service/UI layer also enforces authorization.
- `/dev/components` is Development-only, shows every primitive against its preview, and is not linked in production.

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
- `ReconnectNotice` is a `MudAlert` Warning Dense (`role="status"`) for “live updates paused” or an SSR/backend error, with a Retry served by `reconnect.js` (`data-reconnect-retry`), like the drawer’s note. It is not shown while the reconnect modal is visible; never both.
- `LibraryView`/`RecordingLibrary.IsLoaded` gates skeleton rows. Empty library shows “No recordings yet”. Unknown `/r/{id}` shows a message and a link back.
- The top bar auth slot uses `TwitchAccount`/`AuthorizeView`: signed out shows Connect with Twitch (link to `/api/auth/twitch/login?returnUrl=<current>`); signed in shows the account avatar, a `MudMenu` activator (see `docs/details-auth-settings-contract.md` §7). Settings and admin actions stay visible but disabled with the `RequiresTwitch` tooltip. Viewing, playback, media, health, and progress remain public.
- The actual OAuth/token flow belongs to the auth ticket; the foundation renders the shell states and the Mock fake.

## 11. JavaScript

- `wwwroot/js/prefs.js` is a plain ES module loaded from `App.razor`; it exposes `window.skPrefs` with cookie read/write and the browser time zone, and sets `sk-tz` on first load.
- `AppShell.razor.js` is a collocated module for the `.scrolled` observer, the compact drawer's focus handling (`setModalDrawer`, Tab trap, Escape → `CloseModalDrawer`), the paused-updates bridge and Retry; it disposes on component dispose. The drawer's open state lives in `AppShell.razor`.
- `UiPreferences`/`ViewerClock` call `prefs.js` through `IJSRuntime`; no JS reads or writes layout state directly.
- `Editor`/player modules load later; hls.js is imported only by the player module, never by the shell.
- `ChatPane.razor.js` owns the chat log, polling, auto-scroll, and sync controls; it listens to `sk:playhead` and never crosses the circuit for time updates.
- Chat scrollback persists while playback runs on. A seek back, a seek more than 120 s ahead, a sync change, or a seek before the kept rows fetches a fresh window from 30 s before the playhead, then backfills earlier chat in 120 s chunks until the log overflows, the chat starts, a chunk hits the limit, or it reaches 30 minutes back. Otherwise each fetch continues from what is loaded: live panes poll every 0.5 s, and a complete recording reads ahead when the playhead is within 30 s of the loaded end. The log keeps the newest 1000 rows; older rows drop off the top.
- `ChatPane.razor.js` also owns optional third-party emote matching and rendering: it fetches the catalog once per recording load, matches only text outside Twitch emote ranges, renders server-provided static `src`/`srcset` images with `loading="lazy"`, `decoding="async"`, and `referrerpolicy="no-referrer"`, and falls back to text on image error. A catalog failure never blocks chat.
- `<ImportMap />` is kept; no CDN and no Node for application assets at runtime. Chat emote images are content and may load from the Twitch and third-party emote CDNs; the pane never imports JavaScript or CSS from a CDN.
- Anything changing more than once a second stays in JS (player/stats/scrubbing/pointer); shell state crosses the circuit only on user actions.

## 12. CSS and design-system source

- `design-system/tokens.json` is the single source of design values. `tokens.css`, `components.css` and `fonts.css` are committed under `src/StreamKeepr/wwwroot/css/`.
- `tokens.css` is generated: `npm run build:tokens` in `tests/js` (or `node scripts/build-tokens.mjs`) writes one `:root` block of the colour, spacing, radius, shadow, size and opacity tokens, `--font-sans`, and a `font` shorthand `--text-<style>` plus `--text-<style>-letter-spacing` per type style. A `{name}` reference becomes `var(--name)`. `tests/js/tokens.test.js` fails when the committed file differs from the generator's output. Never edit `tokens.css` by hand. Node is dev-only and never enters the image.
- Changing a design value means editing `tokens.json`, regenerating `tokens.css` and updating `StreamKeeprTheme.cs`; CI fails if any of the three disagree.
- `components.css` is hand-written and holds today's components. It reads only `--sk-*` aliases, declared in one `:root` block at its top, which point at the `tokens.css` variables: today's surfaces take the MudBlazor palette and Roboto type without markup changes, while radii keep their old values until each surface migrates. Each surface migration stops using its aliases; the block is deleted with `components.css`. Exception: `StatsPanel.razor` and the sparkline modules (`StatsPanel.razor.js`, `sparkline.js`) pass `var(--primary)` and `var(--tertiary)` as series colours, which now resolve straight to the new tokens; network series read indigo until the stats migration moves them to `secondary`.
- MudBlazor's base stylesheet applies app-wide, ahead of `components.css`: a reset on every element (`*{margin:0;padding:0;border-width:0;border-style:solid}`) and bare `body`, `a`, `label` and `button` rules (Roboto `body2` type on `body`, 14px/20.02px; no underline on links). `components.css` restores `letter-spacing: normal` on `body`; everything else it does not style shows the reset. For example, the not-found page's bare `<h1>` and `<p>` (`Routes.razor`) lose their UA margins, and the `<h1>` sizes from the 14px body (28px). `QualityGateJourneyTests` pins the body type, its tracking and link decoration, so a surface PR that changes them updates that test and this list.
- No Google Fonts or other CDN request. `fonts.css` declares Roboto (one variable `woff2`, weights 100–900, Latin subset with `tnum`, `font-display: swap`) for text and Material Symbols Rounded for today's icons. Numeric styles (`timecode`, `stat-value`, `stat-hero`) add `font-variant-numeric: tabular-nums`.
- New shared styles use tokens; no raw hex, px colours, or hard-coded font sizes. Text uses `var(--text-<style>)`.
- The design-system previews in `design-system/components/` are the visual reference; `/dev/components` mirrors them for primitives.

## 13. Test matrix

bUnit:

- Shell layout, drawer modes per page kind, theater, compact layout (through a test `IBrowserViewportService`), bottom bar, top bar slots and links, REC dot.
- `UiPreferences` parse/defaults/cap/write/Changed/dispose; malformed cookie.
- `ViewerClock` cookie/invalid zone/UTC fallback/DST; `Format` values.
- `LibraryView` state/sort/search/query/throttle/dispose; unknown id.
- Primitive markup/ARIA/keyboard; `RequiresTwitch` visible-but-inert; dialog/snackbar.
- Loading/empty/unknown/reconnect notice; Development-only dev page.
- `StreamKeeprTheme` against `design-system/tokens.json` (unit test, no rendering).

Vitest:

- `prefs.js` cookie read/write, `sk-tz` first-load, malformed values, time zone helper.
- `tokens.css` drift against `scripts/build-tokens.mjs` and the generator's output format.

Playwright:

- Drawer expanded → rail → theater; reload preserves `sk-ui`; responsive 960/600.
- At 375×812 with touch: the bottom bar navigates; Library opens the temporary drawer and picking a recording closes it. Axe and no horizontal scroll at 1280 and 375 for Home, a recording and Settings; the bar links work before the circuit connects. At 1280 and 375 the first paint already has the settled bar height and main-column padding.
- Reconnect notice/retry; auth disabled controls; unknown route; `/dev/components`.
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
