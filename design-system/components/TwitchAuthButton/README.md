# TwitchAuthButton

The control that connects a Twitch account, enabling features that need it (following list, live notifications, auto-record of followed channels, subscriber-only VODs).

**States.**
1. Disconnected — `.sk-twitch`: `twitch` fill, `on-twitch` label "Connect with Twitch", `link` icon. Starts the OAuth flow in a popup.
2. Connecting — `aria-busy="true"`, spinner, "Waiting for Twitch…", `twitch-hover` fill.
3. Connected — `.sk-auth-chip`: avatar, Twitch login, a small `success` (Green) dot; opens a menu with "Manage Twitch connection" and "Disconnect".
4. Expired/failed — an `error-container` chip "Twitch session expired" with a text Reconnect button.

- `twitch` is used for this control only — it is Twitch's colour, not ours. It sits close to Dracula Purple (`primary`); the white label and the Twitch wording keep it distinct from our own filled buttons, which use Background-coloured labels.
- The `link` icon stands in for Twitch's Glitch mark. Use the official mark from Twitch's brand assets when shipping; don't redraw it.
- Features that need auth are visible but disabled with a tooltip "Connect Twitch to use this", never hidden.
