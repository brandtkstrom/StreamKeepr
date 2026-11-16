One recording in the library: a 16:9 thumbnail with its duration and watch progress, a two-line title, a meta line and, when it applies, one status chip.

## Build it from

A real `<a class="sk-row" href="/r/{id}" role="treeitem" aria-current>` (so deep links and selection work without the circuit) containing:

- `.sk-thumb` (`thumb-width` 112px, `radius-md`, `surface-raised` placeholder with `Icons.Material.Rounded.Movie`), a lazy `<img alt="">`, a `timecode` duration badge on `player-scrim`, and a 3px progress strip: `player-accent` for the resume position, a full `rec` strip while live.
- The title in `MudText Typo.subtitle2` (two lines, then ellipsis), with search matches in `<MudHighlighter Text="@Recording.Title" HighlightedText="@Search" Class="sk-mark" />` (the class lands on each `<mark>`).
- The meta line in `caption`: "Started 21:04 · 1h 02m" while live, then "20:30 · 2h 14m".
- `StatusChip`: Rec, Processing or Partial. Saved rows carry no chip.

## Rules

- Hover lays `text-primary` at `hover-opacity` over the row. Selected rows are `selection`, with every line in `text-primary`.
- One chip at most per row. The thumbnail never carries text other than the duration.
