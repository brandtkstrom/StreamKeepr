import { describe, expect, it } from "vitest";
import {
  buildSnapshot,
  copyText,
  createRing,
  createStatsProbe,
  formatCodecs,
  formatDroppedFrames,
  formatErrors,
  formatErrorValue,
  formatHint,
  formatLastSegment,
  formatResolution,
  formatSegments,
  readLevel,
  sampleFrom,
  SAMPLE_RING_SIZE,
  SNAPSHOT_VERSION,
} from "../../src/StreamKeepr/wwwroot/js/stats.js";
import {
  drawSparkline,
  formatSparklineTip,
  sparklineGeometry,
  sparklinePointAt,
} from "../../src/StreamKeepr/wwwroot/js/sparkline.js";

class FakeHls {
  static Events = {
    MANIFEST_PARSED: "hlsManifestParsed",
    FRAG_LOADED: "hlsFragLoaded",
    LEVEL_SWITCHED: "hlsLevelSwitched",
    ERROR: "hlsError",
  };

  constructor() {
    this.handlers = new Map();
    this.levels = [{
      attrs: {
        "FRAME-RATE": "59.94",
        RESOLUTION: "1920x1080",
        CODECS: "avc1.64002A,mp4a.40.2",
      },
      bitrate: 6200000,
    }];
    this.currentLevel = 0;
    this.bandwidthEstimate = 48600000;
  }

  on(name, handler) {
    if (!this.handlers.has(name)) this.handlers.set(name, []);
    this.handlers.get(name).push(handler);
  }

  emit(name, data) {
    for (const handler of this.handlers.get(name) ?? []) handler(name, data);
  }
}

function fakeVideo(overrides = {}) {
  return {
    videoWidth: 1280,
    videoHeight: 720,
    currentTime: 10,
    buffered: {
      length: 1,
      start: () => 0,
      end: () => 38.4,
    },
    getVideoPlaybackQuality: () => ({ droppedVideoFrames: 3, totalVideoFrames: 41220 }),
    ...overrides,
  };
}

describe("ring buffer", () => {
  it("keeps the newest items and rejects an invalid capacity", () => {
    const ring = createRing(3);
    ring.push(1);
    ring.push(2);
    ring.push(3);
    ring.push(4);

    expect(ring.values()).toEqual([2, 3, 4]);
    expect(ring.size).toBe(3);

    ring.clear();
    expect(ring.values()).toEqual([]);
    expect(() => createRing(0)).toThrow(RangeError);
  });
});

describe("level projection", () => {
  it("reads attributes, codecs, and encoded size", () => {
    const level = readLevel(new FakeHls().levels[0], 0, 1);

    expect(level).toMatchObject({
      index: 0,
      count: 1,
      width: 1920,
      height: 1080,
      frameRate: 59.94,
      bitrate: 6200000,
      videoCodec: "avc1.64002A",
      audioCodec: "mp4a.40.2",
    });
  });

  it("keeps missing level data null", () => {
    expect(readLevel(null)).toBeNull();
    expect(readLevel({}, 2, 4)).toMatchObject({
      index: 2,
      count: 4,
      width: null,
      height: null,
      frameRate: null,
      bitrate: null,
      videoCodec: null,
      audioCodec: null,
    });
  });
});

