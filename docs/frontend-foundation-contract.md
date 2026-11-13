# Stream Keepr Frontend Foundation Contract

Status: accepted (2026-11-10)
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
- CSS order in `App.razor`: `tokens.css`, `fonts.css`, `components.css`, using `@Assets[...]` fingerprinted URLs. No Google Fonts, no CSS isolation for shared styles, no CSS-in-JS.
- Node is dev-only. The runtime image has no Node; JS is plain ES modules.

## 3. Routing and query state

- `/` and `/r/{RecordingId}` are handled by `Home.razor`; `RecordingId` is optional.
- Library query state lives in the URL: `?q=` search, `?state=` filter, `?sort=` sort. Invalid or missing values fall back to no search, no filter, `newest`.
- `Home` resolves the selected recording through `ILibraryView.Find`; an unknown id shows a message and a link back, not a 500.
- Navigation uses `NavigationManager`; filters/search/sort update the URL and the `LibraryView` query without a full reload.

## 4. App shell

Use the design system's `.sk-shell` structure:

```text
.sk-shell (column)
  .sk-appbar
  .sk-shell-body (row)
    .sk-drawer#sk-drawer
    main.sk-main
      .sk-main-inner (max 1100px, centred)
        player slot
        details slot
        stats slot
```

- Top app bar: menu toggle, type-only wordmark linking to `/` with REC dot, search slot, storage meter, schedule stub, settings, auth slot last.
- The wordmark returns to the default library home: it clears `q`/`state`/`sort`, closes the selected recording, preserves the drawer mode, and exits theater on the empty home because theater hides the drawer.
- REC dot is lit while any streamer is recording; storage meter turns `warning` below 10% free.
- The main column has 24px (`--space-6`) top padding below the app bar on every route, breakpoint, and theater mode; it scrolls with the content.
- Drawer: expanded 320px, rail 72px, hidden. The menu toggles expanded ↔ rail. Theater hides the drawer and sets the main column to `surface-container-lowest`.
- Below 960px the drawer is modal over `scrim` with `elevation-3`, opened from the menu; below 600px stats tiles stack (stats implementation later).
- The player column order is fixed: player → details → stats. The layout provides slots/placeholders; the feature tickets fill them.
- Collapse/expand is 300ms with the design-system easing; honor `prefers-reduced-motion`.
- Every interactive element has the 2px `primary` focus ring; use `sk-` classes and role tokens only.

## 5. `UiPreferences`

```csharp
public enum DrawerMode { Expanded, Rail, Hidden }

public sealed class UiPreferences
{
    public DrawerMode Drawer { get; }
    public bool Theater { get; }
    public bool StatsOpen { get; }
    public bool DetailsOpen { get; }
    public bool IsCollapsed(string streamerLogin);
    public event Action? Changed;
}
```

- Scoped per circuit; reads the `sk-ui` cookie at prerender through `IHttpContextAccessor`; missing/malformed values fall back to defaults.
- Cookie JSON is versioned: `{"v":1,"drawer":"expanded","theater":false,"stats":false,"details":true,"collapsed":["login"]}`.
- Defaults: drawer expanded, theater off, stats closed, details open, no collapsed streamers.
- Cookie attributes: `Path=/; SameSite=Lax; Max-Age=31536000`; `Secure` when HTTPS. It is not `HttpOnly` because `prefs.js` writes it. No secrets.
- Cap: 4 KB and at most 50 collapsed streamers; drop extras/oldest so the cookie is never rejected. Unknown fields are ignored.
- Writes go through `prefs.js`; `UiPreferences` updates in memory and raises `Changed`. A reload reads the cookie at prerender, so the first paint matches.
- Layout state is not stored in localStorage. Player preferences (volume, mute, speed, quality) remain localStorage in the player module later.
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
- The shell uses `Streamers` for the REC dot and `Storage` for the meter; components never read files or backend stores directly.

## 8. Design-system primitives

All primitives use the design-system `sk-` classes and role tokens; no inline colours or font sizes; accessible names and keyboard behavior are mandatory.

| Primitive | Contract |
|---|---|
| `Button` | `Variant` Filled/Tonal/Outlined/Text/Danger; `Type`, `Disabled`, `OnClick`, `ChildContent`; `AdditionalAttributes`; one Filled action per view; Danger requires confirmation |
| `IconButton` | `Icon`, `Label` (aria-label), `Active`, `Disabled`, `OnClick`; 40px target, 24px glyph |
| `Icon` | Material Symbols name, size 18/20/24, optional fill; decorative by default, labelled when meaningful |
| `Chip` | Filter chip; `Selected`, `OnClick`, `ChildContent`; `aria-pressed`, check icon |
| `StatusChip` | Rec / Processing / Partial / Saved / Error; uppercase label; REC pulse stops under reduced motion |
| `Avatar` | Image URL or initials; `Name` for alt; recording ring |
| `Collapsible` | `Open`, `Header`, `ChildContent`, `OnToggle`; `aria-expanded`; 300ms; reduced motion |
| `Switch` | `Checked`, `Label`, `OnChange`; `role="switch"` |
| `Menu` / `MenuItem` | Anchored menu; keyboard navigation, Esc/outside close, focus return; `role="menu"`/`menuitem` |
| `Tooltip` | Hover/focus text; `aria-describedby`; no interactive content |
| `Dialog` | `<dialog>` + `showModal()` via JS; focus trap/Esc; return focus; title, content, actions |
| `Snackbar` | Scoped `SnackbarService` queue, one action, auto-dismiss; `SnackbarHost` in the shell |

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

