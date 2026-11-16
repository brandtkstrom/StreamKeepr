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
| `Collapsible` | `MudExpansionPanel` (Blazor-rendered), `MudCollapse` | Details uses a panel. Stats keeps its own markup (see islands). |
| `Dialog` | `MudDialog` via `IDialogService.ShowAsync`, or `ShowMessageBoxAsync` | See `Dialog`. |
| `Menu`, `MenuItem` | `MudMenu`, `MudMenuItem` | `AnchorOrigin="Origin.BottomRight"`, `TransformOrigin="Origin.TopRight"`. |
| `Snackbar`, `SnackbarHost` | `ISnackbar` + `MudSnackbarProvider` | Bottom left; one at a time; an action, never a close icon alone. |
| `Switch` | `MudSwitch T="bool"` | `Color.Primary`. |
| `Tooltip` | `MudTooltip` | Ground restyled to `tooltip-background`. |
| `Icon` | `MudIcon` + `Icons.Material.Rounded.*` | Removes the Material Symbols webfont. |

## Layout and features

| StreamKeepr component | Built from |
|---|---|
| `AppShell` | MudBlazor's Layout 2 wireframe: `MudLayout`, `MudAppBar` (`Dense` below Md), `MudDrawer` (`ClipMode.Always`; `Mini` on Md and up, `Temporary` below), `MudMainContent`, all inside `MudBreakpointProvider` |
| `BottomNav` (new) | `MudAppBar Bottom` inside `MudHidden Breakpoint="Breakpoint.MdAndUp"`: Home, Library, Settings |
| Watch tabs (new, below Md) | `MudTabs` + `MudTabPanel` with icons: Chat, Details, Stats |
| `StorageMeter` | The library drawer's footer: `MudTooltip` + caption + full-width `MudProgressLinear` (`Size.Small`, `Color.Warning` under 10% free). It moves out of the app bar. |
| `SearchField` | `MudTextField` `Variant.Outlined`, `Margin.Dense`, `Adornment.Start`, `Immediate`, `DebounceInterval="150"`; 360px max, 480px while focused |
| `TwitchAccount` | `MudButton Href` (styled `twitch`) or a `MudMenu` with an avatar activator |
| `LibraryDrawer`, `StreamerGroup`, `RecordingRow` | `MudDrawer` content: header, `MudChipSet`, and a tree of `MudAvatar` + `MudText` rows (see islands) |
| `Home` (new; the default view) | Two `<section>`s with `MudText Typo.h6` headers, each a horizontal `<ul>` of `RecordingCard`s; `MudButton` "Show all"; `MudIconButton` chevrons; `MudSkeleton` while loading |
| `RecordingCard` (new) | A real `<a>` with the thumbnail, `MudAvatar` on a channel colour (`channel-*`), `MudText` title and meta, and one `StatusChip` |
| `RecordingDetails` | `MudExpansionPanels` → `MudExpansionPanel`, `MudButton`, `MudIconButton`, `MudProgressLinear`, `MudChip` chapters |
| `SettingsSheet` (new) | `MudDrawer Anchor.End Temporary Overlay="false" Width="440px"` on Md and up; `MudDialog` with `FullScreen` below; shared `SettingsSections` |
| `ChannelSettings` | `MudPaper` + `MudList` rows, `MudTextField`, `MudButton` |
| `ConnectionStatusPanel` | `MudPaper` + `MudSimpleTable` (key/value) + `StatusChip`, `MudAlert` for the coverage note |
| `ReconnectNotice` | `MudAlert Severity.Warning Dense` with a Retry action |

## JS-owned islands

`VideoPlayer`, `StatsPanel` and `ChatPane` render once. Their `.razor.js` modules own the DOM after that, and `LibraryDrawer`'s tree keyboard model belongs to `tree-nav.js`. **Do not put interactive MudBlazor components inside a JS-owned island**: Mud components re-render and would overwrite attributes the module set. Inside an island:

- keep the existing markup and `data-sk` hooks;
- style it with the Mud CSS variables (`var(--mud-palette-surface)`) and the StreamKeepr tokens (`var(--player-accent)`), so it follows the theme switch;
- draw icons as inline SVG copied from `Icons.Material.Rounded.*` (the constants are SVG strings).

Static MudBlazor components that never re-render, such as `MudIcon`, `MudAvatar` and `MudText`, are fine in the library tree rows, because Blazor owns those rows.
