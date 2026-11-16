The default view at `/` when no recording is open: active recordings in one row, and recent complete recordings in a second row below, both newest first.

## Build it from

`Home.razor` renders this in the main column whenever `RecordingId` is null. Opening a card navigates to `/r/{id}`, and the player column replaces this view.

```razor
<section aria-labelledby="sk-now">
    <div class="sk-shelf-head">
        <span class="sk-rec-dot" aria-hidden="true"></span>
        <MudText Typo="Typo.h6" id="sk-now">Recording now</MudText>
        <MudText Typo="Typo.caption">@active.Count</MudText>
    </div>
    @if (active.Count == 0) { <NothingRecording /> }
    else { <RecordingShelf Recordings="active" Size="CardSize.Large" Label="Recording now" /> }
</section>
<section aria-labelledby="sk-recent">
    <div class="sk-shelf-head">
        <MudText Typo="Typo.h6" id="sk-recent">Recent recordings</MudText>
        <MudText Typo="Typo.caption">Newest first</MudText>
        <MudSpacer />
        <MudButton Variant="Variant.Text" Size="Size.Small" OnClick="ShowAllSaved">Show all</MudButton>
        @* scroll buttons, mouse only *@
    </div>
    <RecordingShelf Recordings="recent" Label="Recent recordings" />
</section>
```

## Which recordings, in which order

- **Recording now** holds every recording whose state is `Recording` or `Reconnecting`: the same set as the library's Recording filter. It is never truncated.
- **Recent recordings** holds the 20 newest recordings whose state is `Saved` or `Partial`: the same set as the library's Saved filter.
- Both rows sort by start time, **newest to oldest**, like the library's default order. A recording moves from the first row to the second the moment its capture finishes. The view re-renders on `ILibraryView.Changed`, so no reload is needed.
- "Show all" sets the drawer's filter to Saved and opens the drawer (on narrow screens, the temporary drawer).

## The consumer provides

`ILibraryView` (recordings across all streamers, `IsLoaded`, `Changed`), streamer display names and avatar URLs, and the viewer's clock and time zone for "Started 21:04" and "Yesterday, 20:30".

## Layout

- The two sections stack in the main column, `space-8` apart, with no width cap: on wide screens the rows simply show more cards. Each has an `h6` header after a meaningful icon (`Sensors` in `rec` for Recording now, `History` for Recent recordings), with a `caption` beside it: the count for Recording now, "Newest first" for Recent recordings.
- A row is a horizontal list (`<ul>`, `scroll-snap-type: x`) with a `space-4` gap. It never wraps to a second line, and the last visible card is cut at the edge to show there is more.
- Active cards are large (360px), so live work stands out. Recent cards are 264px. See `RecordingCard`.
- With nothing recording, the first row is one dashed note: "Nothing is recording right now. A watched channel starts recording when it goes live." The heading stays, so the page doesn't jump when a capture starts.
- With no recordings at all, the whole view is the "No recordings yet" empty state (see `EmptyStates`).
- While the library loads, each row shows three `MudSkeleton` cards.

## Rules

- **Keyboard:** each card is a link in the tab order, and focusing one scrolls it into view. The chevron buttons scroll a row by its visible width for mouse users; they are `aria-hidden` with `tabindex="-1"`. Each list is labelled by its heading.
- **Below `Breakpoint.Md`:** rows become swipe rows with `pa-4` gutters, active cards at 85% and recent cards at 60% of the screen width, and the chevrons hide. The bottom bar (`BottomNav`) shows Home as current, and the library opens from it.
- The drawer stays where it is. Picking a recording in either the drawer or a row opens the same player view.

## Intentional additions

This view is new. Today `/` shows an empty player column. "Show all" is new too.
