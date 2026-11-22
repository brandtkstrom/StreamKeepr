import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  createStatsProbe,
  registerStatsSource,
} from "../../src/StreamKeepr/wwwroot/js/stats.js";
import { createStatsPanel } from "../../src/StreamKeepr/Components/Features/Player/StatsPanel.razor.js";
import { publishViewers, resetViewerStore } from "../../src/StreamKeepr/wwwroot/js/viewer-store.js";

const MARKUP = `
  <div data-sk-player>
    <button data-sk="stats" aria-label="Statistics" aria-pressed="false"></button>
    <section data-sk-stats data-chrome="overlay" data-open="false" hidden>
      <button data-sk="copy-stats" aria-label="Copy stats"></button>
      <button data-sk="stats-close" aria-label="Close statistics"></button>
      <div data-sk="stats-body">
        <div data-sk="value-bitrate"></div>
        <svg data-sk="spark-bitrate" data-spark-unit="Mb/s" data-spark-color="var(--primary)"></svg>
        <span data-sk="tip-bitrate"></span><table data-sk="table-bitrate"><tbody data-sk="rows-bitrate"></tbody></table>
        <div data-sk="value-frame"></div>
        <svg data-sk="spark-frame" data-spark-unit="fps" data-spark-color="var(--primary)"></svg>
        <span data-sk="tip-frame"></span><table data-sk="table-frame"><tbody data-sk="rows-frame"></tbody></table>
        <div data-sk="value-bandwidth"></div>
        <svg data-sk="spark-bandwidth" data-spark-unit="Mb/s" data-spark-color="var(--tertiary)"></svg>
        <span data-sk="tip-bandwidth"></span><table data-sk="table-bandwidth"><tbody data-sk="rows-bandwidth"></tbody></table>
        <div data-sk="value-buffer"></div>
        <svg data-sk="spark-buffer" data-spark-unit="s" data-spark-color="var(--tertiary)"></svg>
        <span data-sk="tip-buffer"></span><table data-sk="table-buffer"><tbody data-sk="rows-buffer"></tbody></table>
        <span data-sk="kv-resolution"></span><span data-sk="kv-codecs"></span>
        <span data-sk="kv-dropped"></span><span data-sk="kv-decoder"></span>
        <span data-sk="kv-last-segment"></span><span data-sk="kv-segments"></span>
        <span data-sk="kv-errors"></span><span data-sk="kv-playlist"></span>
        <div data-sk="stats-stream" hidden>
          <dt data-sk="stream-at">Viewers at 0:00</dt><dd data-sk="stream-viewers">—</dd>
        </div>
      </div>
    </section>
  </div>`;

function fakeVideo() {
  return {
    videoWidth: 1280,
    videoHeight: 720,
    currentTime: 10,
    buffered: { length: 1, start: () => 0, end: () => 38.4 },
    getVideoPlaybackQuality: () => ({ droppedVideoFrames: 3, totalVideoFrames: 41220 }),
  };
}

function fakeDotNet() {
  const calls = [];
  return {
    calls,
    invokeMethodAsync: vi.fn(async (identifier, ...args) => {
      calls.push([identifier, ...args]);
    }),
  };
}

async function flush() {
  for (let index = 0; index < 6; index++) await Promise.resolve();
}

function hook(name) {
  return document.querySelector(`[data-sk="${name}"]`);
}

function rows(name) {
  return document.querySelectorAll(`[data-sk="rows-${name}"] tr`);
}

