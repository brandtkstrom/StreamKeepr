Settings › Chat: viewer preferences for the chat replay, starting with one Chat sync offset that applies to every recording.

## Build it from

- A `SettingsSection` titled "Chat" after the `Chat` icon, between **Channels** and **Connection status**. Unlike those, it is the viewer's own preference, not channel data, so it shows signed out too, under the "Connect Twitch to manage channels" panel.
- **Chat sync**: the slider the chat pane's footer used to hold, now one app-wide offset.
  - The top line: a `Tune` icon, "Chat sync" in `label` (the slider's `<label>`), the current offset right-aligned in `timecode` ("+1.5 s", "−3.0 s"; "0.0 s" in `text-secondary`), and a small `RestartAlt` icon button, "Reset chat sync to 0".
  - Below, a slider from −30 s to +30 s (`ChatReader.MaxSyncOffsetSeconds`) in 0.5 s steps, "Earlier" at the left and "Later" at the right in `caption`, at most 420px wide. It is centred on 0 with a tick there. The track is `primary` at 24%, and the fill runs from the centre to the thumb in `primary`, so the direction and size of the shift read at a glance. `aria-valuetext` reads "1.5 seconds later" or "3 seconds earlier".
  - Under it, one line of help in `caption`, `text-secondary`: "Shifts chat on every recording, to match the delay between the stream and its chat."
  - A native `<input type="range">` styled to match `MudSlider Color.Primary`, because `MudSlider` fills from its minimum and cannot fill from the centre. Keyboard is the browser's: ←/→ 0.5 s, Home/End ±30 s.
- The offset is stored in the viewer's `UiPreferences` (`chatSync`, seconds). Dragging updates the readout live; the offset is saved and applied when the drag ends (`change`), and per key press.

## Rules

- One offset for every recording. There is no per-recording offset.
- The Settings sheet opens over a playing recording, so a change applies to the open chat pane at once: the pane refetches around the playhead with the new offset. Playback never stops.
- Positive shows chat later, negative earlier, the same sign the chat endpoint's `syncOffsetSec` takes.

## Later (planned, not built)

Further rows in this section, each app-wide like Chat sync: font size, link highlighting, message highlights (on/off), and channel badges (on/off).