- `App.razor` keeps the Blazor template reconnect modal for a hard circuit loss.
- `ReconnectNotice` is a non-modal banner for “live updates paused” or an SSR/backend error, with Retry. It is not shown while the reconnect modal is visible; never both.
- `LibraryView`/`RecordingLibrary.IsLoaded` gates skeleton rows. Empty library shows “No recordings yet”. Unknown `/r/{id}` shows a message and a link back.
- The top bar auth slot uses `TwitchAuthButton`/`AuthorizeView`: signed out shows Connect with Twitch (link to `/api/auth/twitch/login?returnUrl=<current>`); signed in shows the account chip. Settings and admin actions stay visible but disabled with the `RequiresTwitch` tooltip. Viewing, playback, media, health, and progress remain public.
- The actual OAuth/token flow belongs to the auth ticket; the foundation renders the shell states and the Mock fake.

## 11. JavaScript

- `wwwroot/js/prefs.js` is a plain ES module loaded from `App.razor`; it exposes `window.skPrefs` with cookie read/write and the browser time zone, and sets `sk-tz` on first load.
- `AppShell.razor.js` is a collocated module for the `.scrolled` observer; it disposes on component dispose.
- `UiPreferences`/`ViewerClock` call `prefs.js` through `IJSRuntime`; no JS reads or writes layout state directly.
- `Editor`/player modules load later; hls.js is imported only by the player module, never by the shell.
- `<ImportMap />` is kept; no CDN and no Node at runtime.
- Anything changing more than once a second stays in JS (player/stats/scrubbing/pointer); shell state crosses the circuit only on user actions.

## 12. CSS and design-system source

- Built `tokens.css`, `components.css`, and `fonts.css` are committed under `src/StreamKeepr/wwwroot/css/`; the source is `design-system/`.
- No Google Fonts request; `fonts.css` declares Material Symbols Rounded for icons, and UI text uses the system monospace stack.
- Generated CSS is used as-is; `npm run sync:ds` is deferred until the design system actually changes. When added, it is dev-only; Node never enters the image.
- New shared styles go into `components.css` using tokens; no raw hex, px colours, or hard-coded font sizes. Text uses `var(--text-<style>)`.
- The design-system previews in `design-system/components/` are the visual reference; `/dev/components` mirrors them for primitives.

## 13. Test matrix

bUnit:

- Shell layout, drawer modes, theater, responsive classes, top bar slots, REC dot, storage warning.
- `UiPreferences` parse/defaults/cap/write/Changed/dispose; malformed cookie.
- `ViewerClock` cookie/invalid zone/UTC fallback/DST; `Format` values.
- `LibraryView` state/sort/search/query/throttle/dispose; unknown id.
- Primitive markup/ARIA/keyboard; `RequiresTwitch` visible-but-inert; dialog/snackbar.
- Loading/empty/unknown/reconnect notice; Development-only dev page.

Vitest:

- `prefs.js` cookie read/write, `sk-tz` first-load, malformed values, time zone helper.

Playwright:

- Drawer expanded → rail → theater; reload preserves `sk-ui`; responsive 960/600; `.scrolled` class.
- Reconnect notice/retry; auth disabled controls; unknown route; `/dev/components`.
- axe clean and keyboard-only use on the shell. Visual regression is dropped for now (#106).

No automatic retries; flaky tests are fixed or quarantined per the testing contract.

## 14. Binding versus implementation freedom

Binding: project/render model, routes and query keys, shell structure/breakpoints, cookie names/schema/defaults/caps, clock behavior, `LibraryView` interface/throttle/state rules, primitive APIs/ARIA, Mock profile behavior, reconnect/auth states, JS module loading, and CSS order.

Freedom: component internals, exact markup beyond the design-system classes, JS helper style, error wording, and test implementation.

## 15. Related

- `docs/backend-ui-contract.md` for view models and services.
- `docs/hosting-contract.md` for host/middleware/CSS assets.
- `docs/twitch-auth-contract.md` for sign-in and auth states.
- `docs/testing-contract.md` for test layers and CI.
- `docs/design-assets.md` and `design-system/` for tokens, components, previews, and fonts.