describe("StatsPanel.razor.js", () => {
  let now;
  let dotNet;
  let probe;
  let panel;
  let unregister;

  beforeEach(() => {
    vi.useFakeTimers();
    document.body.innerHTML = MARKUP;
    resetViewerStore();
    now = 0;
    dotNet = fakeDotNet();
    probe = createStatsProbe({ video: fakeVideo(), now: () => now });
    probe.setView({
      recordingId: "r1",
      playlistUrl: "/media/ann/r1/index.m3u8",
      state: "recording",
    });
    probe.setNative(true);
    unregister = registerStatsSource(
      document.querySelector("[data-sk-player]"),
      probe,
    );

    document.querySelector('[data-sk="spark-buffer"]').getBoundingClientRect = () => ({
      left: 0,
      width: 200,
      top: 0,
      height: 36,
      right: 200,
      bottom: 36,
    });
  });

  afterEach(() => {
    panel?.dispose();
    panel = null;
    unregister?.();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.replaceChildren();
  });

  function start(open = true, { state = "recording" } = {}) {
    panel = createStatsPanel(document.querySelector("[data-sk-stats]"), {
      dotNet,
      clock: () => now,
      interval: 2000,
    });
    panel.init({
      recordingId: "r1",
      playlistUrl: "/media/ann/r1/index.m3u8",
      state,
      open,
    });
    return panel;
  }

  function tick() {
    now += 2000;
    vi.advanceTimersByTime(2000);
  }

  it("samples every 2 s, caps the ring at 20, and resets when reopened", () => {
    start();

    expect(rows("buffer")).toHaveLength(1);

    now += 1999;
    vi.advanceTimersByTime(1999);
    expect(rows("buffer")).toHaveLength(1);

    now += 1;
    vi.advanceTimersByTime(1);
    expect(rows("buffer")).toHaveLength(2);

    for (let index = 0; index < 24; index++) tick();
    expect(rows("buffer")).toHaveLength(20);
    expect(rows("buffer")[0].textContent).toContain("12s");

    panel.setOpen(false);
    panel.setOpen(true);
    expect(rows("buffer")).toHaveLength(1);
    expect(rows("buffer")[0].textContent).toContain("0s");
  });

  it("starts frame-rate sampling on open, stops it on close, and resets it on reopen", () => {
    const begin = vi.spyOn(probe, "beginStatsWindow");
    const end = vi.spyOn(probe, "endStatsWindow");

    start();
    expect(begin).toHaveBeenCalledTimes(1);
    expect(end).not.toHaveBeenCalled();

    panel.setOpen(false);
    expect(end).toHaveBeenCalledTimes(1);

    panel.setOpen(true);
    expect(begin).toHaveBeenCalledTimes(2);
    expect(end).toHaveBeenCalledTimes(1);

    panel.dispose();
    panel = null;
    expect(end).toHaveBeenCalledTimes(2);
  });

  it("shows the card while open and hides it when closed", () => {
    const section = document.querySelector("[data-sk-stats]");
    start(false);
    expect(section.hidden).toBe(true);

    panel.setOpen(true);
    expect(section.hidden).toBe(false);
    expect(section.dataset.open).toBe("true");
  });

  it("closes from its own button, persists it, and hands focus to the player's stats button", () => {
    start();
    const close = document.querySelector('[data-sk="stats-close"]');
    const states = [];
    document.addEventListener("sk:stats-state", (event) => states.push(event.detail.open));
    close.focus();

    close.click();

    expect(document.querySelector("[data-sk-stats]").hidden).toBe(true);
    expect(dotNet.calls).toContainEqual(["SetStatsOpen", false]);
    expect(states).toEqual([false]);
    expect(document.activeElement).toBe(document.querySelector('[data-sk="stats"]'));
  });

  it("keeps the phone Stats tab open whatever the player asks", () => {
    const section = document.querySelector("[data-sk-stats]");
    section.setAttribute("data-chrome", "tab");
    start(false);
    expect(section.hidden).toBe(false);

    document.dispatchEvent(new CustomEvent("sk:stats-open", { detail: { open: false }, bubbles: true }));
    expect(section.hidden).toBe(false);
  });

  it("does not claim the player-owned stats button", () => {
    start(false);
    document.querySelector('[data-sk="stats"]').click();

    expect(dotNet.calls).toEqual([]);
    expect(document.querySelector('[data-sk="stats"]').getAttribute("aria-pressed")).toBe("false");
  });

  it("applies the player's immediate stats state without persisting again", () => {
    start(false);
    const begin = vi.spyOn(probe, "beginStatsWindow");
    const states = [];
    document.addEventListener("sk:stats-state", (event) => states.push(event.detail.open));

    document.dispatchEvent(new CustomEvent("sk:stats-open", {
      detail: { open: true },
      bubbles: true,
    }));

    expect(document.querySelector("[data-sk-stats]").dataset.open).toBe("true");
    expect(document.querySelector("[data-sk-stats]").hidden).toBe(false);
    expect(begin).toHaveBeenCalledOnce();
    expect(dotNet.calls).toEqual([]);
    expect(states).toEqual([true]);
  });

  it("maps clipboard success and failure to the copy snackbar", async () => {
    const writeText = vi.fn(async () => {});
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText },
    });
    start();
    tick();

    document.querySelector('[data-sk="copy-stats"]').click();
    await Promise.resolve();
    await Promise.resolve();

    expect(writeText).toHaveBeenCalledOnce();
    const snapshot = JSON.parse(writeText.mock.calls[0][0]);
    expect(snapshot.v).toBe(1);
    expect(snapshot.recordingId).toBe("r1");
    expect(snapshot.mode).toBe("native");
    expect(snapshot.samples).toHaveLength(2);
    expect(dotNet.calls).toContainEqual(["Notify", "Stats copied"]);

    writeText.mockRejectedValueOnce(new Error("denied"));
    document.querySelector('[data-sk="copy-stats"]').click();
    await Promise.resolve();
    await Promise.resolve();

    expect(dotNet.calls).toContainEqual(["Notify", "Could not copy stats"]);
  });

  it("keeps an active sparkline tooltip through a sample redraw", () => {
    start();
    tick();

    const spark = document.querySelector('[data-sk="spark-buffer"]');
    const tip = document.querySelector('[data-sk="tip-buffer"]');
    spark.dispatchEvent(new MouseEvent("pointermove", { clientX: 100, bubbles: true }));

    expect(tip.textContent).toContain("s ago ·");
    expect(spark.querySelector("line.xh").getAttribute("visibility")).toBe("visible");

    tick();

    expect(tip.textContent).toContain("s ago ·");
    expect(spark.querySelector("line.xh").getAttribute("visibility")).toBe("visible");

    spark.dispatchEvent(new MouseEvent("pointerleave"));
    expect(tip.textContent).toBe("");
    expect(spark.querySelector("line.xh").getAttribute("visibility")).toBe("hidden");
  });

  describe("Stream row", () => {
    const SAMPLES = [
      { at: 0, count: 380 },
      { at: 600, count: 900 },
      { at: 2280, count: 1658 },
      { at: 3720, count: 1492 },
    ];

    function playhead(time, recordingId = "r1") {
      document.dispatchEvent(new CustomEvent("sk:playhead", {
        detail: { recordingId, time, duration: 3733, paused: false, rate: 1, live: false },
      }));
    }

    it("stays hidden until the Viewers panel publishes samples for this recording", () => {
      start();
      expect(hook("stats-stream").hidden).toBe(true);

      publishViewers({ recordingId: "r9", durationSec: 60, samples: SAMPLES });
      expect(hook("stats-stream").hidden).toBe(true);

      publishViewers({ recordingId: "r1", durationSec: 3733, samples: SAMPLES });
      expect(hook("stats-stream").hidden).toBe(false);
      expect(hook("stream-at").textContent).toBe("Viewers at 0:00");
      expect(hook("stream-viewers").textContent).toBe("380 · peak 1,658");
    });

    it("reads samples published before it opened, and follows the playhead", () => {
      publishViewers({ recordingId: "r1", durationSec: 3733, samples: SAMPLES });
      start();

      playhead(2300);
      expect(hook("stream-at").textContent).toBe("Viewers at 38:20");
      expect(hook("stream-viewers").textContent).toBe("1,658 · peak 1,658");

      playhead(10, "r9");
      expect(hook("stream-at").textContent).toBe("Viewers at 38:20");
    });

    it("stays hidden for a recording without samples", () => {
      publishViewers({ recordingId: "r1", durationSec: 3733, samples: [] });
      start();

      expect(hook("stats-stream").hidden).toBe(true);
    });

    it("adds the viewer figures to the copy-stats snapshot", async () => {
      const writeText = vi.fn(async () => {});
      Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
      publishViewers({ recordingId: "r1", durationSec: 3733, samples: SAMPLES });
      start();

      hook("copy-stats").click();
      await flush();

      const snapshot = JSON.parse(writeText.mock.calls[0][0]);
      expect(snapshot.viewers).toEqual({ now: 1492, peak: 1658, peakAt: 2280, average: 1108 });
      expect(snapshot.v).toBe(1);
    });

    it("copies null viewer figures without samples", async () => {
      const writeText = vi.fn(async () => {});
      Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
      start();

      hook("copy-stats").click();
      await flush();

      expect(JSON.parse(writeText.mock.calls[0][0]).viewers)
        .toEqual({ now: null, peak: null, peakAt: null, average: null });
    });
  });
});
