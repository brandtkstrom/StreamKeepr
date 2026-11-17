Chat replay beside the player: messages synced to the playhead, with a sync offset slider and a jump back to the current time. A JS-owned island.

## Build it from

Keep `ChatPane.razor.js` owning the log, polling, auto-scroll and the toggle. Restyle to match an outlined `MudPaper`:

- Open on desktop: a sidecar to the right of the player, `flex: 0 1 400px` with a `chat-width` (320px) minimum, so it widens into space the height-capped player leaves. As tall as the player, on `surface`, `radius-lg`, `lines-default` outline. The header has a chat icon button, "Chat" in `subtitle1` and the state in `caption` ("Replay · synced to 54:40", "Live", or "Chat unavailable" in `error`). *As built in #180 the state keeps its existing words ("Chat replay", "Chat connected", "Waiting for chat…", "No chat captured", "Chat unavailable").*
- Log: `body2`, with the offset in `timecode` (11px, `text-secondary`), the name in 700 weight, and replies quoted in `caption` behind a `divider` rule. *As built in #180 rows show no offset yet: adding it would change the log's content, which the issue does not cover.*
- **Chat sync offset** (the footer): a slider instead of nudge buttons. *Built in F3 (#184).*
  - The top line: a `Tune` icon, "Chat sync" in `label`, the current offset right-aligned in `timecode` ("+1.5 s", "−3.0 s"; "0.0 s" in `text-secondary`), and a small `RestartAlt` icon button, "Reset chat sync to 0".
  - Below, a full-width slider from −30 s to +30 s (`ChatReader.MaxSyncOffsetSeconds`) in 0.5 s steps, labelled "Earlier" at the left and "Later" at the right in `caption`. It is centred on 0 with a tick mark there. The track is `primary` at 24%, and the active fill runs from the centre to the thumb in `primary`, so the direction and size of the shift read at a glance. The thumb is 16px, in `primary`.
  - It is a native `<input type="range">` styled to match `MudSlider Color.Primary`, because chat is a JS-owned island. Keep the `data-sk="chat-sync"` hook, and replace the five nudge buttons with `data-sk="chat-sync-range"` and `data-sk="chat-sync-reset"`.
  - Keyboard (native to the range input): ←/→ move 0.5 s, PageUp/PageDown 5 s, Home/End ±30 s. `aria-valuetext` reads "1.5 seconds later" or "3 seconds earlier". *As built in #184 the module handles these keys itself (↑/↓ step 0.5 s too), because browsers disagree on the Page keys (Chrome moves a tenth of the range) and each press must apply at once.*
  - Dragging updates the readout live. The offset is applied, and chat refetched, when the drag ends (`change`), and per key press from the keyboard, so the chat fetch isn't repeated on every pixel. It is saved per recording, as today. "Jump to current time" is a small outlined pill above the footer, shown only after the viewer scrolls away. *As built in #184 the pill still sits inside the footer, on the line below the slider; moving it above the footer is not in #184's scope.*
- Collapsed: a 44px strip with only the chat icon. `C` toggles it (`aria-keyshortcuts`).

## Rules

- The streamer's own messages take their channel colour (`channel-*`). Other chatters' names use their Twitch colour only where it reaches 4.5:1 on `surface`; otherwise `text-primary`.
- In theater mode it moves below the player, beside Recording details (400px wide).
- Below `Breakpoint.Md` it is the **Chat** tab under the player (`MudTabPanel Icon="@Icons.Material.Rounded.Chat"`), filling the rest of the screen without its own header; the tab owns the disclosure, so the pane is always open.
- The channel colour comes from .NET (`ChannelColor`) in the pane's payload (`channelLogin`, `channelColor`), so chat and the drawer always agree (player contract §10a).
