import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createViewerChart } from "../../src/StreamKeepr/Components/Features/Player/ViewerChart.razor.js";
import { resetViewerStore, viewersFor } from "../../src/StreamKeepr/wwwroot/js/viewer-store.js";

// The Viewers panel's chart island (design system: Viewers; the chart is #211). These tests
// moved here from stats-panel.test.js when the chart left the stats panel.
const MARKUP = `
  <section data-viewers-panel>
    <button><span data-viewers-hint></span></button>
    <div data-sk-viewers>
      <div data-sk="viewer-readout">
        <span data-sk="viewer-at">Viewers at 0:00</span><span data-sk="viewer-count">—</span>
        <dl>
          <dt data-sk="viewer-now-label">Now</dt><dd data-sk="viewer-now">—</dd>
          <dt>Average</dt><dd data-sk="viewer-average">—</dd>
          <dt>Peak</dt><dd><span data-sk="viewer-peak">—</span> <span data-sk="viewer-peak-at"></span></dd>
        </dl>
      </div>
      <div data-sk="viewer-chart" role="img" aria-label="No viewer data for this recording.">
        <svg data-sk="viewer-svg" viewBox="0 0 1000 100"></svg><span data-sk="tip-viewers"></span>
      </div>
      <table data-sk="table-viewers"><tbody data-sk="rows-viewers"></tbody></table>
    </div>
  </section>`;

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

