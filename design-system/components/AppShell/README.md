# AppShell

The full StreamKeepr screen: top app bar, library drawer on the left, and the player column with the stats panel and recording details stacked beneath the player.

**Structure.** `.sk-shell` (column) → `.sk-appbar` → `.sk-shell-body` (row) → `.sk-drawer#sk-drawer` + `main.sk-main` → `.sk-main-inner` (max 1100px, centred) → `.sk-player`, `#sk-stats`, recording details.

**Order in the player column is fixed:** player first, then Stats for nerds (directly below, so numbers sit next to the picture they describe), then Recording details.

**Maximise the video.** Every surface around the player collapses:
- Menu button in the app bar → drawer collapses to the 72px rail (`.sk-drawer.rail`).
- Theater button in the player → drawer hidden (`.hidden`) and the main column goes to `surface-container-lowest`.
- Stats panel and details card collapse to a single 48px header.
Remember each of these per viewer (local storage) so the layout comes back the way it was left.

**Responsive.** Below 960px the drawer becomes a modal drawer over `scrim` with `elevation-3`, opened from the menu button; below 600px the stats tiles stack to one column.

**Consumer provides:** the library list (streamers sorted by their newest recording, recordings newest first), the selected recording, the HLS URL for the player and the Twitch session state.
