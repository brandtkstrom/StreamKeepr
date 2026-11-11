# Design and Planning Assets

Status: complete (2026-11-10). The Claude design-system export is installed; Material Symbols Rounded is the only bundled webfont.

## Asset inventory

| Asset | Status | Source | Repo destination | License / notes |
|---|---|---|---|---|
| Implementation plan | Present | `/Users/brandt/Downloads/TEMP/StreamKeepr_Implementation_Plan_Final.md` | `docs/plans/streamkeepr-implementation-plan.md` | SHA-256 `5323c9e4…74056b5` |
| Design-system source | Present | Claude artifact export, 2026-11-10 | `design-system/` | Manifest v3; 9 components; tokens and previews |
| Built CSS | Present | Generated from `design-system/` | `src/StreamKeepr/wwwroot/css/` | `tokens.css`, `components.css`, `fonts.css` |
| Material Symbols Rounded | Present | `material-symbols@0.48.3` | `src/StreamKeepr/wwwroot/fonts/material-symbols-rounded.woff2`; `design-system/fonts/` | Apache-2.0; license copied |
| hls.js ESM 1.7.4 | Present | `https://cdn.jsdelivr.net/npm/hls.js@1.7.4/dist/hls.mjs` | `src/StreamKeepr/wwwroot/lib/hls.js/hls.mjs` | Apache-2.0; license copied |
| hls.js 1.6.15 (local cache) | Not used | `/Users/brandt/repos/go-ffmpeg/frontend/node_modules/hls.js` | — | Superseded by 1.7.4 |
| Dracula PRO palette | Reference | `/Users/brandt/Downloads/dracula-pro-v2.2.4/design/` | — | Design input; not vendored |
| Fake Streamlink/media fixtures | Deferred to testing | Testing contract | `tests/fixtures/` | Generated at test time |

## Design-system export

The export is at `design-system/` with:

- `tokens.json`, `tokens.css`, `design-system.json`, `manifest.json`, `README.md`
- `components/bundle.css`, nine component `README.md`/`preview.html` pairs
- `api/tokens.md` and `api/components/*.md`

The built CSS in `src/StreamKeepr/wwwroot/css/` is generated from the source:

- `tokens.css` — tokens only; no `@font-face` rules.
- `components.css` — components only; the Google Fonts import and all `@font-face` rules are removed.
- `fonts.css` — the local Material Symbols Rounded face, with the path under `/fonts/`.

The source `components/bundle.css` has the Google Fonts import replaced by a local Material Symbols `@font-face` pointing at `../fonts/material-symbols-rounded.woff2`, so previews make no Google Fonts requests.

`components/bundle.js` is intentionally absent: the export documents the components without a runnable bundle.

## Typography

- UI text uses the system monospace stack (`ui-monospace`, SF Mono, Menlo, Consolas); no text webfont is bundled.
- Material Symbols Rounded is vendored under Apache-2.0 for icons.
- No Google Fonts or other CDN request is made at runtime.
- A future text face must be licensed for web self-hosting, added under `design-system/fonts/`, declared in the design-system source, and regenerated into `wwwroot/css/`.

## Local setup for a fresh clone

No font setup is required: Material Symbols Rounded is committed. Build normally.

## Destinations

```text
design-system/                              # exported source of truth
  tokens.json, tokens.css
  design-system.json, manifest.json, README.md
  components/bundle.css
  components/<Comp>/README.md, preview.html
  api/tokens.md, api/components/*.md
  fonts/                                    # Material Symbols Rounded, committed
src/StreamKeepr/wwwroot/
  css/tokens.css, components.css, fonts.css # generated/committed
  fonts/material-symbols-rounded.woff2      # committed, Apache-2.0
  lib/hls.js/hls.mjs                        # committed, Apache-2.0
```
