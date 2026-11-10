# Design and Planning Assets

Status: partial — open-license assets are vendored; the Claude design-system artifact is still sign-in gated and must be exported by the owner.

## Asset inventory

| Asset | Status | Source | Repo destination | License / notes |
|---|---|---|---|---|
| Implementation plan | Present | `/Users/brandt/Downloads/TEMP/StreamKeepr_Implementation_Plan_Final.md` | `docs/plans/streamkeepr-implementation-plan.md` | Project document; SHA-256 `5323c9e4…74056b5` |
| Design-system tokens and components | **Missing** | Claude artifact `https://claude.ai/artifact/MaLyAQVzrTg2Wg6rSiEjAx` | `design-system/` | User-generated; export pending |
| Design-system previews and component READMEs | **Missing** | Same artifact | `design-system/components/<Comp>/` | User-generated; export pending |
| Material Symbols Rounded | Present | `material-symbols@0.48.3` (`material-symbols-rounded.woff2`) | `src/StreamKeepr/wwwroot/fonts/material-symbols-rounded.woff2` | Apache-2.0; license copied |
| MonoLisa woff2, 46 faces | Present locally, **not committed** | `/Users/brandt/Downloads/monolisa-webfont/woff2/` | `src/StreamKeepr/wwwroot/fonts/monolisa/` (Git-ignored) | FaceType EULA; do not redistribute |
| MonoLisa `@font-face` CSS | Present | Derived from `/Users/brandt/Downloads/monolisa-webfont/monolisa.css` | `src/StreamKeepr/wwwroot/css/fonts.css` | Paths rewritten to `/fonts/monolisa/`; EULA notice retained |
| hls.js ESM 1.7.4 | Present | `https://cdn.jsdelivr.net/npm/hls.js@1.7.4/dist/hls.mjs` | `src/StreamKeepr/wwwroot/lib/hls.js/hls.mjs` | Apache-2.0; license copied |
| hls.js 1.6.15 (local cache) | Not used | `/Users/brandt/repos/go-ffmpeg/frontend/node_modules/hls.js` | — | Superseded by 1.7.4 |
| Dracula PRO palette | External reference | `/Users/brandt/Downloads/dracula-pro-v2.2.4/design/` | — | Design input; not vendored |
| Fake Streamlink/media fixtures | Deferred to testing | Plan / testing contract | `tests/fixtures/` | Generated at test time |

## MonoLisa license

- Purchase: Paddle order **#47490377**, 2023-03-03, line item **“MonoLisa - Plus”**.
- Current FaceType EULA: personal/developer plans permit personal website/webfont use; commercial or product use requires the Creator plan.
- Stream Keepr is a personal, non-commercial, self-hosted app. If it is distributed or offered commercially, confirm or upgrade the license with FaceType.
- **Do not commit or publish the woff2 files.** The public repository must not redistribute the font data. The files stay in the local working tree and the private Docker build context; a fresh clone must copy them from the licensed purchase.
- The `LICENSE-NOTES.md` and `.gitignore` in `src/StreamKeepr/wwwroot/fonts/monolisa/` record the constraint.

This is a recorded constraint, not legal advice.

## Design-system artifact export

The artifact is sign-in gated. To complete this task:

1. Open `https://claude.ai/artifact/MaLyAQVzrTg2Wg6rSiEjAx` and sign in as the owner.
2. Use the artifact's export/download control, or copy the project files, and save them locally (for example `~/Downloads/streamkeepr-design-system/`).
3. Tell the agent the local path. It will unpack into `design-system/` and copy the built CSS into `src/StreamKeepr/wwwroot/css/`.

Expected files: `tokens.json`, `tokens.css`, `components/bundle.css` (or `components.css`), component READMEs and `preview.html` files, and `design-system.json`/`README.md` if present.

Rules:

- Do not commit MonoLisa woff2 files from the artifact; use the licensed local copies.
- Do not commit any secret or personal data from the artifact.
- If the artifact cannot be exported, the fallback is to reconstruct tokens and components from the plan, the Dracula PRO palette, MonoLisa, and Material 3. Record that decision before the frontend foundation starts.

## Local setup for a fresh clone

1. Copy the purchased MonoLisa `.woff2` files into `src/StreamKeepr/wwwroot/fonts/monolisa/`.
2. The CSS at `src/StreamKeepr/wwwroot/css/fonts.css` already points at that directory.
3. Build normally. The public repo does not contain the font binaries; the private image built from the working tree does.
4. For a CI container build that needs the fonts, provide them as a build secret or mount; do not publish them in a public image.

## Destinations

```text
design-system/                              # exported source of truth
  tokens.json
  tokens.css
  components/bundle.css
  components/<Comp>/README.md, preview.html
src/StreamKeepr/wwwroot/
  css/fonts.css                             # committed
  css/tokens.css, components.css            # from the exported design system
  fonts/monolisa/*.woff2                    # local only; Git-ignored
  fonts/material-symbols-rounded.woff2      # committed, Apache-2.0
  lib/hls.js/hls.mjs                        # committed, Apache-2.0
```
