StreamKeepr is a personal DVR for Twitch: it captures streams to disk as HLS playlists and plays them back in the browser. The interface uses Material 3 structure in the Dracula PRO palette, built around one idea — **the video is the product**. Everything else (library, stats, details) sits beside or under the player and collapses out of its way.

## Layout

- Three regions, always in this arrangement: the **top app bar** (64px, `app-bar-height`), the **library drawer** on the left (`drawer-width`, 320px) and the **player column** on the right. See the `AppShell` component for the full screen.
- The player column stacks, top to bottom: `VideoPlayer` → `StatsPanel` → `RecordingDetails`. The player always comes first and takes the full column width at 16:9; never place anything above it.
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

- One typeface everywhere: **MonoLisa**, a variable monospace (weights 100–1000, upright and italic). Both family tokens, `sans` and `mono`, resolve to it; keep using `sans` for UI text and `mono` for data so the roles survive a future face change.
- MonoLisa ships as 23 character-set files per style. Basic Latin (`fonts/0-normal`, `fonts/1-italic`) is declared from `tokens.json`; the other sets (Latin-1 and Extended, punctuation such as — and …, arrows, box drawing, symbols, Powerline glyphs) are declared in `components/bundle.css` with `unicode-range`, so a page only downloads the sets its text uses.
- Monospace runs wide (0.64em per character), so the scale sits a step below Material's: default text is `body-medium` 13px, recording titles in the library `title-small` 13px (two lines, then ellipsis), streamer names and panel headers `title-medium` 15px, the title under the player `headline-small` 22px, the wordmark `title-large` 20px.
- No letter-spacing, except uppercase status chips (`label-small`, 0.6px) and stats overlines. Weight carries hierarchy: 400 body, 500 data, 600 titles and labels, 700 the wordmark and chips.
- Data styles: `timecode` in the player and on thumbnails, `stat-hero` for the headline number on a stat tile, `stat-value` for key/value rows. MonoLisa's figures are already fixed-width, so numbers never jitter as they update.
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

Fonts: one file per family is enough (below: the upright face nearest regular weight; bold and italic synthesize). Fetch it as the fetch column says (Artifact tool `read`, that id or path as `path`), upload it as an asset (`publish`, `file_path`, `asset:true`) and declare it with an `@font-face { font-family: "<family>"; src: url(<uploaded>) }` rule.

- **Design canvas:** bring a font that is kept by id, or was not copied, as for any other page, with its `@font-face` in the artboard after the `tokens.css` link; the linked `tokens.css` cannot load it.
- **Slides deck:** for each family you use in the deck (`faces` holds at most the number of typefaces Slides’ `deck-files.md` gives), install its file with the system and name the path it lands on as that family’s `src` in `project/deck.json` `faces`, under the key in the Slides column. A deck takes a font as a file only under the rule in Slides’ `fonts.md` (no space or leading underscore in its path, for two): any other font, or one kept by id, is uploaded as `fonts.md` says.

