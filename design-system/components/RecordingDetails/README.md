A collapsible panel under the player with the recording's title, its actions, its meta line, an export's progress and the chapter strip.

## Build it from

```razor
@* Not MudExpansionPanel: in MudBlazor 9.12 its header is a focusable div with no button role
   or aria-expanded. The header here is a real disclosure button over a MudCollapse. *@
<MudPaper Outlined="true" Elevation="0" Class="sk-details">                      @* radius-lg, like every panel *@
  <button type="button" aria-expanded="@open" aria-controls="@bodyId" @onclick="Toggle">
    <MudIcon Icon="@Icons.Material.Rounded.Info" /><MudText Typo="Typo.h6" HtmlTag="h2">Recording details</MudText><MudText Typo="Typo.caption">@StartLabel</MudText>
  </button>
  <MudCollapse Expanded="@Preferences.DetailsOpen">
    <div id="@bodyId" inert="@(!open)">
      <MudTooltip Text="@Recording.Title">
        <MudText Typo="Typo.h5" HtmlTag="h3" tabindex="0">@Recording.Title</MudText>  @* its own row, two lines max *@
      </MudTooltip>
      <div class="sk-details-row">
        <div class="sk-details-meta"> live chip · streamer · category · start · duration · parts · size </div>
        <div class="sk-actions">
          <MudButton Variant="Variant.Outlined" Color="Color.Error" StartIcon="@Icons.Material.Rounded.Stop">Stop recording</MudButton> @* live only; ≥ Md *@
          <MudButton Variant="Variant.Outlined" StartIcon="@Icons.Material.Rounded.PlaylistPlay">Copy playlist URL</MudButton>
          <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Rounded.Output">Export MP4</MudButton>
          <MudIconButton Icon="@Icons.Material.Rounded.Delete" aria-label="Delete recording" />                                       @* ≥ Md *@
          <MudMenu Icon="@Icons.Material.Rounded.MoreVert" AriaLabel="More actions"> Stop recording · Delete recording </MudMenu> @* < Md *@
        </div>
      </div>
      …export row (MudProgressLinear Color.Secondary), chapters…
    </div>
  </MudCollapse>
</MudPaper>
```

## Rules

- **The title has a row of its own**, the full width of the panel, in `h5`, wrapping to two lines before it ellipsises. The full title is in a tooltip that opens on hover over the text and on keyboard focus (the title is a tab stop). Nothing shares that row. Below it, one row carries the meta line on the left and the actions on the right; when space runs out, the actions wrap under the meta, never beside the title.
- Icons say what each action does: `PlaylistPlay` for Copy playlist URL (it copies the HLS playlist's address), `Output`, the generic export icon, for Export MP4, `Stop` for Stop recording and `Delete` for Delete. After an export finishes, Download uses `Download`.
- One filled button: Export MP4. Copy and Stop are outlined, and Stop is `error`. Delete is an icon button in `action-default` at rest that opens the `Dialog`.
- Actions that need Twitch stay visible. Wrap them in `MudTooltip` "Connect Twitch to use this" and disable them. A blocked export or delete gives its reason in the tooltip.
- Meta line, in `caption`, as icon + text pairs (`icon-inline` 16px icons in `action-default`): the live chip first while live (REC while recording, Reconnecting while reconnecting), the avatar and streamer, `SportsEsports` category, `Event` start, `Timer` duration, `FolderOpen` segments and parts, and `Storage` size (omitted while live).
- It is expanded by default directly under the player, so on desktop the title and actions show without scrolling. Below `Breakpoint.Md` Stop recording and Delete move into a `MoreVert` "More actions" menu; a blocked Delete is a disabled item showing its reason.
- **Not built yet:** below `Breakpoint.Md` the title, meta and actions should sit under the player outside any panel, with the rest (export row, chapters) as the **Details** tab (`MudTabPanel Icon="@Icons.Material.Rounded.Info"`). That waits for the phone watch tabs (PhoneLayout); until then the whole card is the one collapsible panel at every width.
- The export row sits on `surface-raised`: an `Output` icon in `secondary`, state text, `MudProgressLinear` in `secondary` with the percent in `timecode` while running (the bar is not a live region; the state text is the one announcement), then Cancel, Retry or a tonal Download link (a real `<a download>`). The tonal link is a 14% `primary` tint behind a `text-primary` label, because `primary` text is under 4.5:1 on `surface-raised`.
- Chapters are buttons with the offset in `timecode` (`tertiary`), the title in `subtitle2` and the game in `caption`. On hover the row is `surface-raised` and the offset turns `text-primary`, because `tertiary` text is under 4.5:1 there.
- **Not built yet:** the current chapter shown as `selection`. It needs the player's position in the details card.
