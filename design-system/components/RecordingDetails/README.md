A collapsible panel under the player with the recording's title, its actions, its meta line, an export's progress and the chapter strip.

## Build it from

```razor
<MudExpansionPanels Elevation="0" Outlined="true" Class="sk-panel">
  <MudExpansionPanel @bind-Expanded="Preferences.DetailsOpen">
    <TitleContent><MudIcon Icon="@Icons.Material.Rounded.Info" /><MudText Typo="Typo.h6">Recording details</MudText><MudText Typo="Typo.caption">@StartLabel</MudText></TitleContent>
    <ChildContent>
      <MudText Typo="Typo.h5" Class="sk-vtitle">@Recording.Title</MudText>   @* its own row, full width, two lines max *@
      <MudStack Row AlignItems="AlignItems.Center" Wrap="Wrap.Wrap" Spacing="4">
        <div class="sk-meta flex-grow-1"> REC chip (live only) · streamer · category · start · duration · parts · size </div>
        <MudStack Row Spacing="2" Wrap="Wrap.Wrap">
          <MudButton Variant="Variant.Outlined" Color="Color.Error" StartIcon="@Icons.Material.Rounded.Stop">Stop recording</MudButton> @* live only *@
          <MudButton Variant="Variant.Outlined" StartIcon="@Icons.Material.Rounded.PlaylistPlay">Copy playlist URL</MudButton>
          <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Rounded.Output">Export MP4</MudButton>
          <MudIconButton Icon="@Icons.Material.Rounded.Delete" aria-label="Delete recording" />
        </MudStack>
      </MudStack>
      …export row (MudProgressLinear Color.Secondary), chapters…
    </ChildContent>
  </MudExpansionPanel>
</MudExpansionPanels>
```

## Rules

- **The title has a row of its own**, the full width of the panel, in `h5`, wrapping to two lines before it ellipsises (the full title in a tooltip). Nothing shares that row. Below it, one row carries the meta line on the left (the REC chip first while live) and the actions on the right; when space runs out, the actions wrap under the meta, never beside the title.
- Icons say what each action does: `PlaylistPlay` for Copy playlist URL (it copies the HLS playlist's address), `Output`, the generic export icon, for Export MP4, `Stop` for Stop recording and `Delete` for Delete. After an export finishes, Download uses `Download`.
- One filled button: Export MP4. Copy and Stop are outlined, and Stop is `error`. Delete is an icon button that opens the `Dialog`.
- Actions that need Twitch stay visible. Wrap them in `MudTooltip` "Connect Twitch to use this" and disable them. A blocked export or delete gives its reason in the tooltip.
- Meta line, in `caption`, as icon + text pairs (16px icons in `action-default`): the avatar and streamer, `SportsEsports` category, `Event` start, `Timer` duration, `FolderOpen` segments and parts, and `Storage` size.
- On desktop it is expanded by default directly under the player, so the title and actions show without scrolling. Below `Breakpoint.Md` the title, meta and actions sit under the player outside any panel, and the rest (export row, chapters) is the **Details** tab (`MudTabPanel Icon="@Icons.Material.Rounded.Info"`). Stop recording and Delete move into a `MoreVert` menu there.
- The export row sits on `surface-raised`: an `Output` icon in `secondary`, state text, `MudProgressLinear` in `secondary` with the percent in `timecode` while running, then Cancel, Retry or a tonal Download link (a real `<a download>`). Announce state changes, not each percent.
- Chapters are buttons with the offset in `timecode` (`primary`), the title in `subtitle2` and the game in `caption`. The current chapter is `selection`.
