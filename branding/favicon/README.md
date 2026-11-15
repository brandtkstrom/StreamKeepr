Favicons and app icons: the StreamKeepr mark on the app's dark tile (#27272f, MudBlazor's dark app-bar ground), so it reads on both light and dark browser tabs.

| File | Use | Markup |
|---|---|---|
| `favicon.svg` | Modern browsers (crisp at any size; a tighter crop for 16–32px) | `<link rel="icon" href="favicon.svg" type="image/svg+xml">` |
| `favicon.ico` (16, 32, 48) | Fallback for older browsers and Windows | `<link rel="icon" href="favicon.ico" sizes="48x48">` |
| `apple-touch-icon.png` (180, square; iOS rounds it) | iOS home screen | `<link rel="apple-touch-icon" href="apple-touch-icon.png">` |
| `icon-192.png`, `icon-512.png` | Web app manifest (Android home screen, installs) | `"icons": [{ "src": "icon-192.png", "sizes": "192x192" }, { "src": "icon-512.png", "sizes": "512x512" }]` |
| `app-icon.svg`, `app-icon-square.svg` | Sources for the PNGs (rounded tile, and square tile) | — |

`favicon.ico` is not in this asset store (the store doesn't take .ico); it sits beside this README in `branding/favicon/`.
