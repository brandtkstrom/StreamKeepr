The title block directly under the player: the recording's title and meta line on the left, its actions as a compact row of icon buttons with a "More actions" menu on the right, and an export's progress below. It sits on the page, outside any panel.

Built from `RecordingDetailsSummary` (title, meta line, actions, stop/delete dialogs) and the export row. The chapter list that used to share its panel is now its own panel, `Chapters`. The collapsible "Recording details" panel is gone.

## Build it from

```razor
<div class="sk-vhead">                                                     @* no MudPaper: on the page ground *@
  <div class="sk-vinfo">
    <MudTooltip Text="@Recording.Title">
      <MudText Typo="Typo.h5" HtmlTag="h2" tabindex="0">@Recording.Title</MudText>   @* two lines max *@
    </MudTooltip>
    <div class="sk-details-meta"> live chip · streamer · category · start · duration · parts · size </div>
  </div>
  <div class="sk-vacts" role="group" aria-label="Recording actions">            @* one row, top right *@
    <MudTooltip Text="Copy playlist URL"><MudIconButton Icon="@Icons.Material.Rounded.PlaylistPlay" aria-label="Copy playlist URL" /></MudTooltip>
    <MudTooltip Text="Export MP4"><MudIconButton Icon="@Icons.Material.Rounded.Output" Color="Color.Primary" aria-label="Export MP4" /></MudTooltip> @* tonal *@
    <MudMenu Icon="@Icons.Material.Rounded.MoreVert" aria-label="More actions" AnchorOrigin="Origin.BottomRight" TransformOrigin="Origin.TopRight">
      <MudMenuItem Icon="@Icons.Material.Rounded.Stop" IconColor="Color.Error">Stop recording</MudMenuItem>   @* live only *@
      <MudDivider />
      <MudMenuItem Icon="@Icons.Material.Rounded.Delete">Delete recording</MudMenuItem>
    </MudMenu>
  </div>
</div>
…export row (MudProgressLinear Color.Secondary), full width under the block…
```

## Rules

- **Two sides.** The left side takes the remaining width: the title in `h5` (the page's `h2`), wrapping to two lines before it ellipsises, then the meta line under it. The right side is the actions, one row of 40px icon buttons (about 130px in all), its top lined up with the title's first line. It never wraps and never squeezes the title, so the old narrow-column rule is gone.
- **What shows and what is in the menu.** The everyday actions are icon buttons, in this order: Copy playlist URL (`PlaylistPlay`), Export MP4 (`Output`), then `MoreVert` "More actions". The menu holds the rare and destructive ones: Stop recording (`Stop`, `error`, live only), a divider, Delete recording (`Delete`). Both open the `Dialog` as before. After an export finishes, Download stays in the export row.
- Every icon button has a `MudTooltip` with its full name and an `aria-label` that matches it. The menu button has `aria-haspopup="menu"` and `aria-expanded`; the menu opens below it, right-aligned, on `surface` with `elevation-8`.
- Export MP4 is the one emphasised action: a tonal icon button, `primary` on a 14% `primary` tint. Copy and More are plain (`action-default`). While an export runs, Export is disabled with the tooltip "Exporting…", and the export row below shows the progress.
- Actions that need Twitch stay visible. Wrap them in `MudTooltip` "Connect Twitch to use this" and disable them. A blocked export gives its reason in its tooltip; a blocked Stop or Delete is a disabled menu item with its reason under the label in `caption` ("Stop the recording first").
- Meta line, in `caption`, as icon + text pairs (`icon-inline` 16px icons in `action-default`): the live chip first while live (REC while recording, Reconnecting while reconnecting), the channel's avatar (a 24px `ChannelAvatar` with the channel's Twitch profile image, the same image as in the drawer; its initial on its channel colour when there is no image) and the streamer, `SportsEsports` category, `Event` start, `Timer` duration, `FolderOpen` segments and parts, and `Storage` size (omitted while live).
- The full title is in a tooltip that opens on hover over the text and on keyboard focus (the title is a tab stop).
- **The export row** shows under the block, full width, only while an export runs or has finished: on `surface-raised`, an `Output` icon in `secondary`, state text, `MudProgressLinear` in `secondary` with the percent in `timecode` while running (the bar is not a live region; the state text is the one announcement), then Cancel, Retry or a tonal Download link (a real `<a download>`). The tonal link is a 14% `primary` tint behind a `text-primary` label, because `primary` text is under 4.5:1 on `surface-raised`.
- **Below `Breakpoint.Md`** the block keeps its phone form (see `PhoneLayout`): the title in `h6` and a short meta line, with the same icon row and menu at its top right. The export row follows it.
