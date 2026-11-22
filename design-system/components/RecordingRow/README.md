One recording in the library: a 16:9 thumbnail with its duration and watch progress, a two-line title, a meta line and, when it applies, one status chip.

## Build it from

A `<div class="sk-row-item" role="listitem">` inside the library's recordings column (`LibraryDrawer`; it was a `treeitem` in the old tree) holding two siblings: the row itself, a real `<a class="sk-row" href="/r/{id}" aria-current>` so deep links and selection work without the circuit, and the row's Delete button (see Rules). The button is never inside the link. The link contains:

- `.sk-thumb` (`thumb-width` 112px, `radius-md`, `surface-raised` placeholder with `Icons.Material.Rounded.Movie`), a lazy `<img alt="">`, a `timecode` duration badge on `player-scrim`, and a 3px progress strip: `player-accent` for the resume position, a full `rec` strip while live.
- The title in `MudText Typo.subtitle2` (two lines, then ellipsis), with search matches in `<MudHighlighter Text="@Recording.Title" HighlightedText="@Search" Class="sk-mark" />` (the class lands on each `<mark>`).
- The meta line in `caption`: "Started 21:04 · 1h 02m" while live, then "20:30 · 2h 14m".
- `StatusChip`: Rec, Processing or Partial. Saved rows carry no chip.

## Rules

- Hover lays `text-primary` at `hover-opacity` over the row. Selected rows are `selection`, with every line in `text-primary`.
- **Delete.** A `MudIconButton` (`Icons.Material.Rounded.Delete`, `Size.Small`, 32px, `action-default`) at the row's top right, with `aria-label="Delete recording: {title}"` and a `MudTooltip` "Delete recording". It shows on hover, on keyboard focus anywhere in the row, and always on the selected row; on touch screens (`hover: none`) it always shows. Hidden means `opacity: 0`, never `display: none`, so it stays in the tab order. The link reserves its width on the right, so titles do not reflow when it appears. Hovering it turns it `error` on a 12% `error` tint. It opens the `Dialog` "Delete recording?", the same one as the title block's More menu.
- **When Delete is blocked**, the button stays visible but disabled, with the reason in its tooltip, the same reasons as the title block: "Available when the recording finishes" while live, "Cancel the export before deleting" while an export runs, and "Connect Twitch to use this" when signed out.
- One chip at most per row. The thumbnail never carries text other than the duration.
