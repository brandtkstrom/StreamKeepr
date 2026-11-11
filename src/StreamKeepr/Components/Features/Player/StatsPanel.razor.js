import {
  buildSnapshot,
  copyText,
  createRing,
  formatCodecs,
  formatDroppedFrames,
  formatErrorValue,
  formatHint,
  formatLastSegment,
  formatResolution,
  formatSegments,
  sampleFrom,
  SAMPLE_INTERVAL_MS,
  SAMPLE_RING_SIZE,
  statsSource,
} from "/js/stats.js";
import { drawSparkline, formatSparklineTip, sparklinePointAt } from "/js/sparkline.js";

// Mirrors the data-sk hooks StatsPanel.razor renders (StatsPanelTests guards the markup).
const HOOKS = [
  "stats-toggle", "stats-hint", "stats-body", "copy-stats",
  "value-bitrate", "spark-bitrate", "tip-bitrate", "table-bitrate", "rows-bitrate",
  "value-frame", "spark-frame", "tip-frame", "table-frame", "rows-frame",
  "value-bandwidth", "spark-bandwidth", "tip-bandwidth", "table-bandwidth", "rows-bandwidth",
  "value-buffer", "spark-buffer", "tip-buffer", "table-buffer", "rows-buffer",
  "kv-resolution", "kv-codecs", "kv-dropped", "kv-decoder",
  "kv-last-segment", "kv-segments", "kv-errors", "kv-playlist",
];

const TILES = {
  bitrate: { key: "bitrateMbps", digits: 1 },
  frame: { key: "frameRate", digits: 2 },
  bandwidth: { key: "bandwidthMbps", digits: 1 },
  buffer: { key: "bufferSec", digits: 1 },
};

function formatNumber(value, digits) {
  if (!Number.isFinite(value)) return "—";
  const factor = 10 ** digits;
  return String(Math.round(value * factor) / factor);
}

function isLive(state) {
  return state === "recording" || state === "reconnecting";
}

function findPlayerRoot(root) {
  const inner = root.closest?.(".sk-main-inner") ?? root.parentElement;
  return inner?.querySelector("[data-sk-player]") ?? null;
}

function collect(root) {
  return Object.fromEntries(
    HOOKS.map((name) => [name, root.querySelector(`[data-sk="${name}"]`)]),
  );
}

