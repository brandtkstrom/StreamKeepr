Focus rings on every ground, and the keyboard shortcuts StreamKeepr already defines (`shortcuts.js`, `player-keys.js`, `library-keys.js`, `ChatPane.razor.js`).

## Focus

- Every interactive element shows a solid 2px ring with a 2px offset when focused from the keyboard (`:focus-visible`): `primary` everywhere, `player-accent` inside the player. Set it once in app CSS (for example on `.mud-button-root`, `.mud-chip` and the island controls), so it looks the same on MudBlazor components and on StreamKeepr's own markup.
- Text fields show focus with their own 2px `primary` border (MudTextField does this), not an extra ring.
- Library channel and recording rows sit edge to edge, so their ring is inset (`outline-offset: -2px`) and stays inside the drawer.
- The chat log, the Stats overlay's buttons and the speed menu's items sit edge to edge in their islands too, so their rings are inset the same way (#180).
- On the Twitch button the ring sits on the app bar ground, so it is told apart from the purple fill.

## Shortcuts

| Keys | Action | Owner |
|---|---|---|
| Space, K | Play or pause | `player-keys.js` |
| J / L | Back / forward 10 s | `player-keys.js` |
| ← / → | Back / forward 5 s (not inside sliders or the library) | `player-keys.js` |
| M · F · T · I | Mute · full screen · theater · Statistics overlay | `player-keys.js` |
| C | Open or close chat | `ChatPane.razor.js` |
| Esc (with nothing else open) | Close the open chat drawer; it yields first to the library, a dialog, a menu, a popover, the settings sheet, a field being typed in, or fullscreen | `ChatPane.razor.js` |
| / | Focus search | `shortcuts.js` |
| Esc (in the settings sheet or on the Settings page) | Close or exit Settings | `SettingsSheet`, `SettingsPage` |
| Esc | Clear the search | `shortcuts.js` |
| ↑ ↓ · → or Enter · ← or Esc · Home End | Move · open a channel's recordings column (focus moves in) · back to the channel · first/last | library keys (replaces `tree-nav.js`) |
| Delete | On a focused recording in the library: open "Delete recording?" (does nothing while the row's Delete is blocked) | library keys |

## Rules

- Single-letter keys never fire while typing in a field, while a menu or dialog is open (`[role=menu]`, `dialog[open]`, and MudBlazor popovers once migrated), or with a modifier held. Keep that guard when menus move to `MudMenu`. Its popover must still match the selectors, or the guard needs Mud's `.mud-popover-open`.
- Name the shortcut in the control's tooltip ("Theater mode (T)") and in `aria-keyshortcuts`. *As built in #180 the player's controls have no tooltips yet; the player's Chat toggle carries `aria-keyshortcuts="C"` and the chat drawer's Close control carries `aria-keyshortcuts="Escape"` without a tooltip (#333).*
- Key caps in documentation use `timecode` type on `surface-raised` with a `lines-inputs` border, at `radius-sm`.
