StreamKeepr is a personal DVR for Twitch. It captures streams to disk as HLS playlists and plays them back in the browser. This system is its interface built from **MudBlazor** components, themed as one colourful dark theme built entirely from MudBlazor's own colours: its default dark grounds, and vivid accents from the Material palette in `MudBlazor.Colors`. One idea governs every screen: **the video is the product.** The library, details, stats and chat sit beside or under the player and collapse out of its way.

Read **MudBlazor theme** for the `MudTheme` that carries these tokens into the app, and **Component map** for which MudBlazor component replaces each of StreamKeepr's own primitives.

## Layout

**StreamKeepr is video first.** The player is the largest thing on every watch screen and fills the space it is given. Everything else is beside it, under it, or behind a tap.

### The template

The shell is MudBlazor's **Layout 2 wireframe** (Getting Started › Wireframes: app bar plus a drawer clipped under it), made responsive with `MudBreakpointProvider` and CSS media queries. `MudHidden` is not used: it renders nothing until the circuit knows the breakpoint, so a phone's prerendered page would have no bottom-bar links. See `AppShell`.

| Width (MudBlazor breakpoint) | Top bar | Navigation | Watching a recording |
|---|---|---|---|
| **Md and up** (≥ 960px): laptop, desktop | `MudAppBar`, 64px: menu, wordmark, search field, GitHub and Twitch links, settings, account avatar | `MudDrawer Variant="Mini" ClipMode="Always"`: the 72px rail, or the 320px library when opened | Two columns. Left: the player, the title block with its icon actions and More menu at the top right, Chapters (when there are any) and Viewers (when there are samples); Statistics is an overlay inside the player. Right: chat, 320–400px wide and the full height of the view. |
| **Sm** (600–959px): tablet, small window | `MudAppBar Dense`, 48px | Bottom app bar (`BottomNav`); the library is a temporary drawer | Player edge to edge, then the title block, then `MudTabs`: Chat · Chapters · Stats (Chapters only when there are any) |
| **Xs** (< 600px): phone portrait | Dense, 48px; a back arrow on the watch page | Bottom app bar; temporary drawer | As Sm, with the player sticky under the top bar while the tabs scroll |
| Phone landscape (height < 500px) | Hidden | Hidden | The player fills the screen, with controls overlaid |

### The player

- On watch pages the player is **as wide as its column** (no max-width, and only the `space-4` gutter on desktop) and keeps 16:9. Its height is capped at the viewport height minus the app bar, the gutter and `player-peek` (88px), so the recording's title always shows below it without scrolling. When the cap applies, the video letterboxes inside the black frame.
- Opening a recording on desktop collapses the drawer to the rail. Home opens it fully again. Both choices are remembered per viewer.
- **Theater mode** (desktop only) makes the player span the full width of the view: no drawer, no gutters, no sidecar. Its height is capped at the viewport minus the app bar. The two columns move below it, chat still on the right, filling the height left. Fullscreen takes over theater mode's job on phones.
- Nothing ever sits above the player, and nothing sits to the right of the open chat: it is the full-height right-hand column on desktop.
- The recording's title, meta and actions sit directly under the player on the page, not in a panel; the actions are a compact row of icon buttons beside the title, with Stop and Delete in a More actions menu (`RecordingDetails`). The chapters are their own panel, `Chapters`, shown only when the recording has chapters.

### The rest

- **Playback is never interrupted by navigation.** Secondary things open over the page instead of replacing it: Settings is a right side sheet on desktop and a full-screen dialog on phones (`SettingsSheet`), and dialogs and menus float. The `/settings` route remains only for bookmarks.
- **The default view** at `/`, before a recording is open, is `Home`: every active recording (`Recording` or `Reconnecting`) in one row, and the 20 most recent complete recordings (`Saved` or `Partial`) in a second row below, both newest first by start time. The rows fill the window's width, showing as many cards as fit.
- Watch and Home pages have no width cap. Forms (Settings) cap at `form-max-width` (760px) for readable line lengths.
- The library drawer's footer shows disk usage. The desktop app bar carries search, the GitHub and Twitch links, settings and the account avatar; on phones, search is an icon and settings moves to the bottom bar.
- Maximise viewing area by collapsing things, not by scrolling. Remember each collapse per viewer.
- Touch targets are at least 40px, 48px in the bottom bar and tabs. Nothing scrolls sideways except the Home rows, which scroll inside themselves.
- Empty, no-match and not-found states get an icon, one headline, one sentence and at most one action. See `EmptyStates`.

