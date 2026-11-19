// Viewer-count chart for the Stats panel's Stream section (player contract §10, StatsPanel card).
// The figures and geometry are pure so Vitest covers them without a browser; drawViewerChart
// only owns the chart's SVG and label children. StatsPanel.razor.js owns fetching and events.

import { formatCount } from "/js/stats.js";
import { formatTimecode } from "/js/timecode.js";

function viewers(count) {
  return `${formatCount(count)} ${count === 1 ? "viewer" : "viewers"}`;
}

/** The hover tooltip: "38:00 · 1,658 viewers". */
export function formatViewerTip(seconds, count) {
  return `${formatTimecode(seconds)} · ${viewers(count)}`;
}

/** Gridline labels: 0 · 500 · 1k · 2.5k · 2M. */
export function formatCompactCount(value) {
  if (!Number.isFinite(value)) return "";
  const trim = (number) => String(Math.round(number * 10) / 10);
  if (Math.abs(value) >= 1e6) return `${trim(value / 1e6)}M`;
  if (Math.abs(value) >= 1e3) return `${trim(value / 1e3)}k`;
  return String(Math.round(value));
}

/** Now (latest), Peak (maximum and its first time) and Average (rounded mean); null when empty. */
export function viewerSummary(samples) {
  const list = Array.isArray(samples) ? samples : [];
  if (list.length === 0) return { now: null, peak: null, peakAt: null, average: null };

  let peak = list[0];
  let total = 0;
  for (const sample of list) {
    if (sample.count > peak.count) peak = sample;
    total += sample.count;
  }

  return {
    now: list.at(-1).count,
    peak: peak.count,
    peakAt: peak.at,
    average: Math.round(total / list.length),
  };
}

/**
 * The count at, or immediately before, the playhead; null before the first sample. Samples are
 * oldest first, so this is a binary search: it runs on every playhead event.
 */
export function viewerCountAt(samples, seconds) {
  const list = Array.isArray(samples) ? samples : [];
  let low = 0;
  let high = list.length - 1;
  let found = null;
  while (low <= high) {
    const middle = (low + high) >> 1;
    if (list[middle].at <= seconds) {
      found = list[middle].count;
      low = middle + 1;
    } else {
      high = middle - 1;
    }
  }
  return found;
}

/** One sentence for the chart's role="img" label. */
export function viewerChartLabel(samples, { live = false } = {}) {
  const list = Array.isArray(samples) ? samples : [];
  if (list.length === 0) return "No viewer data for this recording.";

  const prefix = "Viewer count over the recording:";
  if (list.length === 1)
    return `${prefix} ${viewers(list[0].count)} at ${formatTimecode(list[0].at)}.`;

  const summary = viewerSummary(list);
  const last = `${formatCount(summary.now)} ${live ? "now" : "at the end"}`;
  return `${prefix} from ${formatCount(list[0].count)} at the start to a peak of ` +
    `${formatCount(summary.peak)} at ${formatTimecode(summary.peakAt)}, ${last}.`;
}

// The plot is drawn in a fixed 1000 × 100 viewBox stretched to the chart (preserveAspectRatio
// none, non-scaling strokes); labels and the playhead are HTML positioned in percent.
export const VIEWER_CHART_WIDTH = 1000;
export const VIEWER_CHART_HEIGHT = 100;

// Candidate x-axis steps in seconds, dense enough that most durations get four or five labels.
const TIME_STEPS = [
  1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300, 360, 480, 600, 900, 1200, 1800,
  2400, 3600, 5400, 7200, 10800, 14400, 21600, 28800, 43200, 86400,
];
const MIN_TIME_LABELS = 4;
const MAX_TIME_LABELS = 5;
const MIN_TOP = 10;

/** The y-axis maximum: the first 1/2/5 × 10ⁿ at or above the peak (never below 10). */
function niceTop(peak) {
  if (!(peak > MIN_TOP)) return MIN_TOP;
  const magnitude = 10 ** Math.floor(Math.log10(peak));
  for (const mantissa of [1, 2, 5, 10]) {
    if (mantissa * magnitude >= peak) return mantissa * magnitude;
  }
  return 10 * magnitude;
}

function timeTicks(domain) {
  const ticksFor = (step) => {
    const ticks = [0];
    for (let at = step; at < domain; at += step) ticks.push(at);
    // An interior label within half a step of the end would collide with the end label.
    if (ticks.length > 1 && domain - ticks.at(-1) < step / 2) ticks.pop();
    ticks.push(domain);
    return ticks;
  };

  // The smallest step with four or five labels; failing that (a very short recording) the
  // smallest with at most five.
  const fits = (candidate, minimum) => {
    const count = ticksFor(candidate).length;
    return count >= minimum && count <= MAX_TIME_LABELS;
  };
  const step = TIME_STEPS.find((candidate) => fits(candidate, MIN_TIME_LABELS))
    ?? TIME_STEPS.find((candidate) => fits(candidate, 0))
    ?? TIME_STEPS.at(-1);
  return ticksFor(step).map((at) => ({
    at,
    label: formatTimecode(at),
    fraction: at / domain,
  }));
}

const round2 = (value) => Math.round(value * 100) / 100;

/**
 * Maps samples onto the recording timeline. x runs from 0 to the recording duration (or the
 * last sample, if later); y from 0 to a rounded maximum. Samples must be oldest first.
 */
