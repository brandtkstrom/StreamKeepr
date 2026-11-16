What the library and player column show when there is nothing to show: an empty library, a filter with no match, and a recording that does not exist.

## Build it from

- **No match** (drawer): `MudText Typo.subtitle1` "No recordings match", one `body2` line saying which filter is active ("Nothing is recording right now."), and a text button that clears it ("Show all recordings"). Search gets "No recordings match “speed”" and a "Clear search" button.
- **Library empty** (first run, main column): a 48px `VideoLibrary` icon in `action-default`, `Typo.h4` "No recordings yet", one `body1` sentence, and a filled "Add a channel" that goes to `/settings#channels`. When signed out, the button is the Twitch button instead, because adding channels needs Twitch.
- **Recording not found** (`/r/{id}` with an unknown id): `Typo.h4` "Recording not found", then "That recording is not in the library." with a "Back to library" link.

## Intentional additions

The "Show all recordings" and "Add a channel" affordances are new in this redesign. With recordings present and none open, `/` shows the `Home` rows, not an empty state. Today the app shows only the headline text.

## Rules

- One icon, one headline, one sentence, at most one action. No illustrations, no emoji. "Recording not found" leads with `Search`.
- Use `MudSkeleton` rows while the library loads, never an empty state.
