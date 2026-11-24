Theater mode (desktop only): the player spans the full width of the view, edge to edge, and the two watch columns move below it.

## Build it from

- `AppShell` with `Theater = true`. The `MudDrawer` closes and its menu button is disabled ("Library is hidden in theater mode"). `MudMainContent` drops its gutters and takes `background-gray`.
- The player is the full width of the viewport, with square corners. Its height is `min(100vw × 9/16, 100dvh − appbar-height)`, so it never needs scrolling. When the height cap applies, the video letterboxes inside the full-width black frame.
- Below the player, with `pa-4`, the same two columns as the normal watch view (`AppShell`): on the left the title block (`RecordingDetails`, live chip and More actions menu at its top right), `Chapters` (when there are any) and `Viewers` (when there are samples), with Statistics as the overlay inside the player; on the right the open `ChatPane` (`flex: 0 1 400px`, 320px minimum). Chat takes all the vertical space left under the player, down to the bottom of the viewport, and at least 260px; it is sticky, so it stays in view while the left column scrolls. Without chat, the left column takes the full width.
- The player's theater toggle shows pressed (`player-accent`). `T` toggles it (the existing shortcut), and so does the button.

## Rules

- Chat never sits beside the player in theater mode. The player always gets the whole width; chat sits to the right of everything below it.
- Theater mode never hides the app bar. Fullscreen does that.
- Leaving theater mode restores the drawer state and the chat sidecar. Disk usage hides with the drawer.
- Below `Breakpoint.Md` the toggle hides: the player is already edge to edge there, and fullscreen takes over.
