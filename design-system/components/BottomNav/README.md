The bottom app bar below `Breakpoint.Md`: three destinations, Home, Library and Settings, within thumb reach.

## Build it from

`<MudAppBar Bottom="true" Fixed="true" Elevation="0" Class="sk-bottombar">` (56px, `bottom-bar-height`), rendered until the breakpoint is known to be Md or wider and hidden by CSS at 960px and up (`MudHidden` is not used: it renders nothing until the circuit knows the breakpoint, so a phone's prerendered page would have no bottom-bar links), holding three equal links. Each link is an icon in a 56×28 pill above a `caption` label at Medium:

| Destination | Icon | Action |
|---|---|---|
| Home | `Home` | navigates to `/` |
| Library | `VideoLibrary` | opens the temporary library drawer |
| Settings | `Settings` | opens the full-screen settings dialog (`SettingsSheet`) over the current page; a link to `/settings` before the circuit connects and on that page |

## Rules

- The current destination gets `aria-current="page"`, `text-primary` for the label, and an 18% `primary` pill behind a `primary` icon. Library counts as current while the drawer is open, and Settings while the settings dialog is open; Away from the Settings page, Settings carries `aria-expanded`, and `aria-controls="sk-settings"` while the dialog is open.
- A `rec` dot badge on Library while anything is recording (`MudBadge Dot`, styled `rec`).
- Hidden on watch pages: the top bar's back arrow returns Home.
- The bar sits on `appbar-background` with a `divider` rule on top. Pad the page by `bottom-bar-height` so nothing hides beneath it, plus the safe-area inset on phones with a home indicator.