function createStatsPanel(root, { dotNet = null, interval = SAMPLE_INTERVAL_MS, clock = () => performance.now() } = {}) {
  const el = collect(root);
  const listeners = new AbortController();
  const on = (target, type, handler) =>
    target?.addEventListener(type, handler, { signal: listeners.signal });

  let playerRoot = findPlayerRoot(root);
  let options = {};
  let open = false;
  let disposed = false;
  let windowStart = 0;
  let timer = 0;
  let samples = createRing(SAMPLE_RING_SIZE);
  let geometry = {};
  let lastMetrics = null;

  function player() {
    if (!playerRoot) playerRoot = findPlayerRoot(root);
    return playerRoot;
  }

  function playerButton() {
    return player()?.querySelector(`[data-sk="stats"]`) ?? null;
  }

  function source() {
    return statsSource(player());
  }

  function syncControls() {
    root.setAttribute("data-open", open ? "true" : "false");
    el["stats-toggle"]?.setAttribute("aria-checked", open ? "true" : "false");
    playerButton()?.setAttribute("aria-pressed", open ? "true" : "false");

    if (!el["stats-body"]) return;
    if (open) {
      el["stats-body"].removeAttribute("inert");
      if ("inert" in el["stats-body"]) el["stats-body"].inert = false;
    } else {
      el["stats-body"].setAttribute("inert", "");
      if ("inert" in el["stats-body"]) el["stats-body"].inert = true;
    }
  }

  function setValue(node, value, digits) {
    if (node) node.textContent = formatNumber(value, digits);
  }

  function renderPlaylist(url) {
    if (!el["kv-playlist"]) return;
    const text = url ?? "";
    el["kv-playlist"].textContent = text || "—";
    if (text) el["kv-playlist"].setAttribute("title", text);
    else el["kv-playlist"].removeAttribute("title");
  }

  function renderValues(metrics) {
    const latest = samples.values().at(-1) ?? null;
    for (const [name, tile] of Object.entries(TILES))
      setValue(el[`value-${name}`], latest?.[tile.key], tile.digits);

    if (el["stats-hint"])
      el["stats-hint"].textContent = metrics ? formatHint(metrics.engine, metrics.level) : "—";
    if (el["kv-resolution"])
      el["kv-resolution"].textContent = metrics
        ? formatResolution(metrics.level, metrics.viewport)
        : "—";
    if (el["kv-codecs"])
      el["kv-codecs"].textContent = metrics ? formatCodecs(metrics.level) : "—";
    if (el["kv-dropped"])
      el["kv-dropped"].textContent = metrics
        ? formatDroppedFrames(metrics.droppedFrames, metrics.totalFrames)
        : "—";
    if (el["kv-decoder"])
      el["kv-decoder"].textContent = metrics
        ? metrics.engine === "native" ? "Native HLS" : "MSE · hls.js"
        : "—";
    if (el["kv-last-segment"])
      el["kv-last-segment"].textContent = metrics
        ? formatLastSegment(metrics.lastSegment)
        : "—";
    if (el["kv-segments"])
      el["kv-segments"].textContent = metrics
        ? formatSegments(metrics.segmentsLoaded)
        : "—";

    const errorCount = metrics?.errorCount ?? 0;
    if (el["kv-errors"]) {
      el["kv-errors"].textContent = metrics
        ? formatErrorValue(errorCount, metrics.errors?.at(-1))
        : "—";
      el["kv-errors"].classList.toggle("warn", errorCount > 0);
    }

    renderPlaylist(options.playlistUrl ?? metrics?.playlistUrl);
  }

  function renderSparklines() {
    const values = samples.values();
    for (const [name, tile] of Object.entries(TILES)) {
      const series = values.map((sample) => sample[tile.key]);
      const svg = el[`spark-${name}`];
      geometry[name] = drawSparkline(svg, series, {
        color: svg?.dataset.sparkColor ?? "var(--primary)",
      });
      if (el[`tip-${name}`]) el[`tip-${name}`].textContent = "";
    }
  }

  function renderTables() {
    const values = samples.values();
    for (const [name, tile] of Object.entries(TILES)) {
      const body = el[`rows-${name}`];
      if (!body) continue;

      const rows = values.map((sample) => {
        const row = document.createElement("tr");
        const time = document.createElement("th");
        time.scope = "row";
        time.textContent = `${sample.at}s`;
        const value = document.createElement("td");
        value.textContent = formatNumber(sample[tile.key], tile.digits);
        row.append(time, value);
        return row;
      });

      body.replaceChildren(...rows);
    }
  }

  function sample() {
    if (!open || disposed) return;

    const metrics = source()?.read?.() ?? null;
    const at = Math.max(0, Math.round((clock() - windowStart) / 1000));
    samples.push(sampleFrom({ at, probe: metrics }));
    lastMetrics = metrics;
    renderValues(metrics);
    renderSparklines();
    renderTables();
  }

  function stopWindow() {
    clearInterval(timer);
    timer = 0;
  }

  function startWindow() {
    stopWindow();
    windowStart = clock();
    samples = createRing(SAMPLE_RING_SIZE);
    lastMetrics = null;
    // Show the current (possibly empty) window immediately, then every 2 s.
    sample();
    timer = setInterval(sample, interval);
  }

  async function persistOpen(value) {
    try {
      await dotNet?.invokeMethodAsync("SetStatsOpen", value);
    } catch (error) {
      // A closed circuit has no snackbar or preference store left to update; the DOM
      // already reflects the user's action.
      console.debug("Stream Keepr: stats state was not persisted.", error);
    }
  }

  async function notify(message) {
    try {
      await dotNet?.invokeMethodAsync("Notify", message);
    } catch (error) {
      console.debug("Stream Keepr: stats snackbar was not delivered.", error);
    }
  }

  function applyOpen(next, { persist = false } = {}) {
    if (disposed) return;
    const value = Boolean(next);
    const changed = value !== open;

    // inert would blur whatever is inside on some browsers, so move focus deliberately first.
    if (!value && el["stats-body"]?.contains(document.activeElement))
      el["stats-toggle"]?.focus();

    open = value;
    syncControls();

    if (changed) {
      if (open) startWindow();
      else stopWindow();
    }

    if (changed && persist) void persistOpen(open);
  }

  async function copy() {
    const metrics = source()?.read?.() ?? null;
    const snapshot = buildSnapshot({
      recordingId: options.recordingId ?? metrics?.recordingId,
      capturedAt: new Date().toISOString(),
      mode: metrics?.mode ?? (isLive(options.state) ? "live" : "vod"),
      playlistUrl: options.playlistUrl ?? metrics?.playlistUrl,
      level: metrics?.level ?? null,
      samples: samples.values(),
      errors: metrics?.errors ?? [],
    });

    const written = await copyText(JSON.stringify(snapshot, null, 2));
    await notify(written ? "Stats copied" : "Could not copy stats");
  }

  function bindHover(name) {
    const svg = el[`spark-${name}`];
    const tip = el[`tip-${name}`];
    if (!svg || !tip) return;

    on(svg, "pointermove", (event) => {
      const current = geometry[name];
      if (!current?.points?.length) return;

      const rect = svg.getBoundingClientRect();
      const fraction = rect.width > 0 ? (event.clientX - rect.left) / rect.width : 0;
      const point = sparklinePointAt(current, fraction);
      if (!point) return;

      const crosshair = svg.querySelector(".xh");
      if (crosshair) {
        crosshair.setAttribute("x1", String(point.x));
        crosshair.setAttribute("x2", String(point.x));
        crosshair.setAttribute("visibility", "visible");
      }

      const values = samples.values();
      const sampleValue = values[point.index];
      const latest = values.at(-1);
      const secondsAgo = sampleValue && latest
        ? Math.max(0, latest.at - sampleValue.at)
        : 0;
      const tile = TILES[name];
      tip.textContent = formatSparklineTip(
        secondsAgo,
        point.value,
        svg.dataset.sparkUnit ?? "",
        (value) => formatNumber(value, tile.digits),
      );
    });

    on(svg, "pointerleave", () => {
      svg.querySelector(".xh")?.setAttribute("visibility", "hidden");
      tip.textContent = "";
    });
  }

  function update(next) {
    if (disposed) return;

    const previous = options;
    options = { ...options, ...next };
    const recordingChanged = previous.recordingId !== options.recordingId;

    if (recordingChanged) {
      samples = createRing(SAMPLE_RING_SIZE);
      windowStart = clock();
      lastMetrics = null;
      renderValues(null);
      renderSparklines();
      renderTables();
      if (open) sample();
      return;
    }

    renderValues(lastMetrics);
  }

  function init(next) {
    options = next ?? {};
    applyOpen(Boolean(options.open));
    if (!open) {
      renderValues(null);
      renderSparklines();
      renderTables();
    }
  }

  function dispose() {
    if (disposed) return;
    disposed = true;
    stopWindow();
    listeners.abort();
  }

  on(el["stats-toggle"], "click", () => applyOpen(!open, { persist: true }));
  on(el["copy-stats"], "click", () => void copy());

  // The stats button lives inside the player, which the player module never re-renders.
  // Delegating from the document also survives the panel initializing before the button.
  on(document, "click", (event) => {
    const target = event.target instanceof Element
      ? event.target.closest(`[data-sk="stats"]`)
      : null;
    if (!target || !target.closest("[data-sk-player]")) return;
    event.preventDefault();
    applyOpen(!open, { persist: true });
  });

  for (const name of Object.keys(TILES)) bindHover(name);

  return { init, update, setOpen: applyOpen, dispose };
}

const panels = new WeakMap();

export function init(root, dotNet, options) {
  if (!root) return;
  panels.get(root)?.dispose();

  const panel = createStatsPanel(root, { dotNet });
  panels.set(root, panel);
  panel.init(options);
}

export function setOpen(root, open) {
  panels.get(root)?.setOpen(open);
}

export function update(root, options) {
  panels.get(root)?.update(options);
}

export function dispose(root) {
  const panel = panels.get(root);
  panel?.dispose();
  panels.delete(root);
}