## Library ordering

- Group recordings **by streamer**, and sort **newest first at every level**. Streamer groups sort by their latest recording, and a streamer being captured right now is always on top. Inside a group, recordings sort by start time, descending, under day headers: `Today`, `Yesterday`, then `Wed, Nov 4`.
- The drawer header always states the order ("Newest first"), with the sort menu beside it.
- Filter chips (All · Recording · Saved) sit under the header as a single-select `MudChipSet`. Search in the app bar filters the same list and marks matches with `highlight`.

## Color

- There is one theme, **MudBlazor Dark**, and the app is dark only. Every colour comes from MudBlazor 9.12.0: the grounds are `PaletteDark`'s defaults, and the accents and greys are `MudBlazor.Colors` constants. Each constant is a source token named after it (`purple-accent-1` = `Colors.Purple.Accent1`, `gray-darken-4` = `Colors.Gray.Darken4`). Components use the role tokens, never the source tokens or raw hex.
- The role tokens are named after MudBlazor's `Palette` properties (`primary` = `Palette.Primary`, `drawer-background` = `Palette.DrawerBackground`, and so on), so the theme is a straight transcription.
- **The colour is in the accents, on neutral grounds.** The roles are: `primary` = Purple A1 (magenta) · `secondary` = Cyan A3 · `tertiary` = Indigo A1 · `info` = Light Blue A2 · `success` = Green A3 · `warning` = Amber A3 · `error` = Red A1 · `rec` = Red A2 · `highlight` = Yellow A2. Each one has a job, so several can share a screen and still read: playback is magenta, network is cyan, the stream itself (chapters and viewer count) is indigo, and states are green, amber and red.
- **Each channel has a channel colour.** `channel-1` … `channel-8` (Deep Purple, Cyan, Pink, Amber, Light Green, Orange, Indigo, Teal accents) are assigned by a stable hash of the Twitch login. They fill the channel's avatar, with the initial in `channel-contrast-text`, and colour their name in chat, so a channel looks the same everywhere. They never mean a state.
- Grounds are MudBlazor's neutral dark greys: `appbar-background` and `drawer-background` #27272f → `background` #32333d → `surface` #373740 (panels, menus, dialogs) → `surface-raised` Gray Darken 3 (tiles). Theater mode drops to `background-gray`, Gray Darken 4. `selection` is Purple Darken 4, a deep tint of `primary`, for the selected row and the current chapter. Elevate with a higher step, not with a shadow.
- Text is `text-primary` (Gray Lighten 4) and `text-secondary` (Gray Lighten 1). Both reach at least 5.3:1 on every ground, `surface-raised` and `selection` included.
- Every bright fill takes `gray-darken-4` text through its `*-contrast-text` token (at least 5:1). Never put white on a Material accent. `twitch` is the one exception, with a white label.
- `primary` marks the one main action per region, the selected filter chip, active toggles, the focus ring and progress. It is text-safe on every ground except `surface-raised`, where it is icon-only.
- **Reserved colours.** `rec` means "a capture is being written right now": the pulsing REC dot, the REC chip, the live-edge tick and the live ring on an avatar. It is the brighter Red A2 and only ever a fill or a dot. `error` is the lighter Red A1, so error text stays legible, and an error always carries the warning icon or a word. `warning` marks partial captures, reconnecting, low disk and the polling fallback. `success` marks a connected Twitch account and healthy service. `twitch` is Twitch's own purple, kept exact, and appears only on the Connect with Twitch button.
- The player keeps its own fixed colours: `player-background` (black), `player-scrim` behind its strips, `player-text` over them, and `player-accent` (= `primary`) for the played range. No gradients.
- In stats charts, video series are `primary`, network series `secondary`, and the viewer-count chart `tertiary`. Chapter ticks and offsets are `tertiary` too: indigo always means the stream itself. Draw one series per sparkline, and set labels in text tokens, never in the series colour.

## Typography