describe("stats probe", () => {
  it("reads hls metrics, buffers, dropped frames, and fragment counters", () => {
    let clock = 1000;
    const video = fakeVideo();
    const probe = createStatsProbe({ video, now: () => clock });
    const hls = new FakeHls();

    probe.setView({ recordingId: "r1", playlistUrl: "/media/ann/r1/index.m3u8", state: "saved" });
    probe.attachHls(hls, FakeHls);
    hls.emit(FakeHls.Events.FRAG_LOADED, {
      frag: {
        sn: 1207,
        duration: 2,
        stats: { total: 1550000, loading: { start: 100, end: 282 } },
      },
    });
    clock += 4000;

    const metrics = probe.read();

    expect(metrics.mode).toBe("vod");
    expect(metrics.engine).toBe("hls");
    expect(metrics.level.width).toBe(1920);
    expect(metrics.bitrateBps).toBe(6200000);
    expect(metrics.frameRate).toBe(59.94);
    expect(metrics.bandwidthBps).toBe(48600000);
    expect(metrics.bufferSec).toBeCloseTo(28.4, 5);
    expect(metrics.droppedFrames).toBe(3);
    expect(metrics.totalFrames).toBe(41220);
    expect(metrics.lastSegment).toEqual({ index: 1207, durationSec: 2, loadMs: 182 });
    expect(metrics.segmentsLoaded).toBe(1);
    expect(metrics.errorCount).toBe(0);
    expect(metrics.errors).toEqual([]);
  });

  it("falls back to measured fragment bytes when the level has no bitrate", () => {
    const probe = createStatsProbe({ video: fakeVideo(), now: () => 0 });
    const hls = new FakeHls();
    hls.levels[0].bitrate = 0;

    probe.attachHls(hls, FakeHls);
    hls.emit(FakeHls.Events.FRAG_LOADED, {
      frag: { sn: 1, duration: 2 },
      stats: { total: 1000000, loading: { start: 0, end: 100 } },
    });

    expect(probe.read().bitrateBps).toBe(4000000);
  });

  it("does not count init-segment loads as media segments", () => {
    const probe = createStatsProbe({ video: fakeVideo(), now: () => 0 });
    const hls = new FakeHls();
    hls.levels[0].bitrate = 0;
    probe.attachHls(hls, FakeHls);

    hls.emit(FakeHls.Events.FRAG_LOADED, {
      frag: { sn: "initSegment", duration: null },
      stats: { total: 900, loading: { start: 0, end: 10 } },
    });

    const metrics = probe.read();
    expect(metrics.segmentsLoaded).toBe(0);
    expect(metrics.lastSegment).toBeNull();
    expect(metrics.bitrateBps).toBeNull();
  });

  it("counts non-fatal errors and keeps the newest ten", () => {
    const probe = createStatsProbe({ video: fakeVideo(), now: () => 0 });
    const hls = new FakeHls();
    probe.attachHls(hls, FakeHls);

    for (let index = 0; index < 12; index++)
      hls.emit(FakeHls.Events.ERROR, { type: "networkError", details: `frag${index}`, fatal: false });

    const metrics = probe.read();
    expect(metrics.errorCount).toBe(12);
    expect(metrics.errors).toHaveLength(10);
    expect(metrics.errors.at(-1)).toMatchObject({
      type: "networkError",
      details: "frag11",
      fatal: false,
    });
  });

  it("reports native HLS with dashes for hls.js-only values", () => {
    const probe = createStatsProbe({ video: fakeVideo(), now: () => 0 });
    probe.setView({ recordingId: "r1", playlistUrl: "/media/ann/r1/index.m3u8", state: "recording" });
    probe.setNative(true);

    const metrics = probe.read();
    expect(metrics.mode).toBe("native");
    expect(metrics.engine).toBe("native");
    expect(metrics.bitrateBps).toBeNull();
    expect(metrics.bandwidthBps).toBeNull();
    expect(metrics.level).toBeNull();
    expect(metrics.lastSegment).toBeNull();
    expect(metrics.segmentsLoaded).toBeNull();
    expect(metrics.frameRate).toBeNull();
    expect(metrics.bufferSec).toBeCloseTo(28.4, 5);
    expect(metrics.droppedFrames).toBe(3);
    expect(formatHint(metrics.engine, metrics.level)).toBe("Native HLS");
  });

  it("estimates frame rate from requestVideoFrameCallback metadata over the window", () => {
    const video = fakeVideo();
    let callback = null;
    video.requestVideoFrameCallback = (handler) => {
      callback = handler;
      return 1;
    };
    video.cancelVideoFrameCallback = () => {};

    const probe = createStatsProbe({ video, now: () => 0 });
    probe.reset();

    callback(0, { presentedFrames: 0, mediaTime: 0 });
    for (let frame = 1; frame <= 12; frame++)
      callback(frame * 16, { presentedFrames: frame, mediaTime: frame / 60 });

    expect(probe.read().frameRate).toBeCloseTo(60, 1);
  });
});

