The recording's chapters, one row per chapter, in a collapsible panel under the title block. It replaces the old "Recording details" panel and shows only when the recording has chapters.

## Build it from

```razor
@if (Recording.Chapters.Count > 0)
{
  @* Not MudExpansionPanel: in MudBlazor 9.12 its header is a focusable div with no button role
     or aria-expanded. The header is a real disclosure button over a MudCollapse. *@
  <MudPaper Outlined="true" Elevation="0" Class="sk-panel sk-chapters">            @* radius-lg, like every panel *@
    <button type="button" aria-expanded="@open" aria-controls="@bodyId" @onclick="Toggle">
      <MudIcon Icon="@Icons.Material.Rounded.Toc" /><MudText Typo="Typo.h6" HtmlTag="h2">Chapters</MudText><MudText Typo="Typo.caption">@CountLabel</MudText>
    </button>
    <MudCollapse Expanded="@open">
      <div id="@bodyId" inert="@(!open)" role="group" aria-label="Chapters">
        @foreach (var chapter in Recording.Chapters)
        {
          <button type="button" class="sk-chapter" aria-current="@(IsCurrent(chapter) ? "true" : null)" @onclick="() => Seek(chapter)">
            <span class="time">@chapter.Offset</span><span class="title">@chapter.Title</span><span class="game">@chapter.Game</span>
          </button>
        }
      </div>
    </MudCollapse>
  </MudPaper>
}
```

## Rules

- **Hidden without chapters.** A recording with no chapters renders no panel at all, not an empty one; Stats for nerds moves up under the title block.
- The header: the `Toc` icon, "Chapters" in `h6` (the page's `h2`), and the count in `caption` ("1 chapter", "4 chapters"), then the chevron. It is expanded by default and remembers its open state per viewer in `UiPreferences` (the slot the old details panel used).
- Each chapter is a full-width button, one row: the offset in `timecode` (`tertiary`, at least 64px so offsets line up), the title in `subtitle2` taking the remaining width and ellipsising on one line, and the game in `caption`, `text-secondary`, at the right. Clicking seeks the player to the chapter's start.
- On hover the row is `surface-raised` and the offset turns `text-primary`, because `tertiary` text is under 4.5:1 there.
- The current chapter (the one the playhead is in) is drawn on `selection`, with every line in `text-primary`, and carries `aria-current="true"`. *Not built yet: it needs the player's position in the panel.*
- The list grows with the recording; there is no inner scroll on desktop. Live recordings add rows as chapters arrive.
- **Below `Breakpoint.Md`** the list is the **Chapters** tab under the player (`MudTabPanel Icon="@Icons.Material.Rounded.Toc"`), without the panel or its header; the tab is left out when there are no chapters.
