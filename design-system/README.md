StreamKeepr is a personal DVR for Twitch: it captures streams to disk as HLS playlists and plays them back in the browser. The interface uses Material 3 structure in the Dracula PRO palette, built around one idea — **the video is the product**. Everything else (library, stats, details) sits beside or under the player and collapses out of its way.

## Layout

- Three regions, always in this arrangement: the **top app bar** (64px, `app-bar-height`), the **library drawer** on the left (`drawer-width`, 320px) and the **player column** on the right. See the `AppShell` component for the full screen.
- The player column stacks, top to bottom: `VideoPlayer` → `RecordingDetails` → `StatsPanel`. The player always comes first and takes the full column width at 16:9; never place anything above it.
- Maximise viewing area with collapse, not with scrolling: the drawer collapses to a 72px rail (`rail-width`) of streamer avatars and hides entirely in theater mode; Stats for nerds and Recording details each collapse to a 48px header. Remember those choices per viewer.
- The player column's content is capped at 1100px and centred; gutters are `space-6` on the outside, `space-3` between stacked panels.
- Below 960px wide the drawer becomes a modal drawer over `scrim` with `elevation-3`; below 600px stats tiles stack in one column.

## Library ordering

- Recordings are organised **by streamer**, and **newest first at every level**: streamer groups by their latest recording (a streamer being captured right now is always on top), recordings inside a group by start time, descending, under day headers — `Today`, `Yesterday`, then `Wed, Nov 4`.
- The drawer header always states the order ("↓ Newest first").
- Filter chips (All · Recording · Saved) sit under the header; search in the app bar filters the same list.

## Color

- One theme, **Dracula PRO**, laid onto Material 3 colour roles. The eleven Dracula PRO colours are tokens of their own (`dracula-background` … `dracula-yellow`, exact hex); the role tokens alias them. Use the role tokens in components, never the `dracula-*` tokens or raw hex.
- Role map: `primary` = Purple · `tertiary` = Cyan · `success` = Green · `warning` = Orange · `error` and `rec` = Red · `highlight` = Yellow · `surface` = Background · `secondary-container` and `surface-container-highest` = Selection · `outline` and `secondary` = Comment · `on-surface` = Foreground. Pink is held in reserve as the next categorical hue.
- Surfaces climb in Background's own hue (250°, 15% saturation): `surface-container-lowest` 10% → `surface` (Background, 15%) → `surface-container-low` 17% (drawer) → `surface-container` 19.5% (panels) → `surface-container-high` 23% (tiles, search, hovered rows) → `surface-container-highest` (Selection, 30%). Elevate with a higher step, not with shadows.
- Text: `on-surface` (Foreground) for primary text; `on-surface-variant` for dates, durations and stat labels. Comment itself is too dark for small text (3.6:1 on Background), so `on-surface-variant` is Comment's hue lightened to 75% — ≥4.6:1 on every surface step and on Selection. Keep Comment for borders and marks.
- Bright Dracula fills always take Background-coloured text: `on-primary`, `on-tertiary`, `on-rec`, `on-warning`, `on-highlight` are all `dracula-background`.
- `primary` (Purple) is for the main action, the playhead/progress, the active control and the focus ring. As text it is used only on `surface` through `surface-container`. Selected library rows use Selection with Foreground text.
- The player is always `player-bg` (pure black); overlays on it use `player-scrim` with `on-player` (Foreground). No gradients.
- **Reserved colours.** `rec` means "a capture is being written right now" — the REC dot, the live-edge tick, the REC chip. It shares Red with `error`, so errors always carry ⚠ or a word, and REC is always the pulsing dot or the REC label. `warning` (Orange) marks partial captures and low disk. `success` (Green) marks a connected Twitch account. `twitch` is Twitch's own purple, kept exact, and appears only on the Connect with Twitch button — it sits close to Dracula Purple, so the two are told apart by label colour (white on Twitch, Background on `primary`) and wording.
- Stats charts: video series in `primary` (Purple), network series in `tertiary` (Cyan); one series per sparkline, labels in text tokens, never in the series colour.

## Typography

- No text webfont is bundled. Both family tokens, `sans` and `mono`, resolve to the system monospace stack (`ui-monospace`, SF Mono, Menlo, Consolas); keep using `sans` for UI text and `mono` for data so the roles survive a future face change.
- Material Symbols Rounded is the only bundled font, declared from `components/bundle.css` for icons.
- Monospace runs wide (0.64em per character), so the scale sits a step below Material's: default text is `body-medium` 13px, recording titles in the library `title-small` 13px (two lines, then ellipsis), streamer names and panel headers `title-medium` 15px, the title under the player `headline-small` 22px, the wordmark `title-large` 20px.
- No letter-spacing, except uppercase status chips (`label-small`, 0.6px) and stats overlines. Weight carries hierarchy: 400 body, 500 data, 600 titles and labels, 700 the wordmark and chips.
- Data styles: `timecode` in the player and on thumbnails, `stat-hero` for the headline number on a stat tile, `stat-value` for key/value rows. Monospace figures are fixed-width, so numbers never jitter as they update.
- Components take their type from the style tokens (`font: var(--text-body-medium)` and so on), never from hard-coded sizes.
- Search matches are marked with `highlight` (Yellow) behind the matched characters. Sentence case everywhere except status chips (uppercase: REC, PROCESSING, PARTIAL).

## Shape, spacing, elevation

