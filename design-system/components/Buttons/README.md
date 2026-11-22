MudBlazor buttons, icon buttons, toggle icon buttons, filter chips, switches and menus as StreamKeepr themes them.

## Variants

- **Filled `Color.Primary`**: the one main action in a region (Add; Export MP4 is the tonal icon button in the title block). **Filled `Color.Error`**: the confirm in a destructive dialog (Delete, Stop recording).
- **Outlined**: secondary actions (Disconnect). The title block's actions are icon buttons and menu items, not outlined buttons (see `RecordingDetails`). The border is `lines-inputs`, and the label is `text-primary`.
- **Text**: low-emphasis and row actions (Retry, Copy stats; Cancel, Remove and Resume recording in `Color.Default`).
- **`MudIconButton`**: `action-default` at rest. **`MudToggleIconButton`** for stats and theater: `ToggledColor="Color.Primary"` and a 14% `primary` tint.
- **`MudChipSet`** filters: `Variant.Outlined`, `SelectedColor="Color.Primary"`, `CheckMark`.
- **`MudSwitch T="bool" Color="Color.Primary"`** for settings toggles.
- **`MudMenu`** with checkable `MudMenuItem`s: the checked item shows `Check` in `primary`, and unchecked items keep the icon slot empty so labels align.

## Rules

- Labels are verbs in sentence case (`Typography.Button.TextTransform = "none"`): "Export MP4", never "EXPORT MP4" or "OK".
- `StartIcon` only where the icon adds meaning: `PlaylistPlay` for Copy playlist URL, `Output` (the generic export icon) for Export MP4, `Stop` for Stop recording, `ContentCopy` only for plain copies such as Copy stats. Use `Size.Small` icons inside buttons.
- Disabled buttons that need Twitch get a `MudTooltip`: "Connect Twitch to use this".
- Every icon-only button has an `aria-label`. Toggles also set `aria-pressed`.
