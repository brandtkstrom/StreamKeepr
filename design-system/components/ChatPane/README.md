Chat replay beside the player: messages synced to the playhead, shifted by the viewer's Chat sync offset from Settings, with a jump back to the current time. A JS-owned island.

## Build it from

Keep `ChatPane.razor.js` owning the log, polling, auto-scroll and the open state. Restyle to match an outlined `MudPaper`:

- Open on desktop: the right-hand column of the watch view, `flex: 0 1 400px` with a `chat-width` (320px) minimum, so it widens on wide screens. It takes all the vertical space of the view, from the top of the main column to the bottom of the viewport, sticky while the left column (player, title block, Chapters, Stats) scrolls; everything else stays to its left (see `AppShell`). The log fills the height between the header and footer, on `drawer-background`, `radius-lg`, `lines-default` outline. The header has the `Chat` icon (decorative, `action-default`), "Chat" in `subtitle1` and the state in `caption` ("Replay · synced to 54:40", "Live", or "Chat unavailable" in `error`). *As built in #180 the state keeps its existing words ("Chat replay", "Chat connected", "Waiting for chat…", "No chat captured", "Chat unavailable").*
- Log: `body2`, with the offset in `timecode` (11px, `text-secondary`), the name in 700 weight, and replies quoted in `caption` behind a `divider` rule. *As built in #180 rows show no offset yet: adding it would change the log's content, which the issue does not cover.*
- **Footer**: the "Jump to current time" pill (a small outlined pill, shown only after the viewer scrolls away) and a notice line. Chat sync is no longer here: it is one offset for every recording, set in Settings › Chat (`ChatSettings`), and the pane applies it to every fetch.
- **No toggle of its own.** The player's Chat button (see `VideoPlayer`) and `C` open and close the pane; the header is a plain title. Closed, the pane is not shown at all: no strip is left behind, and the player takes the sidecar's width.

## Rules

- The streamer's own messages take their channel colour (`channel-*`). Other chatters' names use their Twitch colour only where it reaches 4.5:1 on `surface`; otherwise `text-primary`.
- In theater mode it moves below the player and stays the right-hand column there, taking the height left down to the bottom of the viewport, at least 260px (see `TheaterMode`).
- Below `Breakpoint.Md` it is the **Chat** tab under the player (`MudTabPanel Icon="@Icons.Material.Rounded.Chat"`), filling the rest of the screen without its own header; the tab owns the disclosure, so the pane is always open.
- The channel colour comes from .NET (`ChannelColor`) in the pane's payload (`channelLogin`, `channelColor`), so chat and the drawer always agree (player contract §10a).