const withSamples = () => viewerFetch((url) => viewerBody(/\/recordings\/([^/]+)\//.exec(url)[1]));

async function flush() {
  for (let index = 0; index < 6; index++) await Promise.resolve();
}

function hook(name) {
  return document.querySelector(`[data-sk="${name}"]`);
}

function hint() {
  return document.querySelector("[data-viewers-hint]");
}

describe("ViewerChart.razor.js", () => {
  let dotNet;
  let chart;

  beforeEach(() => {
    vi.useFakeTimers();
    document.body.innerHTML = MARKUP;
    dotNet = fakeDotNet();
    resetViewerStore();
  });

  afterEach(() => {
    chart?.dispose();
    chart = null;
    vi.useRealTimers();
    document.body.replaceChildren();
  });

  function start({ state = "recording", fetch = viewerFetch(), chapters } = {}) {
    chart = createViewerChart(document.querySelector("[data-sk-viewers]"), { dotNet, fetch });
    chart.init({ recordingId: "r1", state, chapters });
    return chart;
  }

  describe("chart", () => {
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

    it("fetches the samples, draws the readout, chart and table, and tells .NET there are some", async () => {
      const fetch = withSamples();
      start({ state: "saved", fetch });
      await flush();

      expect(fetch).toHaveBeenCalledOnce();
      expect(fetch.calls[0].url).toBe("/api/recordings/r1/viewers");
      expect(dotNet.calls).toContainEqual(["SetHasSamples", true]);

      expect(hook("viewer-now-label").textContent).toBe("Last");
      expect(hook("viewer-now").textContent).toBe("1,492");
      expect(hook("viewer-peak").textContent).toBe("1,658");
      expect(hook("viewer-peak-at").textContent).toBe("at 38:00");
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
      // A finished recording has no live edge; the peak is marked on the line.
      expect(hook("viewer-chart").querySelector("[data-live-edge]")).toBeNull();
      expect(hook("viewer-chart").querySelector("[data-peak]")).not.toBeNull();

      const table = [...document.querySelectorAll('[data-sk="rows-viewers"] tr')]
        .map((row) => [...row.children].map((cell) => cell.textContent));
      expect(table).toHaveLength(5);
      expect(table[2]).toEqual(["38:00", "1,658"]);
    });

    it("marks chapter starts on the baseline and the peak on the signal", async () => {
      start({ state: "saved", fetch: withSamples(), chapters: [0, 750, 2280, 2830] });
      await flush();

      const chart = hook("viewer-chart");
      const chapters = [...chart.querySelectorAll("[data-chapter]")];
      expect(chapters).toHaveLength(3);
      expect(chapters.map((node) => node.style.left)).toEqual(["20.09%", "61.08%", "75.81%"]);

      const peak = chart.querySelector("[data-peak]");
      expect(peak).not.toBeNull();
      expect(peak.style.left).toBe("61.08%");
      expect(peak.style.top).toBe("17.1%");
    });

    it("fetches the final samples once when the capture finishes, then stops refreshing", async () => {
      const fetch = withSamples();
      start({ state: "recording", fetch });
      await flush();

      chart.update({ state: "saved" });
      await flush();
      expect(fetch).toHaveBeenCalledTimes(2);
      expect(hook("viewer-chart").querySelector("[data-live-edge]")).toBeNull();
      expect(hook("viewer-now-label").textContent).toBe("Last");

      vi.advanceTimersByTime(180_000);
      expect(fetch).toHaveBeenCalledTimes(2);
    });

    it("draws the live edge while recording and grows with each refresh", async () => {
      let samples = VIEWER_SAMPLES.slice(0, 3);
      let durationSec = 2300;
      start({
        state: "recording",
        fetch: viewerFetch(() => viewerBody("r1", samples, durationSec)),
      });
      await flush();

      const edge = () => hook("viewer-chart").querySelector("[data-live-edge]");
      expect(edge()).not.toBeNull();
      expect(edge().style.left).toBe("99.13%");
      expect(hook("viewer-chart").getAttribute("aria-label")).toContain("1,658 now.");
      expect(hook("viewer-now-label").textContent).toBe("Now");

      samples = VIEWER_SAMPLES;
      durationSec = 3733;
      vi.advanceTimersByTime(60_000);
      await flush();

      expect(document.querySelectorAll('[data-sk="rows-viewers"] tr')).toHaveLength(5);
      expect(edge().style.left).toBe("99.65%");
      expect(hook("viewer-now").textContent).toBe("1,492");
    });

    it("ignores a response that belongs to the previous recording", async () => {
      const resolvers = [];
      const fetch = viewerFetch((url) => new Promise((resolve) => resolvers.push({ url, resolve })));
      start({ state: "saved", fetch });

      chart.update({ recordingId: "r2", playlistUrl: "/media/ann/r2/index.m3u8", state: "saved" });
      expect(fetch.calls[0].signal.aborted).toBe(true);
      expect(fetch.calls[1].url).toBe("/api/recordings/r2/viewers");

      // r1 answers late (its abort was ignored), and then r2 answers with no samples.
      resolvers[0].resolve(viewerBody("r1"));
      await flush();
      expect(hook("viewer-now").textContent).toBe("—");

      resolvers[1].resolve(viewerBody("r2", [{ at: 0, count: 7 }], 60));
      await flush();
      expect(hook("viewer-now").textContent).toBe("7");
    });

    it("ignores an older refresh that answers after a newer one", async () => {
      const resolvers = [];
      const fetch = viewerFetch(() => new Promise((resolve) => resolvers.push(resolve)));
      start({ state: "recording", fetch });

      vi.advanceTimersByTime(60_000);
      expect(fetch).toHaveBeenCalledTimes(2);

      resolvers[1](viewerBody("r1", [{ at: 0, count: 50 }, { at: 60, count: 60 }], 61));
      await flush();
      resolvers[0](viewerBody("r1", [{ at: 0, count: 50 }], 1));
      await flush();

      expect(hook("viewer-now").textContent).toBe("60");
    });

    it("ignores a body for another recording", async () => {
      start({ state: "saved", fetch: viewerFetch(() => viewerBody("other")) });
      await flush();

      expect(hook("viewer-now").textContent).toBe("—");
    });

    it("follows the playhead in the readout and on the chart", async () => {
      start({ state: "saved", fetch: withSamples() });
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
      start({ state: "saved", fetch: withSamples() });
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
      start({ state: "recording", fetch: viewerFetch(() => viewerBody("r1", samples, 3733)) });
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
      start({ state: "saved", fetch: withSamples() });
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
      start({ state: "saved", fetch: withSamples() });
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

    it("reports no samples, so the panel stays hidden, and treats 404 the same", async () => {
      const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
      start({ state: "saved", fetch: viewerFetch(() => 404) });
      await flush();

      expect(dotNet.calls).toEqual([["SetHasSamples", false]]);
      expect(hint().textContent).toBe("");
      expect(warn).not.toHaveBeenCalled();
    });

    it("writes the collapsed peak summary and does not follow the playhead", async () => {
      start({ state: "saved", fetch: withSamples() });
      await flush();
      expect(hint().textContent).toBe("peak 1,658 at 38:00");

      playhead(2300);
      expect(hint().textContent).toBe("peak 1,658 at 38:00");
      expect(hook("viewer-at").textContent).toBe("Viewers at 38:20");
    });

    it("publishes the samples for the Statistics overlay", async () => {
      start({ state: "saved", fetch: withSamples() });
      await flush();

      expect(viewersFor("r1")).toEqual({ recordingId: "r1", durationSec: 3733, samples: VIEWER_SAMPLES });
    });

    it("fetches a finished recording once", async () => {
      const fetch = withSamples();
      start({ state: "saved", fetch });
      await flush();

      vi.advanceTimersByTime(180_000);
      await flush();
      expect(fetch).toHaveBeenCalledOnce();
    });

    it("refreshes every 60 s while live, and stops on dispose", async () => {
      const fetch = withSamples();
      start({ state: "recording", fetch });
      await flush();
      expect(fetch).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(60_000);
      await flush();
      expect(fetch).toHaveBeenCalledTimes(2);

      chart.dispose();
      chart = null;
      vi.advanceTimersByTime(180_000);
      expect(fetch).toHaveBeenCalledTimes(2);
    });

    it("aborts a pending request on dispose", () => {
      const pending = viewerFetch(() => new Promise(() => {}));
      start({ state: "saved", fetch: pending });

      chart.dispose();
      chart = null;
      expect(pending.calls[0].signal.aborted).toBe(true);
    });
  });
});
