# Visual baselines

`VisualJourneyTests` captures the main screens and compares each one with an approved PNG in this folder (testing contract §8, #106):

| Screen | Desktop 1280×820 | Phone 375×812 |
| --- | --- | --- |
| Home with the library | `home-desktop` | `home-phone` |
| A recording | `recording-desktop` | `recording-phone` |
| Its details card | `details-desktop` | `details-phone` |
| Settings | `settings-desktop` | `settings-phone` |
| Phone drawer open | | `drawer-phone` |

Chrome draws text slightly differently on each OS, so baselines live in one folder per OS: `linux/`, `macos/` or `windows/`. **Only `linux/` is committed**, and its images come from CI. A local run on another OS compares only against baselines you made yourself in that OS's folder; don't commit them.

## How a run reports

Each run writes these files to the `visual` folder of the E2E artifacts. In CI that is the `playwright-results` artifact.

- `actual/<screen>.png`: what this run captured.
- `diff/<screen>.png`: differing pixels in red over a faded copy of the baseline.
- `report.txt`: one line per screen, also written to the test output.
  - `ok` gives the share of pixels that differ.
  - `FAIL` means more than 0.1 % of pixels differ, the image size changed, or the screen has no baseline while other baselines for this OS do (one was deleted or renamed).
  - `blocked` means this OS has no approved baselines at all yet.

A pixel counts as different when any colour channel moves by more than 8 of 255. That ignores anti-aliasing noise but still catches small colour changes. Blocked screens don't fail the run (testing contract §10); a `FAIL` does.

## Approving baselines

1. Open the pull request and wait for the **E2E (Mock profile)** job.
2. Download `playwright-results`, then open `visual/actual/` and `visual/report.txt`.
3. Check each screenshot against the design-system previews in `design-system/components/*/preview.html`.
4. Copy the approved PNGs into `tests/StreamKeepr.E2E/Visual/linux/`, keeping their names. Commit them with a message that says who approved them against which previews.
5. When a UI change is intended, `diff/` shows what moved. Replace those baselines the same way, and have the pull request's reviewer approve the new images.

## Masked regions

Some regions change from run to run for reasons that aren't UI changes, so they are painted solid magenta (`#FF00FF`) before comparison. `Masks()` in `VisualJourneyTests.cs` is the list itself; in short:

- video frames, and the seek bar's buffered fill;
- the drawer's day headers ("Today" and "Yesterday" follow the wall clock);
- for sessions still recording: the thumbnail, the meta line and the status chip, since they grow and startup recovery decides between Recording and Reconnecting (titles stay visible);
- the subtitle of a streamer group with a live session;
- channel state chips in Settings.

Before each screenshot the test waits until no element moves for 10 frames, and centres the selected recording in the drawer, so each run captures the same layout. The app runs with the Mock live feed off. The browser runs in UTC, en-US, light theme, with reduced motion.
