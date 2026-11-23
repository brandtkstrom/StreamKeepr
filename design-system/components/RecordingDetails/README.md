The title block directly under the player: the recording's title and meta line on the left, the live chip and a "More actions" menu on the right, and an export's progress below. It sits on the page, outside any panel.

Built from `RecordingDetailsSummary` (title, meta line, actions, stop/delete dialogs) and the export row. The chapter list that used to share its panel is now its own panel, `Chapters`. The collapsible "Recording details" panel is gone.

## Build it from

```razor
<div class="sk-vhead">                                                     @* no MudPaper: on the page ground *@
  <div class="sk-vinfo">
    <MudTooltip Text="@Recording.Title">
      <MudText Typo="Typo.h5" HtmlTag="h2" tabindex="0">@Recording.Title</MudText>   @* two lines max *@
    </MudTooltip>
    <div class="sk-details-meta"> streamer · category · start · duration · parts · size </div>
  </div>
  <div class="sk-actions" data-details-actions>                                @* one row, top right *@
    <StatusChip />                                                            @* live only *@
    <div class="sk-actions-group" role="group" aria-label="Recording actions">
      <MudMenu Icon="@Icons.Material.Rounded.MoreVert" aria-label="More actions" AnchorOrigin="Origin.BottomRight" TransformOrigin="Origin.TopRight">
        <MudMenuItem Icon="@Icons.Material.Rounded.PlaylistPlay">Copy playlist URL</MudMenuItem>
        <MudMenuItem Icon="@Icons.Material.Rounded.Output">Export MP4</MudMenuItem>   @* disabled with a reason when blocked *@
        <MudDivider />
        <MudMenuItem Icon="@Icons.Material.Rounded.Stop" IconColor="Color.Error">Stop recording</MudMenuItem>   @* live only *@
        <MudDivider />
        <MudMenuItem Icon="@Icons.Material.Rounded.Delete">Delete recording</MudMenuItem>
      </MudMenu>
    </div>
  </div>
</div>
…export row (MudProgressLinear Color.Secondary), full width under the block…
```

## Rules

- **Two sides.** The left side takes the remaining width: the title in `h5` (the page's `h2`), wrapping to two lines before it ellipsises, then the meta line under it. The right side is the live chip (when live) and the `MoreVert` "More actions" button, its top lined up with the title's first line. It never wraps and never squeezes the title, so the old narrow-column rule is gone.
- **What is in the menu.** All four actions live in More actions, in this order: Copy playlist URL (`PlaylistPlay`), Export MP4 (`Output`), a divider, Stop recording (`Stop`, `error`, live only), a divider, Delete recording (`Delete`). Copy is public. Stop and Delete open the `Dialog` as before. After an export finishes, Download stays in the export row.
- The More actions button has `aria-haspopup="menu"` and `aria-expanded`; the menu opens below it, right-aligned, on `surface` with `elevation-8`. The status chip is a status, not an action; it stays outside the `role="group"` named "Recording actions".
- Export MP4 is the first privileged item in the menu. While an export runs, it is disabled with "Exporting…" under the label, and the export row below shows the progress.
- Actions that need Twitch stay visible. While signed out, Export, Stop and Delete are disabled menu items with "Connect Twitch to use this" on hover and in their accessible description; Copy stays enabled. A blocked export or Delete also shows its reason under the label in `caption` ("Available when the recording finishes", "Cancel the export before deleting").
- Meta line, in `caption`, as icon + text pairs (`icon-inline` 16px icons in `action-default`): the channel's avatar (a 24px `ChannelAvatar` with the channel's Twitch profile image, the same image as in the drawer; its initial on its channel colour when there is no image) and the streamer, `SportsEsports` category, `Event` start, `Timer` duration, `FolderOpen` segments and parts, and `Storage` size (omitted while live).
- The full title is in a tooltip that opens on hover over the text and on keyboard focus (the title is a tab stop).
- **The export row** shows under the block, full width, only while an export runs or has finished: on `surface-raised`, an `Output` icon in `secondary`, state text, `MudProgressLinear` in `secondary` with the percent in `timecode` while running (the bar is not a live region; the state text is the one announcement), then Cancel, Retry or a tonal Download link (a real `<a download>`). The tonal link is a 14% `primary` tint behind a `text-primary` label, because `primary` text is under 4.5:1 on `surface-raised`.
- **Below `Breakpoint.Md`** the block keeps its phone form (see `PhoneLayout`): the title in `h6` and a short meta line, with the same live chip and More actions row at its top right. The export row follows it.
