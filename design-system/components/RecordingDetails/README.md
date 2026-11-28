The title block directly under the player: the recording's title with the live chip and a "More actions" menu, a channel byline linking to Twitch, and a labelled fact slate. It sits on the page, outside any panel.

Built from `RecordingDetailsSummary` (title, channel byline, fact slate, actions, stop/delete dialogs) and the export row. The chapter list that used to share its panel is now its own panel, `Chapters`. The collapsible "Recording details" panel is gone.

## Build it from

```razor
<div class="sk-details-summary" data-details-summary>         @* no MudPaper: on the page ground *@
  <div class="sk-details-head">
    <MudTooltip Text="@Recording.Title">
      <MudText Typo="Typo.h5" HtmlTag="h2" tabindex="0">@Recording.Title</MudText>   @* two lines max *@
    </MudTooltip>
  </div>
  <div class="sk-details-meta" data-details-meta>
    <a class="sk-channel-link" href="https://www.twitch.tv/@login" target="_blank" rel="noopener noreferrer">
      <ChannelAvatar Size="Size.Medium" Decorative="true" />
      <span class="name">@displayName</span>
      <span class="where">on Twitch</span>
      <MudIcon Icon="@Icons.Material.Rounded.OpenInNew" />                    @* leaves the app *@
    </a>
    <dl class="sk-details-facts">
      <div class="sk-fact when">
        <dt><MudIcon Icon="@Icons.Material.Rounded.Event" />Recorded</dt>
        <dd>Sun, Nov 8, 2026<span class="sub">6:00 PM – 6:01 PM</span></dd>
      </div>
      <div class="sk-fact"><dt><MudIcon Icon="@Icons.Material.Rounded.Timer" />Length</dt><dd>1m</dd></div>
      <div class="sk-fact"><dt><MudIcon Icon="@Icons.Material.Rounded.SportsEsports" />Category</dt><dd>Apex Legends</dd></div>
      <div class="sk-fact"><dt><MudIcon Icon="@Icons.Material.Rounded.Toc" />Chapters</dt><dd>1 chapter</dd></div>
      <div class="sk-fact"><dt><MudIcon Icon="@Icons.Material.Rounded.Storage" />File</dt><dd>3.7 MB<span class="sub">2 parts</span></dd></div>
    </dl>
  </div>
  <div class="sk-actions" data-details-actions>                                @* top right *@
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

- **Title row, then the slate.** A two-row grid: the title in `h5` (the page's `h2`) at the left, the live chip (when live) and the `MoreVert` "More actions" button at the right, their tops lined up; the channel byline and the fact slate span both columns underneath. The title wraps to two lines before it ellipsises. The actions never wrap and never squeeze the title.
- **What is in the menu.** All four actions live in More actions, in this order: Copy playlist URL (`PlaylistPlay`), Export MP4 (`Output`), a divider, Stop recording (`Stop`, `error`, live only), a divider, Delete recording (`Delete`). Copy is public. Stop and Delete open the `Dialog` as before. After an export finishes, Download stays in the export row.
- The More actions button has `aria-haspopup="menu"` and `aria-expanded`; the menu opens below it, right-aligned, on `surface` with `elevation-8`. The status chip is a status, not an action; it stays outside the `role="group"` named "Recording actions".
- Export MP4 is the first privileged item in the menu. While an export runs, it is disabled with "Exporting…" under the label, and the export row below shows the progress.
- Actions that need Twitch stay visible. While signed out, Export, Stop and Delete are disabled menu items with "Connect Twitch to use this" on hover and in their accessible description; Copy stays enabled. A blocked export or Delete also shows its reason under the label in `caption` ("Available when the recording finishes", "Cancel the export before deleting").
- **Channel byline.** A 40px `ChannelAvatar` (the channel's Twitch profile image, the same image as in the drawer; its initial on its channel colour when there is no image), the streamer's display name, the words "on Twitch" and an `OpenInNew` glyph are one link to `https://www.twitch.tv/{login}`. The link opens in a new tab (`target="_blank"`, `rel="noopener noreferrer"`) and its accessible name adds "(opens in a new tab)". The name falls back to the login when the library does not know the display name.
- **The fact slate.** A `dl` under the byline, separated by a top hairline, with `caption` labels and `body2` values; numbers use tabular figures. Facts: `Event` Recorded (local date and start–end time, "now" while live; an end on another day carries its date), `Timer` Length (or "Elapsed" while live), `SportsEsports` Category (or "No category"), `Toc` Chapters ("No chapters", "1 chapter", "N chapters"; counted from the same filtered list as the panel), and `Storage` File (size; its second line shows the segment and part counts; omitted while live).
- **Long titles.** At the base size, a title that needs two lines takes the first type step (`--text-title-step-1`: desktop `1.25rem`, `--text-title-step-1-compact`: compact `1.125rem`); three or more lines take the second (`--text-title-step-2`: desktop `1.125rem`, `--text-title-step-2-compact`: compact `1rem`). The line-height tightens to `1.35` (compact `1.4`). The two-line clamp and the full-title tooltip stay. `RecordingDetails.razor.js` measures the natural line count against the title column with a hidden clone and re-runs on column resize, recording change and font load; without JavaScript the title stays at the base size.
- The full title is in a tooltip that opens on hover over the text and on keyboard focus (the title is a tab stop).
- **The export row** shows under the block, full width, only while an export runs or has finished: on `surface-raised`, an `Output` icon in `secondary`, state text, `MudProgressLinear` in `secondary` with the percent in `timecode` while running (the bar is not a live region; the state text is the one announcement), then Cancel, Retry or a tonal Download link (a real `<a download>`). The tonal link is a 14% `primary` tint behind a `text-primary` label, because `primary` text is under 4.5:1 on `surface-raised`.
- **Below `Breakpoint.Md`** the block keeps its phone form (see `PhoneLayout`): the title in `h6`, the same live chip and More actions row beside it, then the channel byline and a fact slate that pairs into two columns on a phone and returns to one row when the compact width allows. The compact slate keeps Recorded, Length, Category and Chapters; the File fact stays desktop-only. The export row follows it.
