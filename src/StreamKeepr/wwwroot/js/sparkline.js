// One-series SVG sparkline used by the Stats for nerds tiles (player contract §10).
// Geometry is pure; drawSparkline only owns the SVG children. The 2 px stroke lives in
// components.css (.sk-tile .line), and the colour comes from a design token.

export const SPARK_WIDTH = 200;
export const SPARK_HEIGHT = 36;
export const SPARK_PAD = 3;

const SVG_NS = "http://www.w3.org/2000/svg";

function element(name, attributes) {
  const node = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) {
    if (value !== undefined && value !== null) node.setAttribute(key, String(value));
  }
  return node;
}

/**
 * Maps non-null values to points on a 0-based scale. Null samples keep their x slot,
 * so a gap in the data is visible instead of squeezing the remaining samples together.
 */
export function sparklineGeometry(
  values,
  { width = SPARK_WIDTH, height = SPARK_HEIGHT, pad = SPARK_PAD } = {}) {
  const list = Array.isArray(values) ? values : [];
  const finite = list
    .map((value, index) => ({ value, index }))
    .filter((point) => Number.isFinite(point.value));

  const maximum = finite.length > 0
    ? Math.max(...finite.map((point) => point.value))
    : 0;
  // A flat zero series still needs a valid scale.
  const scale = maximum > 0 ? maximum * 1.1 : 1;

  const points = finite.map((point) => ({
    index: point.index,
    value: point.value,
    x: list.length > 1 ? (point.index / (list.length - 1)) * width : width / 2,
    y: height - pad - (point.value / scale) * (height - pad * 2),
  }));

  return { width, height, pad, points };
}

/** Returns the point nearest a horizontal fraction of the plot (0..1). */
export function sparklinePointAt(geometry, fraction) {
  if (!geometry?.points?.length) return null;

  const clamped = Math.min(1, Math.max(0, Number.isFinite(fraction) ? fraction : 0));
  const target = clamped * geometry.width;
  let nearest = geometry.points[0];
  for (const point of geometry.points) {
    if (Math.abs(point.x - target) < Math.abs(nearest.x - target)) nearest = point;
  }
  return nearest;
}

export function formatSparklineTip(secondsAgo, value, unit, format = String) {
  const age = Math.max(0, Math.round(secondsAgo));
  const text = format(value);
  return `${age}s ago · ${text}${unit ? ` ${unit}` : ""}`;
}

/**
 * Replaces the SVG children in place. The crosshair is recreated with the line and stays
 * hidden until the tile's pointer handler moves it. The SVG itself stays aria-hidden.
 */
export function drawSparkline(svg, values, { color = "var(--primary)" } = {}) {
  if (!svg) return sparklineGeometry(values);

  const geometry = sparklineGeometry(values);
  svg.replaceChildren();
  svg.setAttribute("viewBox", `0 0 ${geometry.width} ${geometry.height}`);
  svg.setAttribute("preserveAspectRatio", "none");

  svg.append(element("line", {
    class: "base",
    x1: 0,
    x2: geometry.width,
    y1: geometry.height - 0.5,
    y2: geometry.height - 0.5,
  }));

  if (geometry.points.length >= 2) {
    svg.append(element("polyline", {
      class: "line",
      points: geometry.points.map((point) => `${point.x},${point.y}`).join(" "),
      stroke: color,
      "vector-effect": "non-scaling-stroke",
    }));
  } else if (geometry.points.length === 1) {
    const point = geometry.points[0];
    svg.append(element("circle", {
      class: "pt",
      cx: point.x,
      cy: point.y,
      r: 2,
      fill: color,
      "vector-effect": "non-scaling-stroke",
    }));
  }

  svg.append(element("line", {
    class: "xh",
    x1: 0,
    x2: 0,
    y1: 0,
    y2: geometry.height,
    visibility: "hidden",
    "vector-effect": "non-scaling-stroke",
  }));

  return geometry;
}
