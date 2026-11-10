# TopAppBar

The 64px bar across the top: menu (drawer) toggle, the StreamKeepr wordmark, recording search, disk space, schedule, settings and the Twitch auth control.

- Ground is `surface`; once the main column scrolls, add `.scrolled` (`surface-container` + `elevation-1`).
- The wordmark is type only — `title-large`, "Stream" in `on-surface`, "Keepr" in `primary`, preceded by the REC dot in `rec`. Never split or recolour it otherwise.
- Search filters the library live; it matches titles, streamer names and categories.
- The Twitch control is always the last item on the right (see TwitchAuthButton).
- Do: keep disk space visible — a DVR that silently fills its disk fails. Turn the meter `warning` (Orange) under 10% free.
