# Admin-only Twitch sign-in with server-side token storage

## Status

Accepted

## Context

Stream Keepr needs an admin Twitch user token for EventSub and for subscriber-only streams. The UI is otherwise open on the local network. Tokens must survive restarts and must not be exposed to the browser.

## Decision

Auth is admin-only: logins are allowlisted in `Auth:AllowedTwitchLogins`; viewing stays open; export, delete, and settings require sign-in. The OAuth code flow stores access and refresh tokens server-side, encrypted with Data Protection, behind `IAdminTokenStore` with `GetAsync`, `SaveAsync`, `ClearAsync`, and `WaitForTokenAsync`. The Data Protection key ring is persisted under `/recordings/.streamkeepr/keys` before any token is stored. EventSub runs only while a token is available; otherwise polling carries the app. Exact scopes, callback route, return-url validation, subscriber-token handling, and allowlist values are settled in the Twitch auth ticket.

## Consequences

- The browser never receives the Twitch tokens.
- `F10-T7` no longer depends on `F6-T1`; `F6-T1` depends on the token store.
- Tokens survive restarts; losing the key ring forces re-authentication.
- EventSub is optional; polling is the fallback.
