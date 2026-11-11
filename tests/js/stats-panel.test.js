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

  function start(open = true) {
    panel = createStatsPanel(document.querySelector("[data-sk-stats]"), {
      dotNet,
      clock: () => now,
      interval: 2000,
    });
    panel.init({
      recordingId: "r1",
      playlistUrl: "/media/ann/r1/index.m3u8",
      state: "recording",
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
});