describe("sample and snapshot shapes", () => {
  it("maps probe values into the snapshot sample and keeps unavailable values null", () => {
    expect(sampleFrom({ at: 4.4, probe: null })).toEqual({
      at: 4,
      bitrateMbps: null,
      frameRate: null,
      bandwidthMbps: null,
      bufferSec: null,
      droppedFrames: null,
    });

    expect(sampleFrom({
      at: 2,
      probe: {
        bitrateBps: 6200000,
        frameRate: 59.94,
        bandwidthBps: 48600000,
        bufferSec: 28.44,
        droppedFrames: 3,
      },
    })).toEqual({
      at: 2,
      bitrateMbps: 6.2,
      frameRate: 59.94,
      bandwidthMbps: 48.6,
      bufferSec: 28.4,
      droppedFrames: 3,
    });
  });

  it("builds a versioned snapshot with the last twenty samples and ten errors", () => {
    const samples = Array.from({ length: 25 }, (_, index) => ({
      at: index * 2,
      bitrateMbps: 6.2,
      frameRate: null,
      bandwidthMbps: 48.6,
      bufferSec: 28.4,
      droppedFrames: 3,
    }));
    const errors = Array.from({ length: 12 }, (_, index) => ({
      at: index,
      type: "networkError",
      details: `frag${index}`,
      fatal: index === 11,
    }));

    const snapshot = buildSnapshot({
      recordingId: "r1",
      capturedAt: "2026-11-11T12:00:00.000Z",
      mode: "live",
      playlistUrl: "/media/ann/r1/index.m3u8",
      level: {
        index: 0,
        count: 1,
        width: 1920,
        height: 1080,
        frameRate: 59.94,
        bitrate: 6200000,
        videoCodec: "avc1.64002A",
        audioCodec: "mp4a.40.2",
      },
      samples,
      errors,
    });

    expect(snapshot.v).toBe(SNAPSHOT_VERSION);
    expect(snapshot.recordingId).toBe("r1");
    expect(snapshot.mode).toBe("live");
    expect(snapshot.level).toEqual({
      index: 0,
      width: 1920,
      height: 1080,
      frameRate: 59.94,
      bitrate: 6200000,
      videoCodec: "avc1.64002A",
      audioCodec: "mp4a.40.2",
    });
    expect(snapshot.samples).toHaveLength(SAMPLE_RING_SIZE);
    expect(snapshot.samples[0].at).toBe(10);
    expect(snapshot.samples[0].frameRate).toBeNull();
    expect(snapshot.errors).toHaveLength(10);
    expect(snapshot.errors.at(-1)).toMatchObject({
      type: "networkError",
      details: "frag11",
      fatal: true,
    });
  });
});