- Type is **MudBlazor's own typography**: its default `Typography` from 9.12.0, unchanged, in **Roboto** (falling back to Helvetica, then Arial). Sizes, weights, line heights and letter-spacing are MudBlazor's for every slot. The single override is `Button.TextTransform = "none"`, so buttons keep sentence case.
- Roboto is self-hosted as one variable font, subset to Latin (see **MudBlazor theme**). The app makes no font requests to a CDN.
- MudBlazor's H1–H3 are display sizes (96, 60 and 48px), too large for a dense tool, so the app starts at H4. Use the slots this way:
  - `h4` (34px) for empty and not-found headlines;
  - `h5` (24px) for page titles and the recording title under the player;
  - `h6` (20px, Medium) for panel headers and dialog titles;
  - `subtitle1` (16px) for streamer names, display names, the player-strip title and stats section heads;
  - `subtitle2` (14px, Medium) for library row titles (two lines, then ellipsis) and chapter titles;
  - `body2` (14px) as the default UI text, `body1` (16px) for dialog copy, and `caption` (12px) for dates, durations and logins;
  - `overline` (12px, uppercase, wide tracking) for status chips and stat labels only.
- There is no monospace face. Numbers that update or line up (`timecode`, `stat-value`, `stat-hero`) are Roboto with `font-variant-numeric: tabular-nums`, so the digits keep a fixed width and never jitter.
- Two additions sit outside MudBlazor's `Typo` enum, built from its metrics: `label` (caption size at Medium, for day headers and filter chips) and `wordmark` (h6 size at Bold).
- Weight carries hierarchy the MudBlazor way: Light 300 only in the unused display sizes, Regular 400 for text and most headings, Medium 500 for h6, subtitle2, buttons and labels, and Bold 700 only in the wordmark.
- Use **sentence case everywhere** except `overline` (REC, PROCESSING, PARTIAL).
- Mark search matches with `MudHighlighter` (`Class="sk-mark"`), styled `highlight` behind `highlight-contrast-text`.

## Shape, spacing, elevation

- Spacing uses MudBlazor's 4px unit: `space-1` 4 (`pa-1`) · `space-2` 8 · `space-3` 12 · `space-4` 16 · `space-6` 24 · `space-8` 32. Use Mud's spacing classes (`pa-4`, `gap-3`, `mb-3`), not inline pixels.
- Corners: `radius-md` (8px) is `DefaultBorderRadius` and covers buttons, fields, menus, thumbnails and tiles. `radius-lg` (12px) is for the player frame, every panel and dialogs. `radius-full` is for chips, avatars, icon buttons and the seek knob. `radius-sm` (4px) is for duration badges and progress bars.
- Panels are flat: `MudPaper Elevation="0" Outlined="true"` on `surface`, with a `lines-default` hairline. The app bar is `Elevation="0"` with a `divider` rule under it. Shadows appear only where something floats: `elevation-8` on menus, popovers and snackbars, `elevation-24` on dialogs, and `elevation-4` on the temporary drawer.

## States and motion

- State layers: the content colour at `hover-opacity` (8%) on hover and `ripple-opacity` (10%) when pressed. Disabled content is at `disabled-opacity` (38%) through `text-disabled` and `action-disabled`.
- Tinted status chips and alerts fill with the status colour at `tint-opacity` (14%). Their label stays `text-primary`, and only the icon or dot takes the status colour, so every chip reads the same way.
- The focus ring is a solid 2px `primary` outline with a 2px offset on every interactive element (≥ 4:1 on every ground). On the Twitch button it lands on the app bar, not on the purple. Inside the player it is `player-accent`.
- Collapse and expand take 300ms with `cubic-bezier(0.2, 0, 0, 1)`. Player overlays fade in over 200ms and hide after 3s idle while playing. Under `prefers-reduced-motion`, the REC pulse and spinners stop, and ripples turn off (`Ripple="false"`).

## Content and voice

- Keep it short, plain and specific. Say what the machine is doing: "Recording now", "Processing", "Partial: gaps in capture", "1.2 TB free", "Live updates paused".
- Dates are relative for the last two days ("Today", "Yesterday"), then "Wed, Nov 4". Durations are "2h 14m" in meta lines and "2:14:07" in timecodes.
- Buttons are verbs: "Connect with Twitch", "Export MP4", "Copy playlist URL", "Stop recording". No exclamation marks, no emoji.
- Destructive dialogs name the thing and the consequence: "Delete recording?" followed by "The files and watch progress are removed permanently." Their confirm button repeats the verb ("Delete"), not "OK".
- Features that need a Twitch login stay visible but disabled, with a tooltip: "Connect Twitch to use this".
- Name a control's keyboard shortcut in its tooltip: "Theater mode (T)". See `Keyboard`.

