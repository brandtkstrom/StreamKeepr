# Component map

StreamKeepr's hand-built primitives (`Components/Primitives/`) give way to MudBlazor 9 (component names and parameters checked against 9.12.0). Feature components keep their names and contracts, and are rebuilt from MudBlazor parts. Each card in Components shows the result.

## Primitives → MudBlazor

| StreamKeepr primitive | MudBlazor | Notes |
|---|---|---|
| `Button` (filled, outlined, text, tonal, danger) | `MudButton` `Variant.Filled` / `Outlined` / `Text` | Tonal becomes Filled `Color.Primary` for the region's main action. Danger becomes Filled `Color.Error`. See `Buttons`. |
| `IconButton` | `MudIconButton`, `MudToggleIconButton` | Toggles (theater, stats) use `MudToggleIconButton` with `ToggledColor="Color.Primary"`. |
| `Chip` (filter) | `MudChipSet` + `MudChip` | `SelectionMode.SingleSelection`, `SelectedColor="Color.Primary"`. |
| `StatusChip` | `MudChip` `Size.Small`, class `sk-status` | See `StatusChip`. The label stays `text-primary`, and the icon takes the status colour. |
| `Avatar` | `MudAvatar` + `MudBadge` | The live ring is `MudBadge Dot Overlap Origin="Origin.BottomRight" BadgeClass="sk-live"`, with `.sk-live` filled `rec` (no `Color` maps to `rec`). |
| `Collapsible` | `MudCollapse` under a real disclosure button | Chapters uses `MudPaper` + `MudCollapse`: 9.12's `MudExpansionPanel` header has no button role or `aria-expanded`. Viewers uses the same pattern. Statistics is an overlay inside the player island (see islands). |
| `Dialog` | `ConfirmDialog`: an inline `MudDialog` (shown through `IDialogService` by the `MudDialogProvider`) that its owner keeps open; `ShowMessageBoxAsync` for a plain confirm | See `Dialog`. |
| `Menu`, `MenuItem` | `MudMenu`, `MudMenuItem` | `AnchorOrigin="Origin.BottomRight"`, `TransformOrigin="Origin.TopRight"`. |
| `Snackbar`, `SnackbarHost` | `ISnackbar` + `MudSnackbarProvider` | Bottom left; one at a time; an action, never a close icon alone. |
| `Switch` | `MudSwitch T="bool"` | `Color.Primary`. |
| `Tooltip` | `MudTooltip` | Ground restyled to `tooltip-background`. |
| `Icon` | `MudIcon` + `Icons.Material.Rounded.*` | Removes the Material Symbols webfont. |

## Layout and features

| StreamKeepr component | Built from |
|---|---|
| `AppShell` | MudBlazor's Layout 2 wireframe: `MudLayout`, `MudAppBar` (`Dense` below Md; labeled library toggle, recording tally, search shortcut), `MudDrawer` (`ClipMode.Always`; `Mini` on Md and up, `Temporary` below), `MudMainContent`, all inside `MudBreakpointProvider` |
| `BottomNav` (new) | `MudAppBar Bottom`, rendered until the breakpoint is known to be Md or wider and hidden by CSS at 960px and up (not `MudHidden`, which drops it from the prerender): Home, Library, Settings |
| Watch tabs (new, below Md) | `MudTabs` + `MudTabPanel` with icons: Chat, Chapters (only with chapters), Stats |
| `StorageMeter` | The library drawer's footer: `MudTooltip` + caption + full-width `MudProgressLinear` (`Size.Small`, `Color.Warning` under 10% free); in the rail, the icon plus a 24×3px gauge. |
| `SearchField` | The library toolbar's input at every width: leading `Search` icon, `/` keycap on fine pointers, 250ms debounce, URL `q`. The app bar carries only a `Search` shortcut. |
| `TwitchAccount` | `MudButton Href` (styled `twitch`) or a `MudMenu` with an avatar activator |
| `SignIn` (new) | A `/signin` page with an empty layout: `MudPaper` card, `MudAlert Dense` for the reason, the `twitch` link button. Only used when `Auth:AllowedTwitchLogins` is set |
| `LibraryDrawer`, `StreamerGroup`, `RecordingRow` | `MudDrawer` content: toolbar (title, counts, search, sort-as-menu, close), `MudChipSet`, a channels list (`role="listbox"`) and, beside it, the selected channel's recordings column of `RecordingRow`s with sticky day headers |
| `Home` (new; the default view) | Two `<section>`s with `MudText Typo.h6` headers, each a horizontal `<ul>` of `RecordingCard`s; `MudButton` "Show all"; `MudIconButton` chevrons; `MudSkeleton` while loading |
| `RecordingCard` (new) | A real `<a>` with the thumbnail, `MudAvatar` on a channel colour (`channel-*`), `MudText` title and meta, and one `StatusChip` |
| `RecordingDetails` | The title block under the player, no panel: `MudTooltip` title with a measured two-step long-title scale, a channel byline (`ChannelAvatar` + display name) linking to Twitch, a labelled fact slate (`dl`: date range, length, category, chapter count, file), a live `StatusChip`, a `MoreVert` `MudMenu`, `MudProgressLinear` for an export |
| `Chapters` (was the Recording details panel) | `MudPaper` + `MudCollapse` under a disclosure button, a list of chapter buttons; not rendered without chapters |
| `SettingsSheet` (#183) | `MudDrawer Anchor.End Temporary Overlay="false" Width="440px"` on Md and up; an inline `MudDialog` with `FullScreen` below; shared `SettingsSections`; `?settings=open` in `Ui/SettingsSheetUrl` |
| `Settings` page | Header: `SettingsExit` (`MudTooltip` + `MudIconButton`), `MudText Typo.h5`; then `SettingsSections` (`SettingsSection`: outlined `MudPaper` + icon + `MudText Typo.h6`; `MudAlert` for the HTTPS note) |
| `ChannelSettings` | `MudPaper` + `<ul role="list">` rows (not `MudList`: `MudListItem` wraps its content in a `<p>`), `ChannelAvatar`, `StatusChip`, `MudTextField`, `MudButton` |
| `ConnectionStatusPanel` | `MudPaper` + `MudSimpleTable` (key/value) + `StatusChip`, `MudAlert` for the coverage note |
| `ReconnectNotice` | `MudAlert Severity.Warning Dense` with a Retry action |

## JS-owned islands

`VideoPlayer`, `StatsPanel` (an overlay rendered inside the player's frame), `Viewers` and `ChatPane` render once. Their `.razor.js` modules own the DOM after that, and `LibraryDrawer`'s two-column keyboard model (it replaces `tree-nav.js`) belongs to a small library-keys module. **Do not put interactive MudBlazor components inside a JS-owned island**: Mud components re-render and would overwrite attributes the module set. Inside an island:

- keep the existing markup and `data-sk` hooks;
- style it with the Mud CSS variables (`var(--mud-palette-surface)`) and the StreamKeepr tokens (`var(--player-accent)`), so it follows the theme switch;
- draw icons as inline SVG copied from `Icons.Material.Rounded.*` (the constants are SVG strings).

Static MudBlazor components that never re-render, such as `MudIcon`, `MudAvatar` and `MudText`, are fine in the library's channel and recording rows, because Blazor owns those rows.
