# Design and Planning Assets

Status: complete (2026-11-10); updated 2026-11-15 for the MudBlazor design system (ADR 0012, #173) and 2026-11-17 for the migration cleanup (#181). Roboto is the only webfont; icons are MudBlazor's SVGs.

## Asset inventory

| Asset | Status | Source | Repo destination | License / notes |
|---|---|---|---|---|
| Implementation plan | Present | `/Users/brandt/Downloads/TEMP/StreamKeepr_Implementation_Plan_Final.md` | `docs/plans/streamkeepr-implementation-plan.md` | SHA-256 `5323c9e4…74056b5` |
| Design-system source | Present | claude.ai StreamKeepr design-system artifact (MudBlazor 9.12.0), export of 2026-11-15; replaces the 2026-11-10 Dracula PRO export | `design-system/` | Authoritative from here (ADR 0012); the artifact is re-synced from the repo. See `design-system/REPO-NOTES.md` for changes made on import |
| Built CSS | Present | `tokens.css` generated from `design-system/tokens.json` by `scripts/build-tokens.mjs`; `components.css` and `fonts.css` hand-written | `src/StreamKeepr/wwwroot/css/` | Drift checked by `tests/js/tokens.test.js` |
| Roboto (variable) | Present | `google/fonts` `ofl/roboto/Roboto[wdth,wght].ttf` at commit `6183fc0d26361f6ddfd6f6b7a736e1467c6d8a43`, SHA-256 `d7598e12c5dbef095ff8272cfc55da0250bd07fbdecbac8a530b9b277872a134` (488,584 bytes); licence `OFL.txt` from the same commit, SHA-256 `061402327a96aadb0bfb694a960ed289ecd38d383e396243831ab81feb109c41` | `src/StreamKeepr/wwwroot/fonts/roboto-latin.woff2` (36,564 bytes), `src/StreamKeepr/wwwroot/fonts/ROBOTO-LICENSE` | OFL-1.1; licence copied. Built by `scripts/build-roboto.sh`: `wght` 100–900 kept, `wdth` pinned at 100, Google Fonts' Latin range, default features plus `tnum`, reproducible (`SOURCE_DATE_EPOCH`) |
| Material Symbols Rounded | Retired | `material-symbols@0.48.3` | — | Removed with its licence and `@font-face` on 2026-11-17 (#181); MudBlazor's `Icons.Material.Rounded` SVGs replace it |
| Logo and favicon set | Present | Original artwork, 2026-11-15 (MudBlazor design system, Logos and Favicon groups) | `branding/` (all files and usage READMEs); `src/StreamKeepr/wwwroot/` (`favicon.svg`, `favicon.ico`, `apple-touch-icon.png`, `icon-192.png`, `icon-512.png`, `manifest.webmanifest`) | Project-owned; the wordmark is outlined from Roboto Flex Bold (OFL), so no font is embedded |
| hls.js ESM 1.7.4 | Present | `https://cdn.jsdelivr.net/npm/hls.js@1.7.4/dist/hls.mjs` | `src/StreamKeepr/wwwroot/lib/hls.js/hls.mjs` | Apache-2.0; license copied |
| hls.js 1.6.15 (local cache) | Not used | `/Users/brandt/repos/go-ffmpeg/frontend/node_modules/hls.js` | — | Superseded by 1.7.4 |
| Dracula PRO palette | Retired | `/Users/brandt/Downloads/dracula-pro-v2.2.4/design/` | — | Input to the 2026-11-10 design system; replaced by MudBlazor's palette on 2026-11-15 |
| Fake Streamlink/media fixtures | Deferred to testing | Testing contract | `tests/fixtures/` | Generated at test time |

## Design-system export

The export is at `design-system/` with:

- `tokens.json`: every design value (colour, type, spacing, radius, shadow, size, opacity), the single source;
- `README.md`, `mudblazor-theme.md` (the `MudTheme` and its wiring), `component-map.md` (which MudBlazor component replaces each primitive);
- `components/bundle.css`, `components/icons.js` and one `README.md`/`preview.html` pair per component;
- `design-system.json` (the artifact manifest) and `REPO-NOTES.md`.

The app's CSS in `src/StreamKeepr/wwwroot/css/`:

- `tokens.css`: generated from `tokens.json`; never edited by hand.
- `components.css`: the app's own rules over MudBlazor and the hand-built islands. A few remaining rules read `--sk-*` aliases pointed at `tokens.css`; new rules read `tokens.css` directly.
- `fonts.css`: the local Roboto face, with its path under `/fonts/`.

The previews make no font requests: their Google Fonts links were removed on import. In the repo they are markup references; the claude.ai artifact renders them with the tokens, `bundle.css` and Roboto.

## Typography

- UI text is Roboto, MudBlazor's default typography (falling back to Helvetica, then Arial). It is self-hosted as one variable `woff2` with `font-display: swap`; no monospace face is used. Numeric styles use `font-variant-numeric: tabular-nums`, which is why the subset keeps `tnum`.
- Icons are MudBlazor's `Icons.Material.Rounded` SVGs (`MudIcon`, or `IslandIcon` and `wwwroot/js/island-icons.js` in the JS-owned islands); no icon font is bundled.
- No Google Fonts or other CDN request is made for application assets at runtime. Chat emote images are message content and may load from the Twitch and third-party emote CDNs; they are not design-system assets.
- To rebuild Roboto (for example after a google/fonts update), change the pinned commit and SHA-256 in `scripts/build-roboto.sh`, run it, and update the table above. It needs `fonttools` and `brotli` (`pip install fonttools brotli`); it is dev-only.
- A further face must be licensed for web self-hosting, declared in `tokens.json`, and added to `fonts.css` with its licence under `wwwroot/fonts/`.

## Local setup for a fresh clone

No font setup is required: Roboto is committed. Build normally. Node (for `tests/js`) and fontTools (for `scripts/build-roboto.sh`) are only needed to regenerate assets.

## Destinations

```text
design-system/                              # exported source of truth (ADR 0012)
  tokens.json                               # every design value
  README.md, mudblazor-theme.md, component-map.md, REPO-NOTES.md
  design-system.json                        # artifact manifest
  components/bundle.css, icons.js
  components/<Comp>/README.md, preview.html
scripts/
  build-tokens.mjs                          # dev-only: tokens.json -> wwwroot/css/tokens.css
  build-roboto.sh                           # dev-only: Roboto[wdth,wght].ttf -> roboto-latin.woff2
branding/
  logo/                                     # mark and lockups, SVG + PNG, usage README
  favicon/                                  # favicon.svg/.ico, app icons, sources, README
src/StreamKeepr/wwwroot/
  favicon.svg, favicon.ico, apple-touch-icon.png, icon-192.png, icon-512.png
  manifest.webmanifest                      # linked from App.razor <head>
  css/tokens.css                            # generated, committed
  css/components.css, fonts.css             # hand-written, committed
  fonts/roboto-latin.woff2, ROBOTO-LICENSE  # committed, OFL-1.1
  lib/hls.js/hls.mjs                        # committed, Apache-2.0
```
