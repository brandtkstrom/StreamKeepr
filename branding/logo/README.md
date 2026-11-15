The StreamKeepr logo: stream segments (cyan) flowing into a keep that holds a play symbol (magenta). It means *a video stream, saved*.

| File | Use |
|---|---|
| `streamkeepr-logo.svg` | Horizontal lockup (mark + wordmark) on dark grounds: the app, the README on dark, social cards |
| `streamkeepr-logo-on-light.svg` | The same on white or light grounds (docs, GitHub light mode, print) |
| `streamkeepr-mark.svg` | The mark alone on dark grounds: the app bar, avatars, small spaces |
| `streamkeepr-mark-on-light.svg` | The mark alone on light grounds |
| `streamkeepr-logo@2x.png`, `streamkeepr-logo-on-light@2x.png` | 656×128 raster lockups, for places that don't take SVG |

- Colours are MudBlazor's own. On dark: segments `Colors.Cyan.Accent3` (#00e5ff), keep and play `Colors.Purple.Accent1` (#ea80fc), "Stream" `Colors.Gray.Lighten4` (#f5f5f5). On light: Cyan Darken3 (#00838f), Purple Darken1 (#8e24aa), Gray Darken4 (#212121).
- The wordmark is outlined from Roboto Flex Bold, so the SVGs need no font.
- Keep clear space of at least half the mark's height on every side. The minimum size is 16px for the mark and 96px wide for the lockup.
- Don't recolour, stretch, outline, add effects, or put it on a busy image. Never add the REC dot to the logo itself: red means "capturing now" and appears only as a live badge.
