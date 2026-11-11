# Repo notes

- Exported from the Claude design-system artifact on 2026-11-10; `manifest.json` is v3 and documents nine components.
- The source of truth for tokens and components is this directory. The built CSS is copied/generated into `src/StreamKeepr/wwwroot/css/` (`tokens.css`, `components.css`, `fonts.css`).
- The source `components/bundle.css` uses a local Material Symbols `@font-face`; it makes no Google Fonts request.
- Material Symbols Rounded is Apache-2.0 and committed; the UI otherwise uses the system monospace stack.
- Component previews read `tokens.css`/`bundle.css` and the local Material Symbols file. `components/bundle.js` is intentionally absent; build the components from the READMEs and preview markup.
