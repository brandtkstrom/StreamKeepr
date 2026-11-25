Settings › Chat: viewer preferences for the chat replay — one Chat sync offset, one Show badge text switch, one font size and one font weight, each applying to every recording.

## Build it from

- A `SettingsSection` titled "Chat" after the `Chat` icon, between **Channels** and **Connection status**. Unlike those, it is the viewer's own preference, not channel data, so it shows signed out too, under the "Connect Twitch to manage channels" panel.
- **Chat sync**: the slider the chat pane's footer used to hold, now one app-wide offset.
  - The top line: a `Tune` icon, "Chat sync" in `label` (the slider's `<label>`), the current offset right-aligned in `timecode` ("+1.5 s", "−3.0 s"; "0.0 s" in `text-secondary`), and a small `RestartAlt` icon button, "Reset chat sync to 0".
  - Below, a slider from −30 s to +30 s (`ChatReader.MaxSyncOffsetSeconds`) in 0.5 s steps, "Earlier" at the left and "Later" at the right in `caption`, at most 420px wide. It is centred on 0 with a tick there. The track is `primary` at 24%, and the fill runs from the centre to the thumb in `primary`, so the direction and size of the shift read at a glance. `aria-valuetext` reads "1.5 seconds later" or "3 seconds earlier".
  - Under it, one line of help in `caption`, `text-secondary`: "Shifts chat on every recording, to match the delay between the stream and its chat."
  - A native `<input type="range">` styled to match `MudSlider Color.Primary`, because `MudSlider` fills from its minimum and cannot fill from the centre. Keyboard is the browser's: ←/→ 0.5 s, Home/End ±30 s.
- The offset is stored in the viewer's `UiPreferences` (`chatSync`, seconds). Dragging updates the readout live; the offset is saved and applied when the drag ends (`change`), and per key press.
- **Show badge text**: a native checkbox drawn in `primary` (`accent-color`), led by the `Label` icon and labelled "Show badge text", with one line of help naming the chips it hides. It is the viewer's one switch for the text chips that stand in for badges (MOD, SUB, VIP and the rest); it is not a badge-image switch. Stored in `UiPreferences` (`chatShowBadgeText`, on by default). A change persists and applies to an open chat pane at once: nothing refetches, and the log keeps its scroll position.
- **Chat font size**: the log's font size as a percentage of the design's `body2`, from 85% to 145% in 5% steps, 100% by default, after Show badge text.
  - The top line: a `FormatSize` icon, "Chat font size" in `label` (the slider's `<label>`), and the current percentage right-aligned as a numeric readout ("100%").
  - Below, a native range with "Smaller" at the left and "Larger" at the right in `caption`, at most 420px wide, filled from the left in `primary`; `aria-valuetext` reads "100 percent". Keyboard is the browser's: ←/→ 5%, Home/End 85%/145%.
  - Under it, one line of help in `caption`, `text-secondary`: "Scales chat replay messages and names on every recording."
  - Stored in `UiPreferences` (`chatFontScale`, 85–145, default 100). It scales message text and names; badge chips scale up above 100% (10px floor) and never shrink; replies and gaps keep `caption`; emote images are `2em` (28px at 100%). A change persists and applies to an open pane at once without a refetch or a log rebuild.
- **Chat font weight**: a native select labelled "Chat font weight", led by the `FormatBold` icon, offering Normal (400), Medium (500) and Bold (700), 400 by default, with one line of help: "Sets message text weight; usernames stay bold."
  - Stored in `UiPreferences` (`chatFontWeight`, default 400). It sets message text only; usernames stay 700, and replies, gaps and badges keep their design weights. A change persists and applies to an open pane at once.

## Rules

- One offset for every recording. There is no per-recording offset.
- One font size and one font weight for every recording, like Chat sync. Size is a percentage of the design's `body2`, not px: 100% reproduces the design at any browser root size.
- The font size scales the log's conversation text, names, emotes, and badge chips (upward only, 10px floor). Replies, gaps and the pane's header/footer keep their design sizes.
- The size slider renders its `value` once; the browser owns the thumb afterwards, for the same slow-round-trip reason as Chat sync.
- The badge text switch hides only the text chips. Badge images are a separate, later surface; this switch does not gate them.
- The Settings sheet opens over a playing recording, so a change applies to the open chat pane at once: the pane refetches around the playhead with the new offset. Playback never stops.
- Positive shows chat later, negative earlier, the same sign the chat endpoint's `syncOffsetSec` takes.

## Later (planned, not built)

Further rows in this section, each app-wide like Chat sync: link highlighting and message highlights (on/off).
