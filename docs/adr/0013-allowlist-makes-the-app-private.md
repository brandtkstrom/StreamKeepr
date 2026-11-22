# A non-empty allowlist makes the whole app private

## Status

Accepted (2026-11-21). Amends ADR-0003, whose "viewing stays open" now holds only while `Auth:AllowedTwitchLogins` is empty. Designed in the design system's `SignIn` card; not yet implemented.

## Context

ADR-0003 kept viewing open to anyone who can reach the app and put only management actions (export, delete, settings) behind Twitch sign-in. An operator who lists logins in `Auth:AllowedTwitchLogins` is saying who the app is for, but under ADR-0003 everyone else on the network can still browse the library, watch every recording, read chat, and write the shared watch progress. Recordings of subscriber-only streams make that exposure worse.

## Decision

The allowlist decides two modes:

- **Empty: open.** Unchanged from ADR-0003 and E-13: viewing and recording are open, any successfully authenticated Twitch user is an admin, and a startup warning says so.
- **Non-empty: private.** Every surface except the sign-in flow requires a signed-in Twitch account whose login is on the list (case-insensitive). Being signed in alone is not enough; a login not on the list never gets a session.
  - Pages, deep links included, redirect a visitor without an allowed session to `/signin?returnUrl=<local path>`; signing in returns them there.
  - Media, playlists, thumbnails, chat, emotes, viewer samples, progress writes and the rest of the API answer `401` instead of redirecting. The Blazor circuit refuses an unauthenticated connection.
  - Public in both modes: `/signin`, the Twitch login and callback routes, health endpoints, and the static assets the sign-in page needs.
  - An expired session (the 30-minute revalidation fails) or a login removed from the list ends access: the open page goes to `/signin` with the reason, and playback stops.
  - Inside the app every visitor is signed in, so management actions are never shown as blocked for being signed out.

## Considered options

- **Keep viewing open and only hide the library.** Rejected: media URLs are guessable from channel and recording ids, so hiding the UI protects nothing.
- **A separate `Auth:Private` flag.** Rejected: an operator who sets an allowlist but forgets the flag gets the leak this ADR closes, and no one has asked for "allowlisted admins, public viewers".
- **Gate at the reverse proxy.** Still possible, but the app already knows who is allowed, and gating there cannot reuse the Twitch sign-in.

## Consequences

- Watch progress, chat and every media request carry the session cookie; the player and the chat island get `401` handling that sends the page to `/signin`.
- Plain-HTTP LAN access stops working in private mode except on localhost, because Twitch sign-in needs HTTPS (E-15).
- Endpoint auth in `hosting-contract.md` §3, the allowlist rules in `twitch-auth-contract.md` §6, and the signed-out states in `details-auth-settings-contract.md` and `frontend-foundation-contract.md` apply per mode.
- Tests need a private-mode configuration alongside today's open one: redirects, `401`s, a not-allowed login, and expiry.
