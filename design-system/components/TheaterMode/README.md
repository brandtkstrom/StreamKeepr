Theater mode (desktop only): the player spans the view edge to edge up to the open chat drawer's divider, and the watch content moves below it.

## Build it from

- `AppShell` with `Theater = true`. The `MudDrawer` closes, the labeled Library button is disabled, and the search shortcut is hidden. `MudMainContent` drops its gutters and takes `background-gray`.
- The player is the view's width up to the open chat drawer's divider (the whole viewport when chat is closed), with square corners. Its height is `min(player width × 9/16, 100dvh − appbar-height)`, so it never needs scrolling. When the height cap applies, the video letterboxes inside the full-width black frame.
- Below the player, the content keeps 16px side padding: on the left the title block (`RecordingDetails`, live chip and More actions menu at its top right), `Chapters` (when there are any) and `Viewers` (when there are samples), with Statistics as the overlay inside the player; on the right the open `ChatPane` as the same 400px right drawer, from the app bar's bottom to the viewport's bottom (see `AppShell`). Without chat, the player and the content take the whole view.
- The player's theater toggle shows pressed (`player-accent`). `T` toggles it (the existing shortcut), and so does the button.

## Rules

- In theater mode chat is still the docked right drawer, never a column inside the content beside the player: the player always gets the whole width up to the open drawer's divider.
- Theater mode never hides the app bar. Fullscreen does that.
- Leaving theater mode restores the drawer state and the chat drawer. Disk usage hides with the drawer.
- Below `Breakpoint.Md` the toggle hides: the player is already edge to edge there, and fullscreen takes over.
