# Buttons

Material 3 buttons, icon buttons and filter chips used across StreamKeepr.

- Filled (`primary`) — one per view at most, for the main action (e.g. Record now).
- Tonal (`secondary-container`) — secondary actions such as Export.
- Outlined (`outline` border, `primary` label) — tertiary actions.
- Text — Cancel and inline actions in dialogs and snackbars.
- Danger — outlined with an `error` label, always followed by a confirmation.
- Icon buttons are 40px targets with 24px glyphs from Material Symbols Rounded; the active one (e.g. stats on) turns `primary`.
- Filter chips (`radius-sm`, 32px): selected chips use `secondary-container` and a check icon.
- All share the state-layer rule: an overlay of the content colour at `state-hover` / `state-pressed`, and a 2px `primary` focus ring with 2px offset.