export function viewerChartGeometry(samples, { durationSec = 0 } = {}) {
  const list = Array.isArray(samples) ? samples : [];
  const lastAt = list.at(-1)?.at ?? 0;
  const domain = Math.max(Number.isFinite(durationSec) ? durationSec : 0, lastAt, 1);
  const top = niceTop(viewerSummary(list).peak ?? 0);

  const points = list.map((sample) => ({
    at: sample.at,
    count: sample.count,
    x: round2((sample.at / domain) * VIEWER_CHART_WIDTH),
    y: round2(VIEWER_CHART_HEIGHT - (sample.count / top) * VIEWER_CHART_HEIGHT),
  }));

  const linePath = points
    .map((point, index) => `${index === 0 ? "M" : "L"}${point.x} ${point.y}`)
    .join(" ");
  const areaPath = points.length > 0
    ? `${linePath} L${points.at(-1).x} ${VIEWER_CHART_HEIGHT} L${points[0].x} ${VIEWER_CHART_HEIGHT} Z`
    : "";

  return {
    width: VIEWER_CHART_WIDTH,
    height: VIEWER_CHART_HEIGHT,
    domain,
    top,
    points,
    linePath,
    areaPath,
    xTicks: timeTicks(domain),
    yTicks: [0, 0.5, 1].map((fraction) => ({
      value: top * fraction,
      label: formatCompactCount(top * fraction),
      fraction,
    })),
  };
}

const clamp01 = (value) => Math.min(1, Math.max(0, Number.isFinite(value) ? value : 0));

/** Recording time under a horizontal fraction (0..1) of the plot. */
export function viewerChartTimeAt(geometry, fraction) {
  return clamp01(fraction) * geometry.domain;
}

/** The sample nearest a horizontal fraction of the plot, or null without samples. */
export function viewerChartPointAt(geometry, fraction) {
  if (!geometry?.points?.length) return null;
  const target = clamp01(fraction) * geometry.width;
  let nearest = geometry.points[0];
  for (const point of geometry.points) {
    if (Math.abs(point.x - target) < Math.abs(nearest.x - target)) nearest = point;
  }
  return nearest;
}

const SVG_NS = "http://www.w3.org/2000/svg";
const percent = (fraction) => `${round2(clamp01(fraction) * 100)}%`;

function svgElement(name, attributes) {
  const node = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, String(value));
  return node;
}

function htmlElement(name, attributes, text = "") {
  const node = document.createElement(name);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
  if (text) node.textContent = text;
  return node;
}

/**
 * Redraws the chart: gridlines, area and line in the SVG, and the HTML axis labels, playhead
 * and live-edge dot in the chart element. Everything drawn here is presentational; the chart
 * element is role="img" with a summary label, and the samples live in the hidden table.
 */
export function drawViewerChart(chart, svg, geometry, { live = false } = {}) {
  if (!chart || !svg) return geometry;

  svg.replaceChildren();
  svg.setAttribute("viewBox", `0 0 ${geometry.width} ${geometry.height}`);
  svg.setAttribute("preserveAspectRatio", "none");

  const grid = svgElement("g", { class: "grid" });
  for (const tick of geometry.yTicks) {
    const y = geometry.height - tick.fraction * geometry.height;
    grid.append(svgElement("line", {
      x1: 0, x2: geometry.width, y1: y, y2: y, "data-gridline": "", "vector-effect": "non-scaling-stroke",
    }));
  }
  svg.append(grid);
  if (geometry.points.length > 0) {
    svg.append(svgElement("path", { class: "area", "data-series": "area", d: geometry.areaPath }));
    svg.append(svgElement("path", {
      class: "line", "data-series": "line", d: geometry.linePath, "vector-effect": "non-scaling-stroke",
    }));
  }
  svg.append(svgElement("line", {
    class: "xh", "data-crosshair": "", x1: 0, x2: 0, y1: 0, y2: geometry.height,
    visibility: "hidden", "vector-effect": "non-scaling-stroke",
  }));

  chart.querySelector("[data-chart-overlay]")?.remove();
  const overlay = htmlElement("div", { class: "overlay", "data-chart-overlay": "", "aria-hidden": "true" });
  for (const tick of geometry.yTicks) {
    const label = htmlElement("span", { class: "ylab", "data-axis": "y" }, tick.label);
    label.style.top = percent(1 - tick.fraction);
    overlay.append(label);
  }
  geometry.xTicks.forEach((tick, index) => {
    const edge = index === 0 ? " first" : index === geometry.xTicks.length - 1 ? " last" : "";
    const label = htmlElement("span", { class: `xlab${edge}`, "data-axis": "x" }, tick.label);
    label.style.left = percent(tick.fraction);
    overlay.append(label);
  });

  const playhead = htmlElement("span", { class: "playhead", "data-playhead": "" });
  playhead.append(
    htmlElement("span", { class: "knob", "data-playhead-knob": "" }),
    htmlElement("span", { class: "tag", "data-playhead-tag": "" }));
  overlay.append(playhead);

  const last = geometry.points.at(-1);
  if (live && last) {
    const dot = htmlElement("span", { class: "edge", "data-live-edge": "" });
    dot.style.left = percent(last.x / geometry.width);
    dot.style.top = percent(last.y / geometry.height);
    overlay.append(dot);
  }

  chart.append(overlay);
  return geometry;
}

/** Moves the playhead line, its knob on the series and its timecode tag. */
export function placeViewerPlayhead(chart, geometry, seconds, count) {
  const playhead = chart?.querySelector("[data-playhead]");
  if (!playhead || !geometry) return;

  playhead.style.left = percent(seconds / geometry.domain);
  const knob = playhead.querySelector("[data-playhead-knob]");
  if (knob) {
    knob.hidden = !Number.isFinite(count);
    if (Number.isFinite(count)) knob.style.top = percent(1 - count / geometry.top);
  }
  const tag = playhead.querySelector("[data-playhead-tag]");
  if (tag) tag.textContent = formatTimecode(seconds);
}
