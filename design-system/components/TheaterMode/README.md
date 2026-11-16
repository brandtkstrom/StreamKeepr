Theater mode (desktop only): the player spans the full width of the view, edge to edge, and everything else moves below it.

## Build it from

- `AppShell` with `Theater = true`. The `MudDrawer` closes and its menu button is disabled ("Library is hidden in theater mode"). `MudMainContent` drops its gutters and takes `background-gray`.
- The player is the full width of the viewport, with square corners. Its height is `min(100vw × 9/16, 100dvh − appbar-height)`, so it never needs scrolling. When the height cap applies, the video letterboxes inside the full-width black frame.
- Below the player, in a `pa-4` grid: `RecordingDetails` (title, actions, meta) on the left, and the open `ChatPane` on the right (400px wide, about 260px tall). Stats for nerds follows, full width. Without chat, details take the full width.
- The player's theater toggle shows pressed (`player-accent`). `T` toggles it (the existing shortcut), and so does the button.

## Rules

- Chat never sits beside the player in theater mode. The player always gets the whole width.
- Theater mode never hides the app bar. Fullscreen does that.
- Leaving theater mode restores the drawer state and the chat sidecar. Disk usage hides with the drawer.
- Below `Breakpoint.Md` the toggle hides: the player is already edge to edge there, and fullscreen takes over.
