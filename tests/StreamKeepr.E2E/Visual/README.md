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
- `report.txt`: one line per screen. `ok` shows the share of pixels that differ. `FAIL` means more than 0.1 % differ, or the image size changed. `blocked` means there is no approved baseline for this OS yet.

A blocked screen doesn't fail the run, as testing contract §8 asks. A `FAIL` does.

## Approving baselines

1. Open the pull request and wait for the **E2E (Mock profile)** job.
2. Download `playwright-results`, then open `visual/actual/` and `visual/report.txt`.
3. Check each screenshot against the design-system previews in `design-system/components/*/preview.html`.
4. Copy the approved PNGs into `tests/StreamKeepr.E2E/Visual/linux/`, keeping their names. Commit them with a message that says who approved them against which previews.
5. When a UI change is intended, `diff/` shows what moved. Replace those baselines the same way, and have the pull request's reviewer approve the new images.

## Masked regions

These regions change from run to run for reasons that aren't UI changes. They are painted solid magenta (`#FF00FF`) before comparison:

- video frames;
- the drawer's day headers ("Today" and "Yesterday" follow the wall clock);
- rows that are still capturing, since their duration keeps growing;
- streamer group subtitles, since startup recovery decides between "Recording now" and "Reconnecting";
- the storage meter;
- the details card's date.

The app runs with the Mock live feed off. The browser runs in UTC, en-US, light theme, with reduced motion.
