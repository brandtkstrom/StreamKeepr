import { describe, expect, it } from "vitest";
import {
  formatCompactCount,
  formatViewerTip,
  viewerChartGeometry,
  viewerChartLabel,
  viewerChartPointAt,
  viewerChartTimeAt,
  viewerCountAt,
  viewerSummary,
} from "../../src/StreamKeepr/wwwroot/js/viewer-chart.js";

const SAMPLES = [
  { at: 0, count: 380 },
  { at: 600, count: 900 },
  { at: 2280, count: 1658 },
  { at: 3000, count: 1658 },
  { at: 3720, count: 1492 },
];

describe("viewerSummary", () => {
  it("derives now, peak with its first time, and the rounded average", () => {
    expect(viewerSummary(SAMPLES)).toEqual({
      now: 1492,
      peak: 1658,
      peakAt: 2280,
      average: 1218,
    });
  });

  it("is all null without samples", () => {
    expect(viewerSummary([])).toEqual({ now: null, peak: null, peakAt: null, average: null });
    expect(viewerSummary(undefined)).toEqual({ now: null, peak: null, peakAt: null, average: null });
  });
});

describe("viewerCountAt", () => {
  it("returns the sample at or immediately before the playhead", () => {
    expect(viewerCountAt(SAMPLES, 0)).toBe(380);
    expect(viewerCountAt(SAMPLES, 599.9)).toBe(380);
    expect(viewerCountAt(SAMPLES, 600)).toBe(900);
    expect(viewerCountAt(SAMPLES, 9999)).toBe(1492);
  });

  it("is null before the first sample or without samples", () => {
    expect(viewerCountAt([{ at: 10, count: 5 }], 9)).toBeNull();
    expect(viewerCountAt([], 9)).toBeNull();
  });
});

describe("formatting", () => {
  it("formats the hover tooltip as timecode and grouped count", () => {
    expect(formatViewerTip(2280, 1658)).toBe("38:00 · 1,658 viewers");
    expect(formatViewerTip(3733, 1)).toBe("1:02:13 · 1 viewer");
  });

  it("labels gridlines with compact numbers", () => {
    expect(formatCompactCount(0)).toBe("0");
    expect(formatCompactCount(500)).toBe("500");
    expect(formatCompactCount(1000)).toBe("1k");
    expect(formatCompactCount(2500)).toBe("2.5k");
    expect(formatCompactCount(2_000_000)).toBe("2M");
  });

  it("summarises the chart in one sentence for its img label", () => {
    expect(viewerChartLabel(SAMPLES, { live: true })).toBe(
      "Viewer count over the recording: from 380 at the start to a peak of 1,658 at 38:00, 1,492 now.");
    expect(viewerChartLabel(SAMPLES, { live: false })).toBe(
      "Viewer count over the recording: from 380 at the start to a peak of 1,658 at 38:00, 1,492 at the end.");
    expect(viewerChartLabel([{ at: 0, count: 1 }], { live: false })).toBe(
      "Viewer count over the recording: 1 viewer at 0:00.");
    expect(viewerChartLabel([], { live: false })).toBe("No viewer data for this recording.");
  });
});