- Spacing steps: `space-1` 4 · `space-2` 8 · `space-3` 12 · `space-4` 16 · `space-6` 24 · `space-8` 32.
- Corners: buttons, icon buttons, avatars and the seek knob are `radius-full`; thumbnails and chips `radius-sm`; the player frame, panels and cards `radius-md`; the drawer's trailing edge `radius-lg`; the search field and dialogs `radius-xl`.
- Shadows only where something floats: `elevation-1` on the scrolled app bar and hovered filled buttons, `elevation-2` on menus and tooltips, `elevation-3` on the modal drawer and dialogs.

## States and motion

- State layers: overlay the content colour at `state-hover` (8%) or `state-pressed` (10%); disabled content at `state-disabled` (38%).
- Focus ring: a solid 2px `primary` outline with a 2px offset on every interactive element (≥3:1 on all surfaces; on the Twitch button it lands on the app bar ground, not the purple).
- Collapse and expand over 300ms with Material's emphasized easing `cubic-bezier(0.2, 0, 0, 1)`; player overlays fade in 200ms and hide after 3s idle while playing. Honour `prefers-reduced-motion`: the REC pulse and spinners stop.

## Content and voice

- Short, plain and specific. Say what the machine is doing: "Recording now", "Processing", "Partial — gaps in capture", "1.2 TB free".
- Dates are relative for the last two days ("Today", "Yesterday"), then "Wed, Nov 4"; durations as "2h 14m" in meta lines and "2:14:07" in timecodes.
- Buttons are verbs: "Connect with Twitch", "Export MP4", "Copy playlist URL". No exclamation marks, no emoji.
- Features that need a Twitch login stay visible but disabled with "Connect Twitch to use this".

## Iconography

- Material Symbols Rounded (Google Fonts), weight 400, unfilled by default at 24px (20px in chips and menus, 18px inside buttons). Fill only to show an "on" state (play, REC).
- Core set: `menu`, `search`, `play_arrow` / `pause`, `replay_10` / `forward_10`, `volume_up`, `query_stats` (stats toggle), `picture_in_picture_alt`, `width_wide` (theater), `fullscreen`, `download`, `content_copy`, `delete`, `sensors` (live capture), `movie` (saved recording), `hard_drive`, `schedule`, `settings`, `link`.
- No Twitch logo is included. The Twitch button uses `link`; when shipping, use the official Glitch mark from Twitch's brand assets — never redraw it.
- No app logo yet: the wordmark is set in type ("Stream" in `on-surface`, "Keepr" in `primary`) after a `rec`-coloured REC dot.


---

## Consuming this system (generated — do not edit)

Every path named below is under `project/` in this design system: read `project/api/tokens.md`, not `api/tokens.md`.

9 components are documented without a runnable `components/bundle.js`: read each component’s card, and its README where it has one (`components/<Comp>/README.md`), and build to those guidelines. Tokens: the values are on `api/tokens.md`; a Slides deck or Design canvas also takes `tokens.json` by file path. On a Design canvas, also copy `tokens.css` to `project/ds/<folder>/tokens.css` in the call that installs this system, and link it in each artboard that uses this system, right after the `support.js` line and before the system’s other stylesheets: `<link rel="stylesheet" href="ds/<folder>/tokens.css">` (with one `../` in front for each folder the artboard is in).

No text font is bundled: `--font-sans` and `--font-mono` resolve to the system monospace stack. Material Symbols Rounded is bundled for icons in `components/bundle.css`.

**Read, per thing:** a component’s props, parts and examples: `api/components/<Comp>.md`; token values: `api/tokens.md`. After this README, fetch the cards you need in ONE message as parallel calls — none depends on another.

**Two rules.** Before you use a thing — a component, a token group, an icon, an asset — read its card from the index below; a value you did not read from a card is a guess. `tokens.json`, `manifest.json` and `design-system.json` are sources for tools: hand them over. `components/<Comp>/README.md` is the long-form second read a card links to; `SKILL.md` and `artifact-type/` beside them are authoring guidance, not needed to consume the system.

## Index (generated — do not edit)

**Tokens**

- `api/tokens.md` — Every token: surface, fill, border, palette, type, spacing, radius, shadow, size, opacity. (21.3k)

**Components** (`api/components/<Comp>.md`, 9)

- **Layout**: `AppShell` — The full StreamKeepr screen: top app bar, library drawer on the left, and the player column with the recording details and stats panel stacked beneath the play… · `TopAppBar` — The 64px bar across the top: menu (drawer) toggle, the StreamKeepr wordmark, recording search, disk space, schedule, settings and the Twitch auth control
- **Actions**: `Buttons` — Material 3 buttons, icon buttons and filter chips used across StreamKeepr · `TwitchAuthButton` — The control that connects a Twitch account, enabling features that need it (following list, live notifications, auto-record of followed channels, subscriber-on…
- **Navigation**: `LibraryDrawer` — The left pane listing recordings grouped by streamer, newest first at every level
- **Playback**: `RecordingDetails` — A collapsible card above the stats panel with the recording's title, streamer, category, size and the actions for it · `StatsPanel` — A collapsible "Stats for nerds" panel below the recording details, showing playback (video) and network statistics side by side · `VideoPlayer` — The primary component: a 16:9 HLS player with an overlay title strip and a control bar
- **Status**: `StatusChip` — Small uppercase chips that state a recording's condition in words, with colour as a second cue
