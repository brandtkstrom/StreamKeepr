The layout below MudBlazor's `Breakpoint.Md` (960px), phones and tablets: a dense top bar, a bottom app bar for navigation, an edge-to-edge player and tabs for everything else.

## Build it from

- **Top bar:** `MudAppBar Dense` (48px). On Home, Library and Settings it holds the wordmark, a search shortcut and the account avatar as a `MudMenu` activator (the name is in its menu). The search shortcut opens the temporary library drawer and focuses its search field; there is no separate search row. Settings moves to the bottom bar, and GitHub/Twitch live in Settings › About. On a watch page it holds an `ArrowBack` button to Home, the recording title (`subtitle1` at Medium, ellipsised), the search shortcut and the account.
- **Bottom bar:** `BottomNav` (`MudAppBar Bottom`), with Home · Library · Settings. It is hidden on watch pages, so the player and tabs get the room. The Library item carries the `rec` dot while anything is recording.
- **Library:** `MudDrawer Variant="Temporary"`, `drawer-width` (320px) but never wider than the screen less 48px, over `overlay-dark` at `elevation-4`, opened by Library in the bottom bar or the search shortcut. The toolbar carries the search field, counts, sort menu, close button and filters. It drills in rather than showing two columns: the channels list first; tapping a channel slides its recordings over the list, with an `ArrowBack` "All channels" button and the channel's avatar and name in the header (the card shows this state). Each recording row shows its Delete button all the time, since touch has no hover (see `RecordingRow`). A query-only change (typing, a filter, the sort) keeps the drawer open; picking a recording closes it. Disk usage sits at its foot.
- **Home:** the two rows run edge to edge with `pa-4` gutters, as swipe rows. Large cards are 85% and default cards 60% of the screen width.
- **Watching:**
  1. The player, edge to edge with square corners, at 16:9. On phones (Xs) it is `position: sticky` under the top bar, so it stays in view while the tabs scroll.
  2. The title in `h6`, then the meta line (streamer, `Event` start, `Timer` duration).
  3. The same actions as desktop (`RecordingDetails`): the REC chip and the `MoreVert` menu, with Copy playlist URL, Export MP4, Stop recording and Delete as menu items. An export's progress row follows.
  4. `MudTabs` (`Color.Primary`, full width, `KeepPanelsAlive`), with **Chat** (`Chat`), **Chapters** (`Toc`) and **Stats** (`QueryStats`: Video and Network, then the viewer tile and chart; phones get no overlay and no Viewers panel). Chapters is left out when the recording has no chapters. Chat comes first and is selected when the recording has chat; otherwise Chapters is, or Stats when there are no chapters either. Each panel gets at least half the viewport, and the main column scrolls under the sticky player.
- **Phone landscape** (height below 500px): the top bar hides and the player fills the screen, with its controls overlaid. Rotating back restores the layout.

## Rules

- Touch targets are 40px or more: 48px in the bottom bar and the tabs.
- Player controls: the existing container queries leave play, back 10 s, timecode and fullscreen below Md; the Chat and Stats player buttons are hidden because the tabs own them, and theater is not available. A `MoreVert` menu holding speed, stats and PiP is deferred because this slice does not change the player island's markup.
- Theater mode doesn't exist here. Fullscreen replaces it.
- Nothing scrolls sideways except the Home rows.