describe("viewerChartGeometry", () => {
  it("plots the whole recording timeline, with x as recording time", () => {
    const geometry = viewerChartGeometry(
      [{ at: 0, count: 500 }, { at: 100, count: 1000 }],
      { durationSec: 100 });

    expect(geometry.domain).toBe(100);
    expect(geometry.top).toBe(1000);
    expect(geometry.linePath).toBe("M0 50 L1000 0");
    expect(geometry.areaPath).toBe("M0 50 L1000 0 L1000 100 L0 100 Z");
  });

  it("leaves room after the last sample when the recording runs on", () => {
    const geometry = viewerChartGeometry(SAMPLES, { durationSec: 3733 });
    const peak = geometry.points[2];

    expect(peak.x).toBeCloseTo(610.77, 2);
    expect(peak.y).toBeCloseTo(17.1, 2);
    expect(geometry.points.at(-1).x).toBeLessThan(1000);
  });

  it("extends the domain to a sample past the reported duration", () => {
    expect(viewerChartGeometry(SAMPLES, { durationSec: 3000 }).domain).toBe(3720);
    expect(viewerChartGeometry([{ at: 0, count: 3 }], { durationSec: 0 }).domain).toBe(1);
  });

  it("has three gridlines at 0, half and a rounded maximum", () => {
    const labels = (samples) => viewerChartGeometry(samples, { durationSec: 60 })
      .yTicks.map((tick) => [tick.label, tick.fraction]);

    expect(labels(SAMPLES)).toEqual([["0", 0], ["1k", 0.5], ["2k", 1]]);
    expect(labels([{ at: 0, count: 380 }])).toEqual([["0", 0], ["250", 0.5], ["500", 1]]);
    expect(labels([{ at: 0, count: 0 }])).toEqual([["0", 0], ["5", 0.5], ["10", 1]]);
  });

  it("labels the x axis with four or five timecodes, ending at the duration", () => {
    const labels = (durationSec) => viewerChartGeometry(SAMPLES, { durationSec })
      .xTicks.map((tick) => tick.label);

    expect(labels(3733)).toEqual(["0:00", "15:00", "30:00", "45:00", "1:02:13"]);
    expect(labels(4400)).toEqual(["0:00", "20:00", "40:00", "1:00:00", "1:13:20"]);
    expect(viewerChartGeometry([{ at: 0, count: 1 }], { durationSec: 120 })
      .xTicks.map((tick) => tick.label)).toEqual(["0:00", "0:30", "1:00", "1:30", "2:00"]);

    // Durations between the round steps still get four labels, never three.
    const shortLabels = (durationSec) => viewerChartGeometry([{ at: 0, count: 1 }], { durationSec })
      .xTicks.map((tick) => tick.label);
    expect(shortLabels(290)).toEqual(["0:00", "1:30", "3:00", "4:50"]);
    expect(shortLabels(1450)).toEqual(["0:00", "6:00", "12:00", "18:00", "24:10"]);
    for (const durationSec of [45, 61, 290, 599, 1450, 2500, 7322, 20_000, 80_000])
      expect(shortLabels(durationSec).length, `${durationSec} s`).toBeGreaterThanOrEqual(4);

    const ticks = viewerChartGeometry(SAMPLES, { durationSec: 3733 }).xTicks;
    expect(ticks[0].fraction).toBe(0);
    expect(ticks[1].fraction).toBeCloseTo(900 / 3733, 6);
    expect(ticks.at(-1).fraction).toBe(1);

    // A session that outgrows the day-scale steps still gets four or five labels.
    const weeks = viewerChartGeometry([{ at: 0, count: 1 }], { durationSec: 1_515_012 })
      .xTicks.map((tick) => tick.label);
    expect(weeks).toHaveLength(5);
    expect(weeks.at(-1)).toBe("420:50:12");
  });
});

describe("chart pointer mapping", () => {
  const geometry = viewerChartGeometry(SAMPLES, { durationSec: 3733 });

  it("maps a horizontal fraction to recording time, clamped to the timeline", () => {
    expect(viewerChartTimeAt(geometry, 0.5)).toBeCloseTo(1866.5, 6);
    expect(viewerChartTimeAt(geometry, -1)).toBe(0);
    expect(viewerChartTimeAt(geometry, 2)).toBe(3733);
  });

  it("finds the nearest sample to a fraction", () => {
    expect(viewerChartPointAt(geometry, 2300 / 3733)).toMatchObject({ at: 2280, count: 1658 });
    expect(viewerChartPointAt(geometry, 1)).toMatchObject({ at: 3720, count: 1492 });
    expect(viewerChartPointAt(viewerChartGeometry([], { durationSec: 10 }), 0.5)).toBeNull();
  });
});
