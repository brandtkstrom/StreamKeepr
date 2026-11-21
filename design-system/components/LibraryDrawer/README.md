The library in a `MudDrawer`: recordings grouped by streamer, newest first at every level, filtered by chips and by the app-bar search.

## Build it from

- **Header:** `MudText Typo.h6` "Library", the order in `caption` ("Newest first"), and a `MudMenu` (`Icons.Material.Rounded.MoreVert`, `Size.Small`) with Newest first, Oldest first and A–Z by streamer as checkable items.
- **Filters:** `<MudChipSet T="string" SelectionMode="SelectionMode.SingleSelection" SelectedColor="Color.Primary" Variant="Variant.Outlined" CheckMark>` with All (`ViewList`), Recording (`FiberManualRecord` in `rec`) and Saved (`Inventory2`). The icon gives way to the check mark when a chip is selected.
- **Paused note:** `MudAlert Severity.Warning Dense` "Live updates paused". app CSS shows it while the circuit reconnects (`body[data-sk-reconnect]`).
- **Tree:** keep the existing `role="tree"` markup and `tree-nav.js` (roving tab stop, arrow keys). Each group head is a `treeitem` with a `MudAvatar` (`Size.Large`, the streamer's image, or their initial on their channel colour (`channel-*`); a `MudBadge Dot BadgeClass="sk-live"` filled `rec` while capturing), the name in `subtitle1`, the meta line in `caption`, and an `ExpandMore` chevron. Day headers are `label` type. Rows are `RecordingRow`.
- **Disk usage footer** (`StorageMeter`), pinned to the bottom of the drawer below the scrolling tree, above a `divider` rule: the `Storage` icon, "1.2 TB free" in `caption` at Medium with "of 4 TB" right-aligned, and a full-width `MudProgressLinear Size.Small` of the space used, in `primary`. Under 10% free, the icon becomes `Warning`, and the icon, the free figure and the bar turn `warning`. The tooltip gives "Recording disk: 1.2 TB free of 4 TB". It renders nothing when the size is unknown.
- **Mini rail** (`DrawerVariant.Mini`, 72px; the default while watching on desktop): a `Home` icon button, a short divider, then streamer avatars at 32px (`MudAvatar Size.Medium`, smaller than the open drawer's 40px), the current streamer ringed in `primary`, the name in a `MudTooltip`. A streamer being captured carries the app bar logo's live dot, larger: a 14px `rec` disc at the avatar's top-right corner, ringed 2px in `drawer-background`, pulsing to 35% opacity and back every 1.6 s like the logo's dot (still under `prefers-reduced-motion`). The open drawer keeps its own avatars and badge. At the bottom, the footer shrinks to a `Storage` icon button (`Warning` in `warning` under 10% free) with the same tooltip.

## The consumer provides

`ILibraryView` (streamers, recordings, `IsLoaded`), the current query (sort, state, search), the selected recording id, collapsed groups from `UiPreferences`, and the viewer's time zone for day headers.

## Rules

- A streamer who is recording sorts to the top. Groups are divided by a `divider` hairline.
- While loading, show six skeleton rows (`MudSkeleton`), not a spinner. With no results: "No recordings match" when filtered, otherwise "No recordings yet".
- Selecting a row in the temporary drawer closes the drawer.
