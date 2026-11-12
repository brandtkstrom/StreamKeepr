# iPhone Safari manual checklist

Status: checklist ready; no pass recorded yet (#108)
Source: `docs/player-contract.md` §§3, 4, 9, 10, 14, 15; decision digest E-21 and §9.3 ("native Safari manual").

The E2E suite plays video only in branded Chrome. Native HLS, iOS fullscreen and picture-in-picture are covered by Vitest/bUnit stubs, and by this checklist on a real iPhone. Run it once before a release and again after any change to `VideoPlayer.razor.js`, `player-*.js` or the vendored hls.js. Record each pass in [Results](#results).

## Setup

1. Put the Mac and the iPhone on the same Wi-Fi network.
2. Start the Mock profile listening on the network, from the repository root:

   ```sh
   dotnet run --project src/StreamKeepr --launch-profile Mock --urls http://0.0.0.0:5187
   ```

3. On the iPhone, open `http://<mac-ip>:5187` in Safari. `ipconfig getifaddr en0` prints the Mac's address.
4. Note the time the app started. The Mock profile then changes the library on a timer (`Mock:Feed` in `src/StreamKeepr/appsettings.Mock.json`, run by `src/StreamKeepr/Mock/MockLiveFeed.cs`):
   - about 30 s: Echo Lane's "Chill art stream" reopens as **Recording**;
   - about 60 s: that session ends and is saved again;
   - about 90 s: Echo Lane's "Speedrun practice" is deleted.

   The feed checks its timer every 5 s, and the reopen also waits for the 10 s Twitch poll, so each step can land up to about 15 s late. Wait that long before marking L3, L4 or D1 as Fail. Restart the app to run the timed items again; the library is reseeded on each start.

A real deployment works too. Then the timed items need a channel that goes live and offline during the run.

## Checks

Mark each item **Pass**, **Fail**, **Skipped** or **Accepted** (a known limitation from player contract §14, such as Safari's own fullscreen controls). Note anything unexpected.

### Engine

- **E1.** Open a saved recording and turn on **Stats** (I key, or the stats button). Record the decoder line: `MSE · hls.js` or `Native HLS`. iOS 17.1 and later can run hls.js through Managed Media Source, so the native path may not run at all. Say which path the pass covered.
- **E2.** In native mode, values that only hls.js reports show `—`, and the header hint reads `Native HLS`. The player prefers hls.js whenever the browser has Managed Media Source, so a current iPhone reports `MSE · hls.js` here. In that case mark E2 **Skipped**. The native path then needs a device on iOS 17.0 or earlier.

### Saved recordings

- **V1.** The page shows the poster and the centre play button. Nothing plays until you tap play.
- **V2.** Tapping play plays inline in the page; it does not jump into fullscreen.
- **V3.** Drag the seek bar, and use back and forward 10 s. Playback continues from the new point. While it buffers you see the spinner, never the error overlay.
- **V4.** Play to about the middle, open another recording, then come back. The playhead returns to where you left off.
- **V5.** Turn on mute and set the speed to 1.5×, then reload. Mute and speed are kept. (iOS controls volume with the hardware buttons, so the volume slider may do nothing; mark that Accepted.)
- **V6.** A partial recording plays across its part gap without an error.

### Live recordings

- **L1.** Open a recording marked **Rec**. It opens near the live edge, ignores any saved position, and shows the red live tick and the **Live edge** button.
- **L2.** Pause, wait about 15 s, then play. The player does not jump forward on its own. **Live edge** seeks back to near live and keeps following.
- **L3.** Before the 30 s mark, open Echo Lane's "Chill art stream". It becomes live without a manual reload.
- **L4.** Keep it playing past about 60 s, when it ends. The live tick and **Live edge** disappear, the duration becomes finite, the position is kept, and the status chip updates. There is no reload or error.

### Deleted recording

- **D1.** Before the 90 s mark, open Echo Lane's "Speedrun practice". A "Recording deleted" message shows, and the page moves to the next recording, or to the home page when none is left.

### Fullscreen, picture-in-picture and layout

- **F1.** Tap fullscreen. iPhone Safari has no element fullscreen, so the player hands the video to Safari's own fullscreen with native controls; mark that **Accepted** (player contract §14). Leaving fullscreen returns to the page, and playback carries on.
- **F2.** The picture-in-picture button shows only when Safari supports it. Enter picture-in-picture, switch to another app, then come back. Video keeps playing, and the button's icon matches the current state.
- **F3.** In portrait (player ≤ 520 px wide), skip, picture-in-picture, speed, theater, volume, the quality pill and the live-edge label are hidden. Play, the seek bar and fullscreen remain, and the page does not scroll sideways.
- **F4.** In landscape, the hidden controls come back as the width allows, and nothing overlaps.

### Interruptions

- **I1.** Lock the phone while a video plays, wait 30 s, then unlock. The page either keeps playing or shows a clean paused state. No error overlay, and no leftover "Live updates paused" note once it reconnects.
- **I2.** Turn on Airplane mode for 10 s while a video plays, then turn it off. Playback recovers through the spinner or Retry. Retry, when shown, resumes from the last position.

## Results

Add one row per pass, newest first. A release with any **Fail** needs a linked issue and a dated follow-up (decision digest §9).

| Date | Device | iOS | Engine (E1) | App commit | Result | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| — | — | — | — | — | Not yet run | |