## Iconography

- In the app, use MudBlazor's built-in **Material Rounded** SVG set (`Icons.Material.Rounded.*`). It adds no font file and no network request, and the icons inherit `currentColor`. This replaces the Material Symbols webfont.
- Sizes: `Size.Medium` (24px) in the app bar, drawer and player; `Size.Small` (20px) in chips, menus and inside buttons (`StartIcon`). Fill shows an "on" state only (play, REC).
- **Use icons to carry meaning, not decoration.** Every navigation destination, section header, toggle and meta field gets one, always beside its text label or with an `aria-label`. Use the same icon for the same idea everywhere:

| Meaning | Icon (`Icons.Material.Rounded.*`) |
|---|---|
| Home · Library · Settings | `Home` · `VideoLibrary` · `Settings` |
| Recording now (live capture) · recent | `Sensors` · `History` |
| Filter: all · recording · saved | `ViewList` · `FiberManualRecord` (in `rec`) · `Inventory2` |
| Chapters · stats · chat | `Toc` · `QueryStats` · `Chat` |
| Video · network · viewers (stream) | `Videocam` · `NetworkCheck` · `Groups`, in their series colours |
| Category · started · duration · parts · size | `SportsEsports` · `Event` · `Timer` · `FolderOpen` · `Storage` |
| Twitch account · channels · connection | `ManageAccounts` · `LiveTv` · `Wifi` |
| Player | `PlayArrow` / `Pause`, `Replay10` / `Forward10`, `VolumeUp` / `VolumeOff`, `QueryStats`, `Chat`, `PictureInPictureAlt`, `WidthWide` (theater), `Fullscreen` |
| Actions | `PlaylistPlay` (copy playlist URL) · `Output` (export MP4) · `Download` (download an export) · `ContentCopy` (copy stats) · `Delete` · `Stop` · `Refresh` · `Add` |
| Recording without a thumbnail | `Movie` |
| Back · menu · search · more | `ArrowBack` · `Menu` · `Search` · `MoreVert` |
| GitHub link · Twitch link | `Icons.Custom.Brands.GitHub` · Twitch's official Glitch mark (never redrawn; `LiveTv` stands in until the asset is added) |
| States | `CheckCircle`, `Warning`, `Error`, `Sync`, `CloudOff`, `WifiOff`, `Schedule` (polling) |

- The previews draw their icons from `components/icons.js`, which holds the exact SVG of the `Icons.Material.Rounded` constants from MudBlazor 9.12.0. In the app, use the constants. Inside JS-owned islands and the reconnect dialog, paste the same SVG.
- No Twitch logo is included yet. The Connect with Twitch button uses `Link`, and the app bar's Twitch link shows `LiveTv` as a stand-in. When shipping, use Twitch's official Glitch mark from its brand assets in both places, unmodified. Never redraw it. The GitHub link uses MudBlazor's own `Icons.Custom.Brands.GitHub`.

## Logo

- **The mark:** three stream segments (cyan, `secondary`) flowing into a rounded keep that holds a play symbol (magenta, `primary`). It means a video stream, saved. Files and rules are in the **Logos** and **Favicon** asset groups.
- **In the app bar**, the mark (28px; 24px in the dense bar) sits before the typed wordmark: "Stream" in `text-primary`, "Keepr" in `primary`, in `wordmark` type. While anything records, a pulsing `rec` badge sits on the mark's top-right corner. The logo itself never carries red.
- **Elsewhere** (README, social cards, docs), use the outlined lockup `streamkeepr-logo.svg` on dark grounds and `streamkeepr-logo-on-light.svg` on light ones. Keep clear space of at least half the mark's height. The minimum size is 16px for the mark and 96px wide for the lockup.
- **The favicon** is the mark on the dark app tile. Link `favicon.svg`, `favicon.ico`, `apple-touch-icon.png` and the manifest icons as the Favicon group's README shows.