| family | file | fetch | CSS | Slides `faces` key |
| --- | --- | --- | --- | --- |
| 0 | `fonts/0-normal.woff2` (weight 400) | `read` the file with `project/` in front |  | `0` |
| 1 | `fonts/1-italic.woff2` (weight 400, italic) | `read 11dc5b909e973acacea47356a94ff53b` |  | `1` |
| 5 | `fonts/5-italic.woff2` (weight 400, italic) | `read 69a4a45a13da818b7aa0d8d36977ff3b` |  | `5` |
| 6 | `fonts/6-normal.woff2` (weight 400) | `read 70b395d1cde040f91f045ea6f1d89b0d` |  | `6` |
| 7 | `fonts/7-italic.woff2` (weight 400, italic) | `read 822996a5aa9fdbf5d4066f708c0b5572` |  | `7` |
| 9 | `fonts/9-italic.woff2` (weight 400, italic) | `read 85541e0cebb16c65215d17d369d28459` |  | `9` |
| 25 | `fonts/25-italic.woff2` (weight 400, italic) | `read ecd20468f5efeb3dafb008f7a4141072` |  | `25` |
| 31 | `fonts/31-italic.woff2` (weight 400, italic) | `read 3bada591019ca87bb80ed9e605629340` |  | `31` |
| 33 | `fonts/33-italic.woff2` (weight 400, italic) | `read d6393ff42889b03585ed82c3f2feb367` |  | `33` |
| 43 | `fonts/43-italic.woff2` (weight 400, italic) | `read 8fb7968c9963d82aa046a62d4f1d3fd0` |  | `43` |
| 2 | `fonts/2-normal.woff2` (weight 400) | `read 1bfc9a1a7f0cc21358d6a114725c01e0` |  | `2` |
| 3 | `fonts/3-italic.woff2` (weight 400, italic) | `read 08fcff67bd0fd974ad084ae8c631363b` |  | `3` |
| 4 | `fonts/4-normal.woff2` (weight 400) | `read 43225b4d7b3992ce9c73c5744eaf490d` |  | `4` |
| 8 | `fonts/8-normal.woff2` (weight 400) | `read 8dbc0ec15b3bfa31242fec23c4d95db0` |  | `8` |
| 10 | `fonts/10-normal.woff2` (weight 400) | `read 6e9f9a2863ba57a3997215d4cf263481` |  | `10` |
| 11 | `fonts/11-italic.woff2` (weight 400, italic) | `read 423d535a0e4b7845cfb5aaf908e05312` |  | `11` |
| 12 | `fonts/12-normal.woff2` (weight 400) | `read 27fd280f0be20e6ab2f2b999f28d21ce` |  | `12` |
| 13 | `fonts/13-italic.woff2` (weight 400, italic) | `read 7a1a4cbd6251cc923bdbeda165035486` |  | `13` |
| 14 | `fonts/14-normal.woff2` (weight 400) | `read 0ac026ba8688b7ea4e7b57e084149b62` |  | `14` |
| 15 | `fonts/15-italic.woff2` (weight 400, italic) | `read 30d0dd7137032d19cab353d010880c5b` |  | `15` |
| 16 | `fonts/16-normal.woff2` (weight 400) | `read 0126cf6d04b140d4db2ebac97574d89f` |  | `16` |
| 17 | `fonts/17-italic.woff2` (weight 400, italic) | `read 565eca07223356394c69b5871ab3d628` |  | `17` |
| 18 | `fonts/18-normal.woff2` (weight 400) | `read dcc449f1528efeebd3b06769017e8813` |  | `18` |
| 19 | `fonts/19-italic.woff2` (weight 400, italic) | `read c19ba166c08f3ff5047960bb3a88e80d` |  | `19` |
| 20 | `fonts/20-normal.woff2` (weight 400) | `read 1f6aba141a707a8854f82946bf4a5531` |  | `20` |
| 21 | `fonts/21-italic.woff2` (weight 400, italic) | `read 499302945c1dcdcee965e6b1cc95c415` |  | `21` |
| 22 | `fonts/22-normal.woff2` (weight 400) | `read 13c6b459fce7c1a2b0d666711ade57b0` |  | `22` |
| 23 | `fonts/23-italic.woff2` (weight 400, italic) | `read 9f770e1e35b945e248afc9aa3353a6f0` |  | `23` |
| 24 | `fonts/24-normal.woff2` (weight 400) | `read 52cc86f43741390000765a6757ed26a1` |  | `24` |
| 26 | `fonts/26-normal.woff2` (weight 400) | `read 44cee64c19a7a356a5a863774a9de372` |  | `26` |
| 27 | `fonts/27-italic.woff2` (weight 400, italic) | `read bc42466f19b95ecbd369aeef0b00de86` |  | `27` |
| 28 | `fonts/28-normal.woff2` (weight 400) | `read b2ebbba28a6e3e3213071c7b26e13e99` |  | `28` |
| 29 | `fonts/29-italic.woff2` (weight 400, italic) | `read c2af644733c548b25402e72982eb772f` |  | `29` |
| 30 | `fonts/30-normal.woff2` (weight 400) | `read 068b6416cd11341163ad41608304cdcb` |  | `30` |
| 32 | `fonts/32-normal.woff2` (weight 400) | `read cc6205f8391e424f89e19fa772c8cd97` |  | `32` |
| 34 | `fonts/34-normal.woff2` (weight 400) | `read b58b06f2cec5a1770ffdfc6c64fc4709` |  | `34` |
| 35 | `fonts/35-italic.woff2` (weight 400, italic) | `read df68d9c932e2c34e7e383eced406d7ff` |  | `35` |
| 36 | `fonts/36-normal.woff2` (weight 400) | `read edf2ce2c58ded0105d85607c7cb984cf` |  | `36` |
| 37 | `fonts/37-italic.woff2` (weight 400, italic) | `read 7e060b0bb90e339a82e5d0dd70dd3bc4` |  | `37` |
| 38 | `fonts/38-normal.woff2` (weight 400) | `read ea38fcc684de57a80508566c42772bc0` |  | `38` |
| 39 | `fonts/39-italic.woff2` (weight 400, italic) | `read 9d6c8cd11658c9a70e4bbe6f0309f567` |  | `39` |
| 40 | `fonts/40-normal.woff2` (weight 400) | `read dac24a143da4b524426497c22cdf9115` |  | `40` |
| 41 | `fonts/41-italic.woff2` (weight 400, italic) | `read 8d4b070b5d1c267eb4f8678db47c1000` |  | `41` |
| 42 | `fonts/42-normal.woff2` (weight 400) | `read 525a96fe9301a7402d8e8fbfef3be07c` |  | `42` |
| 44 | `fonts/44-normal.woff2` (weight 400) | `read 4574e1483a4462ccd65d44c876bbde44` |  | `44` |
| 45 | `fonts/45-italic.woff2` (weight 400, italic) | `read 27cf7d03c4f4f595a262e679833f1131` |  | `45` |

