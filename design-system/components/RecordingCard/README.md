One recording as a card in the Home rows: a 16:9 thumbnail with its state and duration, the streamer's avatar, a two-line title and a meta line.

## Build it from

A real link, `<a class="sk-card" href="/r/{id}">`, so it works without the circuit and opens in a new tab. Inside it:

- **Thumbnail:** `.sk-thumb` at full card width, `radius-md`, showing the latest frame grab (`ThumbnailUrl`, lazy). Without one, `surface-raised` with `Icons.Material.Rounded.Movie`. Overlays:
  - top left, the state chip: REC (`rec` fill) for `Recording`, or Reconnecting (`warning`, `Sync` icon) for `Reconnecting`;
  - bottom right, a `timecode` duration badge on `player-scrim`, showing elapsed time while live;
  - along the bottom, a 3px strip: full `rec` while active, or `player-accent` up to the resume position.
- **Body:** a `MudAvatar` (32px) on the channel colour (`channel-*`), or their image. Beside it, the title and the meta line.
  - Title: `subtitle2`, or `subtitle1` at Medium on large cards; two lines, then ellipsis.
  - Meta: `caption` in `text-secondary`. Active: "nightjar_tv · Speedrunning · Started 21:04". Complete: "pixelwren · Yesterday, 20:30".
  - A Partial `StatusChip` under the meta when the state is `Partial`. Saved recordings carry no chip.

## Sizes

| Size | Width | Used for |
|---|---|---|
| Large | 360px | Recording now |
| Default | 264px | Recent recordings |

## Rules

- Hover lays `text-primary` at `hover-opacity` over the card, with `radius-lg` and `space-2` of padding around the thumbnail. Focus shows the `primary` ring around that same shape.
- One chip at most on the thumbnail, and one under the meta. The thumbnail carries no other text.
- The accessible name is the title, then the streamer, then the state ("Late-night speedrun practice, nightjar_tv, recording").
