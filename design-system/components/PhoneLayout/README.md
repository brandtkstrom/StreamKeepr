The layout below MudBlazor's `Breakpoint.Md` (960px), phones and tablets: a dense top bar, a bottom app bar for navigation, an edge-to-edge player and tabs for everything else.

## Build it from

- **Top bar:** `MudAppBar Dense` (48px). On Home, Library and Settings it holds the wordmark, a search icon (which opens a full-width search row), the GitHub and Twitch links, and the account avatar as a `MudMenu` activator (the name is in its menu). On a watch page it holds an `ArrowBack` button to Home, the recording title (`subtitle1` at Medium, ellipsised) and search.
- **Bottom bar:** `BottomNav` (`MudAppBar Bottom`), with Home · Library · Settings. It is hidden on watch pages, so the player and tabs get the room.
- **Library:** `MudDrawer Variant="Temporary"`, `drawer-width` (320px) but never wider than the screen less 48px, over `overlay-dark` at `elevation-4`, opened by Library in the bottom bar. Picking a recording closes it. Disk usage sits at its foot.
- **Home:** the two rows run edge to edge with `pa-4` gutters, as swipe rows. Large cards are 85% and default cards 60% of the screen width.
- **Watching:**
  1. The player, edge to edge with square corners, at 16:9. On phones (Xs) it is `position: sticky` under the top bar, so it stays in view while the tabs scroll.
  2. The title in `h6`, then the meta line (streamer, `Event` start, `Timer` duration).
  3. One row of actions: the REC chip, then small buttons (Copy URL, Export) and a `MoreVert` menu for Stop recording and Delete.
  4. `MudTabs` (`Color.Primary`, full width, `KeepPanelsAlive`), with **Chat** (`Chat`), **Details** (`Info`) and **Stats** (`QueryStats`). Chat comes first and is selected when the recording has chat; otherwise Details is. Each panel gets at least half the viewport, and the main column scrolls under the sticky player.
- **Phone landscape** (height below 500px): the top bar hides and the player fills the screen, with its controls overlaid. Rotating back restores the layout.

## Rules

- Touch targets are 40px or more: 48px in the bottom bar and the tabs.
- Player controls: the existing container queries leave play, back 10 s, timecode and fullscreen below Md; the Chat and Stats player buttons are hidden because the tabs own them, and theater is not available. A `MoreVert` menu holding speed, stats and PiP is deferred because this slice does not change the player island's markup.
- Theater mode doesn't exist here. Fullscreen replaces it.
- Nothing scrolls sideways except the Home rows and the chapter strip.