**Read, per thing:** a component’s props, parts and examples: `api/components/<Comp>.md`; token values: `api/tokens.md`. After this README, fetch the cards and fonts you need in ONE message as parallel calls — none depends on another.

**Two rules.** Before you use a thing — a component, a token group, an icon, an asset — read its card from the index below; a value you did not read from a card is a guess. `tokens.json`, `manifest.json` and `design-system.json` are sources for tools: hand them over. `components/<Comp>/README.md` is the long-form second read a card links to; `SKILL.md` and `artifact-type/` beside them are authoring guidance, not needed to consume the system.

## Index (generated — do not edit)

**Tokens**

- `api/tokens.md` — Every token: surface, fill, border, palette, type, spacing, radius, shadow, size, opacity. (21.3k)

**Components** (`api/components/<Comp>.md`, 9)

- **Layout**: `AppShell` — The full StreamKeepr screen: top app bar, library drawer on the left, and the player column with the stats panel and recording details stacked beneath the play… · `TopAppBar` — The 64px bar across the top: menu (drawer) toggle, the StreamKeepr wordmark, recording search, disk space, schedule, settings and the Twitch auth control
- **Actions**: `Buttons` — Material 3 buttons, icon buttons and filter chips used across StreamKeepr · `TwitchAuthButton` — The control that connects a Twitch account, enabling features that need it (following list, live notifications, auto-record of followed channels, subscriber-on…
- **Navigation**: `LibraryDrawer` — The left pane listing recordings grouped by streamer, newest first at every level
- **Playback**: `RecordingDetails` — A collapsible card under the stats panel with the recording's title, streamer, category, size and the actions for it · `StatsPanel` — A collapsible "Stats for nerds" panel directly under the player, showing playback (video) and network statistics side by side · `VideoPlayer` — The primary component: a 16:9 HLS player with an overlay title strip and a control bar
- **Status**: `StatusChip` — Small uppercase chips that state a recording's condition in words, with colour as a second cue
