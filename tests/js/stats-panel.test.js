import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  createStatsProbe,
  registerStatsSource,
} from "../../src/StreamKeepr/wwwroot/js/stats.js";
import { createStatsPanel } from "../../src/StreamKeepr/Components/Features/Player/StatsPanel.razor.js";

const MARKUP = `
  <div class="sk-main-inner">
    <div data-sk-player>
      <button data-sk="stats" aria-label="Stats for nerds" aria-pressed="false"></button>
    </div>
    <section data-sk-stats data-open="true">
      <button data-sk="stats-toggle" role="switch" aria-label="Stats for nerds"
              aria-checked="true" aria-controls="stats-body"></button>
      <span data-sk="stats-hint"></span>
      <div id="stats-body" data-sk="stats-body">
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
        <div data-sk="stats-stream">
          <div data-sk="viewer-chart-empty"><p data-sk="stream-empty">No viewer data for this recording.</p></div>
          <div data-sk="viewer-tile" hidden>
            <div data-sk="viewer-at">Viewers at 0:00</div><div data-sk="viewer-count">—</div>
            <dl><dt>Now</dt><dd data-sk="viewer-now">—</dd><dt>Peak</dt><dd data-sk="viewer-peak">—</dd>
                <dt>Average</dt><dd data-sk="viewer-average">—</dd></dl>
          </div>
          <div data-sk="viewer-chart" role="img" aria-label="No viewer data for this recording." hidden>
            <svg data-sk="viewer-svg" viewBox="0 0 1000 100"></svg><span data-sk="tip-viewers"></span>
          </div>
          <table data-sk="table-viewers" hidden><tbody data-sk="rows-viewers"></tbody></table>
        </div>
        <button data-sk="copy-stats">Copy stats</button>
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

const VIEWER_SAMPLES = [
  { at: 0, count: 380 },
  { at: 600, count: 900 },
  { at: 2280, count: 1658 },
  { at: 3000, count: 1658 },
  { at: 3720, count: 1492 },
];

function viewerBody(recordingId = "r1", samples = VIEWER_SAMPLES, durationSec = 3733) {
  return { v: 1, recordingId, durationSec, samples };
}

/** A fake fetch: each call records its URL and signal and answers from `respond`. */
function viewerFetch(respond = (url) => viewerBody(/\/recordings\/([^/]+)\//.exec(url)[1], [])) {
  const calls = [];
  const fake = vi.fn(async (url, init) => {
    calls.push({ url, signal: init?.signal });
    const body = await respond(url, init);
    if (body === 404) return { ok: false, status: 404, json: async () => ({}) };
    return { ok: true, status: 200, json: async () => body };
  });
  fake.calls = calls;
  return fake;
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

  function start(open = true, { state = "recording", fetch = viewerFetch() } = {}) {
    panel = createStatsPanel(document.querySelector("[data-sk-stats]"), {
      dotNet,
      clock: () => now,
      interval: 2000,
      fetch,
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

  it("persists the panel switch through the shared stats callback", () => {
    start();
    const toggle = document.querySelector('[data-sk="stats-toggle"]');
    const body = document.querySelector('[data-sk="stats-body"]');
    const states = [];
    document.addEventListener("sk:stats-state", (event) => states.push(event.detail.open));

    toggle.click();

    expect(document.querySelector("[data-sk-stats]").dataset.open).toBe("false");
    expect(toggle.getAttribute("aria-checked")).toBe("false");
    expect(body.hasAttribute("inert")).toBe(true);
    expect(dotNet.calls).toContainEqual(["SetStatsOpen", false]);
    expect(states).toEqual([false]);

    toggle.click();
    expect(toggle.getAttribute("aria-checked")).toBe("true");
    expect(body.hasAttribute("inert")).toBe(false);
    expect(dotNet.calls).toContainEqual(["SetStatsOpen", true]);
    expect(states).toEqual([false, true]);
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
    expect(document.querySelector('[data-sk="stats-toggle"]').getAttribute("aria-checked"))
      .toBe("true");
    expect(document.querySelector('[data-sk="stats-body"]').hasAttribute("inert")).toBe(false);
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

  describe("viewer chart", () => {
    const withSamples = () => viewerFetch((url) =>
      viewerBody(/\/recordings\/([^/]+)\//.exec(url)[1]));

    function playhead(time, recordingId = "r1") {
      document.dispatchEvent(new CustomEvent("sk:playhead", {
        detail: { recordingId, time, duration: 3733, paused: false, rate: 1, live: false },
      }));
    }

    function chartBox() {
      hook("viewer-chart").getBoundingClientRect = () => ({
        left: 0, width: 1000, top: 0, height: 100, right: 1000, bottom: 100,
      });
    }

    it("fetches the samples on open and swaps the empty line for the tile, chart and table", async () => {
      const fetch = withSamples();
      start(true, { state: "saved", fetch });
      await flush();

      expect(fetch).toHaveBeenCalledOnce();
      expect(fetch.calls[0].url).toBe("/api/recordings/r1/viewers");
      expect(hook("viewer-chart-empty").hidden).toBe(true);
      expect(hook("viewer-tile").hidden).toBe(false);
      expect(hook("viewer-chart").hidden).toBe(false);
      expect(hook("table-viewers").hidden).toBe(false);

      expect(hook("viewer-now").textContent).toBe("1,492");
      expect(hook("viewer-peak").textContent).toBe("1,658 at 38:00");
      expect(hook("viewer-average").textContent).toBe("1,218");
      expect(hook("viewer-chart").getAttribute("aria-label")).toBe(
        "Viewer count over the recording: from 380 at the start to a peak of 1,658 at 38:00, 1,492 at the end.");

      const svg = hook("viewer-svg");
      expect(svg.querySelectorAll("[data-series='line']")).toHaveLength(1);
      expect(svg.querySelectorAll("[data-series='area']")).toHaveLength(1);
      expect(svg.querySelectorAll("[data-gridline]")).toHaveLength(3);
      const xLabels = [...hook("viewer-chart").querySelectorAll("[data-axis='x']")].map((n) => n.textContent);
      expect(xLabels).toEqual(["0:00", "15:00", "30:00", "45:00", "1:02:13"]);
      const yLabels = [...hook("viewer-chart").querySelectorAll("[data-axis='y']")].map((n) => n.textContent);
      expect(yLabels).toEqual(["0", "1k", "2k"]);
      // A finished recording has no live edge.
      expect(hook("viewer-chart").querySelector("[data-live-edge]")).toBeNull();

      const table = [...document.querySelectorAll('[data-sk="rows-viewers"] tr')]
        .map((row) => [...row.children].map((cell) => cell.textContent));
      expect(table).toHaveLength(5);
      expect(table[2]).toEqual(["38:00", "1,658"]);
    });

    it("keeps the empty line and hides the tile and chart without samples", async () => {
      start(true, { state: "saved", fetch: viewerFetch() });
      await flush();

      expect(hook("viewer-chart-empty").hidden).toBe(false);
      expect(hook("viewer-tile").hidden).toBe(true);
      expect(hook("viewer-chart").hidden).toBe(true);
      expect(hook("table-viewers").hidden).toBe(true);
    });

    it("treats an unknown recording as having no samples, not as a failure", async () => {
      const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
      start(true, { state: "saved", fetch: viewerFetch(() => 404) });
      await flush();

      expect(hook("viewer-chart-empty").hidden).toBe(false);
      expect(hook("viewer-tile").hidden).toBe(true);
      expect(warn).not.toHaveBeenCalled();
    });

    it("fetches the final samples once when the capture finishes, then stops refreshing", async () => {
      const fetch = withSamples();
      start(true, { state: "recording", fetch });
      await flush();

      panel.update({ state: "saved" });
      await flush();
      expect(fetch).toHaveBeenCalledTimes(2);
      expect(hook("viewer-chart").querySelector("[data-live-edge]")).toBeNull();

      vi.advanceTimersByTime(180_000);
      expect(fetch).toHaveBeenCalledTimes(2);
    });

    it("does not fetch while closed", async () => {
      const fetch = withSamples();
      start(false, { state: "saved", fetch });
      await flush();

      expect(fetch).not.toHaveBeenCalled();
      panel.setOpen(true);
      await flush();
      expect(fetch).toHaveBeenCalledOnce();
    });

    it("fetches a finished recording once, however often the panel reopens", async () => {
      const fetch = withSamples();
      start(true, { state: "saved", fetch });
      await flush();

      vi.advanceTimersByTime(120_000);
      panel.setOpen(false);
      panel.setOpen(true);
      await flush();

      expect(fetch).toHaveBeenCalledOnce();
    });

    it("refreshes every 60 s while live and on reopen, and stops when closed", async () => {
      const fetch = withSamples();
      start(true, { state: "recording", fetch });
      await flush();
      expect(fetch).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(60_000);
      await flush();
      expect(fetch).toHaveBeenCalledTimes(2);

      panel.setOpen(false);
      vi.advanceTimersByTime(180_000);
      expect(fetch).toHaveBeenCalledTimes(2);

      panel.setOpen(true);
      await flush();
      expect(fetch).toHaveBeenCalledTimes(3);
    });

    it("draws the live edge while recording and grows with each refresh", async () => {
      let samples = VIEWER_SAMPLES.slice(0, 3);
      let durationSec = 2300;
      start(true, {
        state: "recording",
        fetch: viewerFetch(() => viewerBody("r1", samples, durationSec)),
      });
      await flush();

      const edge = () => hook("viewer-chart").querySelector("[data-live-edge]");
      expect(edge()).not.toBeNull();
      expect(edge().style.left).toBe("99.13%");
      expect(hook("viewer-chart").getAttribute("aria-label")).toContain("1,658 now.");

      samples = VIEWER_SAMPLES;
      durationSec = 3733;
      vi.advanceTimersByTime(60_000);
      await flush();

      expect(document.querySelectorAll('[data-sk="rows-viewers"] tr')).toHaveLength(5);
      expect(edge().style.left).toBe("99.65%");
      expect(hook("viewer-now").textContent).toBe("1,492");
    });

    it("aborts the request on close and on dispose", async () => {
      const pending = viewerFetch(() => new Promise(() => {}));
      start(true, { state: "saved", fetch: pending });
      panel.setOpen(false);
      expect(pending.calls[0].signal.aborted).toBe(true);

      panel.setOpen(true);
      expect(pending.calls[1].signal.aborted).toBe(false);
      panel.dispose();
      panel = null;
      expect(pending.calls[1].signal.aborted).toBe(true);
    });

    it("ignores a response that belongs to the previous recording", async () => {
      const resolvers = [];
      const fetch = viewerFetch((url) => new Promise((resolve) => resolvers.push({ url, resolve })));
      start(true, { state: "saved", fetch });

      panel.update({ recordingId: "r2", playlistUrl: "/media/ann/r2/index.m3u8", state: "saved" });
      expect(fetch.calls[0].signal.aborted).toBe(true);
      expect(fetch.calls[1].url).toBe("/api/recordings/r2/viewers");

      // r1 answers late (its abort was ignored), and then r2 answers with no samples.
      resolvers[0].resolve(viewerBody("r1"));
      await flush();
      expect(hook("viewer-tile").hidden).toBe(true);

      resolvers[1].resolve(viewerBody("r2", [{ at: 0, count: 7 }], 60));
      await flush();
      expect(hook("viewer-now").textContent).toBe("7");
    });

    it("ignores an older refresh that answers after a newer one", async () => {
      const resolvers = [];
      const fetch = viewerFetch(() => new Promise((resolve) => resolvers.push(resolve)));
      start(true, { state: "recording", fetch });

      vi.advanceTimersByTime(60_000);
      expect(fetch).toHaveBeenCalledTimes(2);

      resolvers[1](viewerBody("r1", [{ at: 0, count: 50 }, { at: 60, count: 60 }], 61));
      await flush();
      resolvers[0](viewerBody("r1", [{ at: 0, count: 50 }], 1));
      await flush();

      expect(hook("viewer-now").textContent).toBe("60");
    });

    it("ignores a body for another recording", async () => {
      start(true, { state: "saved", fetch: viewerFetch(() => viewerBody("other")) });
      await flush();

      expect(hook("viewer-tile").hidden).toBe(true);
    });

    it("follows the playhead in the tile and on the chart", async () => {
      start(true, { state: "saved", fetch: withSamples() });
      await flush();

      playhead(2300);
      expect(hook("viewer-at").textContent).toBe("Viewers at 38:20");
      expect(hook("viewer-count").textContent).toBe("1,658");
      const line = hook("viewer-chart").querySelector("[data-playhead]");
      expect(line.style.left).toBe("61.61%");
      expect(line.querySelector("[data-playhead-tag]").textContent).toBe("38:20");

      // Another recording's player events are not this chart's playhead.
      playhead(10, "r9");
      expect(hook("viewer-at").textContent).toBe("Viewers at 38:20");

      playhead(599);
      expect(hook("viewer-count").textContent).toBe("380");
    });

    it("shows a hover tooltip and clears it on leave", async () => {
      start(true, { state: "saved", fetch: withSamples() });
      await flush();
      chartBox();

      const chart = hook("viewer-chart");
      chart.dispatchEvent(new MouseEvent("pointermove", { clientX: 611, bubbles: true }));
      expect(hook("tip-viewers").textContent).toBe("38:00 · 1,658 viewers");

      chart.dispatchEvent(new MouseEvent("pointerleave"));
      expect(hook("tip-viewers").textContent).toBe("");
    });

    it("keeps an active tooltip through a live refresh", async () => {
      let samples = VIEWER_SAMPLES.slice(0, 3);
      start(true, { state: "recording", fetch: viewerFetch(() => viewerBody("r1", samples, 3733)) });
      await flush();
      chartBox();

      const chart = hook("viewer-chart");
      chart.dispatchEvent(new MouseEvent("pointermove", { clientX: 611, bubbles: true }));
      expect(hook("tip-viewers").textContent).toBe("38:00 · 1,658 viewers");

      samples = [...VIEWER_SAMPLES.slice(0, 2), { at: 2280, count: 1700 }];
      vi.advanceTimersByTime(60_000);
      await flush();

      expect(hook("tip-viewers").textContent).toBe("38:00 · 1,700 viewers");
      expect(hook("viewer-svg").querySelector("[data-crosshair]").getAttribute("visibility")).toBe("visible");
    });

    it("marks the chart active while touched, so the tooltip shows without hover", async () => {
      start(true, { state: "saved", fetch: withSamples() });
      await flush();
      chartBox();
      const chart = hook("viewer-chart");

      chart.dispatchEvent(new MouseEvent("pointerdown", { clientX: 611, button: 0, bubbles: true }));
      expect(chart.hasAttribute("data-active")).toBe(true);
      expect(hook("tip-viewers").textContent).toBe("38:00 · 1,658 viewers");

      chart.dispatchEvent(new MouseEvent("pointerup", { clientX: 611, bubbles: true }));
      expect(chart.hasAttribute("data-active")).toBe(false);
    });

    it("seeks through .NET on click and while dragging", async () => {
      start(true, { state: "saved", fetch: withSamples() });
      await flush();
      chartBox();
      const chart = hook("viewer-chart");
      const seeks = () => dotNet.calls.filter(([name]) => name === "Seek").map(([, at]) => at);

      chart.dispatchEvent(new MouseEvent("pointerdown", { clientX: 500, button: 0, bubbles: true }));
      await flush();
      expect(seeks()).toEqual([1866.5]);

      chart.dispatchEvent(new MouseEvent("pointermove", { clientX: 250, buttons: 1, bubbles: true }));
      await flush();
      expect(seeks()).toEqual([1866.5, 933.25]);

      chart.dispatchEvent(new MouseEvent("pointerup", { clientX: 250, bubbles: true }));
      chart.dispatchEvent(new MouseEvent("pointermove", { clientX: 900, bubbles: true }));
      await flush();
      expect(seeks()).toEqual([1866.5, 933.25]);
    });

    it("adds the viewer figures to the copy-stats snapshot", async () => {
      const writeText = vi.fn(async () => {});
      Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
      start(true, { state: "saved", fetch: withSamples() });
      await flush();

      hook("copy-stats").click();
      await flush();

      const snapshot = JSON.parse(writeText.mock.calls[0][0]);
      expect(snapshot.viewers).toEqual({ now: 1492, peak: 1658, peakAt: 2280, average: 1218 });
      expect(snapshot.v).toBe(1);
      expect(snapshot.samples).toHaveLength(1);
    });

    it("copies null viewer figures without samples", async () => {
      const writeText = vi.fn(async () => {});
      Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
      start(true, { state: "saved", fetch: viewerFetch() });
      await flush();

      hook("copy-stats").click();
      await flush();

      expect(JSON.parse(writeText.mock.calls[0][0]).viewers)
        .toEqual({ now: null, peak: null, peakAt: null, average: null });
    });
  });
});
