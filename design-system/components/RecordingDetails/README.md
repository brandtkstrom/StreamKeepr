The title block directly under the player: the recording's title and meta line on the left, its actions in a vertical stack on the right, and an export's progress below. It sits on the page, outside any panel.

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
  <div class="sk-vstack" role="group" aria-label="Recording actions">            @* 200px, right-aligned *@
    <MudButton Variant="Variant.Outlined" Color="Color.Error" StartIcon="@Icons.Material.Rounded.Stop" FullWidth="true">Stop recording</MudButton> @* live only *@
    <MudButton Variant="Variant.Outlined" StartIcon="@Icons.Material.Rounded.PlaylistPlay" FullWidth="true">Copy playlist URL</MudButton>
    <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Rounded.Output" FullWidth="true">Export MP4</MudButton>
    <MudButton Variant="Variant.Text" StartIcon="@Icons.Material.Rounded.Delete" FullWidth="true">Delete recording</MudButton>
  </div>
</div>
…export row (MudProgressLinear Color.Secondary), full width under the block…
```

## Rules

- **Two sides.** The left side takes the remaining width: the title in `h5` (the page's `h2`), wrapping to two lines before it ellipsises, then the meta line under it. The right side is the actions, stacked vertically in a 200px column, every button the full column width with its label left-aligned, in this order: Stop recording (live only), Copy playlist URL, Export MP4, Delete recording. The stack's top lines up with the title's first line.
- When the left column is narrower than 520px (a narrow window with chat open), the stack moves under the meta line and runs as a row; it never squeezes the title.
- Icons say what each action does: `Stop`, `PlaylistPlay` (it copies the HLS playlist's address), `Output` (the generic export icon) and `Delete`. After an export finishes, Download uses `Download`.
- One filled button: Export MP4. Copy and Stop are outlined, and Stop is `error`. Delete is a text button in `action-default`, labelled in the stack because a lone icon would not line up; it opens the `Dialog`.
- Actions that need Twitch stay visible. Wrap them in `MudTooltip` "Connect Twitch to use this" and disable them. A blocked export or delete gives its reason in the tooltip.
- Meta line, in `caption`, as icon + text pairs (`icon-inline` 16px icons in `action-default`): the live chip first while live (REC while recording, Reconnecting while reconnecting), the avatar and streamer, `SportsEsports` category, `Event` start, `Timer` duration, `FolderOpen` segments and parts, and `Storage` size (omitted while live).
- The full title is in a tooltip that opens on hover over the text and on keyboard focus (the title is a tab stop).
- **The export row** shows under the block, full width, only while an export runs or has finished: on `surface-raised`, an `Output` icon in `secondary`, state text, `MudProgressLinear` in `secondary` with the percent in `timecode` while running (the bar is not a live region; the state text is the one announcement), then Cancel, Retry or a tonal Download link (a real `<a download>`). The tonal link is a 14% `primary` tint behind a `text-primary` label, because `primary` text is under 4.5:1 on `surface-raised`.
- **Below `Breakpoint.Md`** the block keeps its phone form (see `PhoneLayout`): the title in `h6`, a short meta line, then one row of actions with Stop recording and Delete in a `MoreVert` "More actions" menu; a blocked Delete is a disabled item showing its reason. The export row follows it.