describe("human-readable values", () => {
  it("formats the header hint for source, levels, and native", () => {
    expect(formatHint("hls", { index: 0, count: 1 })).toBe("hls.js · source");
    expect(formatHint("hls", { index: 1, count: 4 })).toBe("hls.js · level 1 / 4");
    expect(formatHint("native", null)).toBe("Native HLS");
    expect(formatHint(null, null)).toBe("—");
  });

  it("formats resolution, codecs, dropped frames, segments, and the last segment", () => {
    expect(formatResolution(
      { width: 1920, height: 1080 },
      { width: 1280, height: 720 },
    )).toBe("1920×1080 → 1280×720 viewport");
    expect(formatResolution(
      { width: 1280, height: 720 },
      { width: 1280, height: 720 },
    )).toBe("1280×720");
    expect(formatResolution(null, null)).toBe("—");

    expect(formatCodecs({ videoCodec: "avc1.64002A", audioCodec: "mp4a.40.2" }))
      .toBe("avc1.64002A · mp4a.40.2");
    expect(formatCodecs(null)).toBe("—");

    expect(formatDroppedFrames(3, 41220)).toBe("3 of 41,220");
    expect(formatDroppedFrames(3, null)).toBe("3");
    expect(formatDroppedFrames(null, 41220)).toBe("—");

    expect(formatSegments(1207)).toBe("1,207");
    expect(formatSegments(null)).toBe("—");

    expect(formatLastSegment({ index: 1207, durationSec: 2, loadMs: 182 }))
      .toBe("#1,207 · 2.00 s · 182 ms");
    expect(formatLastSegment(null)).toBe("—");
  });

  it("formats errors as text so colour is never the only signal", () => {
    expect(formatErrors(0)).toBe("None");
    expect(formatErrors(1, { type: "networkError", details: "fragLoadError" }))
      .toBe("1 error · networkError · fragLoadError");
    expect(formatErrors(2, { type: "networkError", details: "" }))
      .toBe("2 errors · networkError");
    expect(formatErrorValue(0)).toBe("None");
    expect(formatErrorValue(1, { type: "networkError", details: "fragLoadError" }))
      .toBe("⚠ 1 error · networkError · fragLoadError");
  });
});

describe("clipboard", () => {
  it("reports success and failure without a legacy fallback", async () => {
    const writes = [];
    const clipboard = { writeText: async (text) => writes.push(text) };

    expect(await copyText("snapshot", clipboard)).toBe(true);
    expect(writes).toEqual(["snapshot"]);

    expect(await copyText("snapshot", { writeText: async () => { throw new Error("denied"); } }))
      .toBe(false);
    expect(await copyText("snapshot", null)).toBe(false);
  });
});

describe("sparkline", () => {
  it("keeps null samples as gaps and selects the nearest point", () => {
    const geometry = sparklineGeometry([1, null, 3]);

    expect(geometry.points).toHaveLength(2);
    expect(geometry.points[0].x).toBe(0);
    expect(geometry.points[1].x).toBe(200);
    expect(geometry.points[0].y).toBeGreaterThan(geometry.points[1].y);

    expect(sparklinePointAt(geometry, 0.9).index).toBe(2);
    expect(sparklinePointAt(geometry, 0.01).index).toBe(0);
    expect(sparklinePointAt({ points: [] }, 0.5)).toBeNull();
  });

  it("draws a 2 px-token line, a single point, and a crosshair", () => {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");

    drawSparkline(svg, [1, 2, 3], { color: "var(--primary)" });
    expect(svg.getAttribute("viewBox")).toBe("0 0 200 36");
    expect(svg.querySelector("polyline.line").getAttribute("stroke")).toBe("var(--primary)");
    expect(svg.querySelector("polyline.line").getAttribute("vector-effect")).toBe("non-scaling-stroke");
    expect(svg.querySelector("line.xh").getAttribute("visibility")).toBe("hidden");

    drawSparkline(svg, [4]);
    expect(svg.querySelector("polyline")).toBeNull();
    expect(svg.querySelector("circle.pt")).not.toBeNull();

    drawSparkline(svg, []);
    expect(svg.querySelector("polyline")).toBeNull();
    expect(svg.querySelector("circle.pt")).toBeNull();
    expect(svg.querySelector("line.base")).not.toBeNull();
  });

  it("formats the hover tooltip with age, value, and unit", () => {
    expect(formatSparklineTip(4, 6.24, "Mb/s", (value) => value.toFixed(1)))
      .toBe("4s ago · 6.2 Mb/s");
  });
});
