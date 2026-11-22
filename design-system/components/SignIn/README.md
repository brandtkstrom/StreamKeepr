The sign-in page. When `Auth:AllowedTwitchLogins` lists one or more logins, StreamKeepr is private: nothing in it shows until an allowed Twitch account has signed in, and this page is all a signed-out visitor sees. With an empty list nothing changes: viewing stays open and the app bar's `TwitchAccount` button signs in for admin actions.

## Build it from

- A page at `/signin` with its own empty layout: no app bar, rail, drawer or bottom nav, and no recordings, channel names or disk figures anywhere on it. It must render statically (prerendered, no circuit), so it works before the visitor has any session.
- `main.ds-signin`: the page on `background`, the card centred both ways with a 16px gutter on phones.
- The card: `MudPaper Elevation="4"` on `surface`, `radius-lg`, at most 400px wide, centred text.
  1. The logo and wordmark at 24px (not a link: there is nowhere to go).
  2. `MudText Typo.h5` "Sign in to watch" (the page's `h1`).
  3. One line in `body2` `text-secondary`: "This StreamKeepr is private. Sign in with a Twitch account its owner has allowed." It is replaced by the alert in the states below.
  4. The Twitch button, full width and 44px tall: the same `twitch` fill as `TwitchAccount` ("Sign in with Twitch"), a real `<a href>` to the login endpoint with the return URL, `data-enhance-nav="false"`.
  5. A `caption` line: "Twitch asks you to approve, then sends you back here."

## States

| State | When | Alert (`MudAlert Dense`, text, above the button) | Button |
|---|---|---|---|
| Ready | First visit, nothing to report | none (the intro line shows) | Sign in with Twitch |
| Waiting | After the click, until the browser leaves | none | Spinner and "Waiting for Twitch…" on `twitch-hover`, `aria-busy` |
| Not allowed | `authError=notAllowed` | Error: "**pixelwren can't use this StreamKeepr.** Ask its owner to add your Twitch login, or use another account." | Use another Twitch account (forces Twitch's account prompt, `force_verify=true`, so the same account isn't reused silently) |
| Expired | The session's token failed revalidation | Warning: "**Your Twitch session expired.** Sign in again to carry on where you were." | Sign in with Twitch |
| Signed out | After Sign out in the account menu | Info: "You're signed out." | Sign in with Twitch |
| Failed | `authError=failed` | Error: "**Twitch sign-in failed.** Try again in a moment." | Try again |
| No HTTPS | HTTP and not localhost | Warning: "Twitch sign-in needs HTTPS. Open this site through its HTTPS address." | None: it could not succeed |

The alert takes `role="alert"` for errors and `role="status"` otherwise. On this page the reasons show in the card, not as the shell's snackbar.

## The gate

- **Everything is behind it.** Every page, deep link included, redirects a signed-out or not-allowed visitor to `/signin?returnUrl=/r/123` (a local path only), and a successful sign-in lands back there. Media, thumbnails, playlists, chat and the API answer `401` rather than redirecting, and the Blazor circuit is refused.
- **Allowed means both:** signed in with Twitch, and the login in the list (case-insensitive). A Twitch sign-in for any other login is rejected at the callback and never gets a session.
- **Inside the app** the account is always signed in, so `TwitchAccount` only ever shows its connected avatar. Its menu's Disconnect becomes **Sign out**, which leads here with "You're signed out". "Connect Twitch to use this" tooltips cannot occur.
- **When the session expires mid-watch,** playback and the page stop with it: the shell sends the visitor here with the Expired state and the current page as the return URL. The 30-minute revalidation is unchanged.
- **Removing a login from the list** ends that account's access at its next revalidation, the same as an expiry, but shows Not allowed.

## Rules

- `twitch` purple is still only for the Twitch button.
- Nothing about the library leaks onto this page or its `<title>` ("Sign in · StreamKeepr").
- Focus starts on the Twitch button; with an alert showing, the alert is read first.
