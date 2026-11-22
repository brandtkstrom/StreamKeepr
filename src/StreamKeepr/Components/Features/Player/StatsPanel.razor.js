import {
  buildSnapshot,
  copyText,
  formatCount,
  createRing,
  formatCodecs,
  formatDroppedFrames,
  formatErrorValue,
  formatLastSegment,
  formatResolution,
  formatSegments,
  sampleFrom,
  SAMPLE_INTERVAL_MS,
  SAMPLE_RING_SIZE,
  statsSource,
} from "/js/stats.js";
import { drawSparkline, formatSparklineTip, sparklinePointAt } from "/js/sparkline.js";
import { formatTimecode } from "/js/timecode.js";
import { viewerCountAt, viewerSummary } from "/js/viewer-chart.js";
import { subscribeViewers, viewersFor } from "/js/viewer-store.js";
import { isLiveState } from "/js/recording-state.js";

// Mirrors the data-sk hooks StatsPanel.razor renders (StatsPanelTests guards the markup).
const HOOKS = [
  "stats-close", "stats-body", "copy-stats",
  "value-bitrate", "spark-bitrate", "tip-bitrate", "table-bitrate", "rows-bitrate",
  "value-frame", "spark-frame", "tip-frame", "table-frame", "rows-frame",
  "value-bandwidth", "spark-bandwidth", "tip-bandwidth", "table-bandwidth", "rows-bandwidth",
  "value-buffer", "spark-buffer", "tip-buffer", "table-buffer", "rows-buffer",
  "kv-resolution", "kv-codecs", "kv-dropped", "kv-decoder",
  "kv-last-segment", "kv-segments", "kv-errors", "kv-playlist",
  "stats-stream", "stream-at", "stream-viewers",
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

// The overlay sits inside the player's frame; the phone tab finds the player on the page.
function findPlayerRoot(root) {
  const own = root.closest?.("[data-sk-player]");
  if (own) return own;
  const inner = root.closest?.(".sk-main-inner") ?? root.parentElement;
  return inner?.querySelector("[data-sk-player]") ?? null;
}

function collect(root) {
  return Object.fromEntries(
    HOOKS.map((name) => [name, root.querySelector(`[data-sk="${name}"]`)]),
  );
}

export function createStatsPanel(root, {
  dotNet = null,
  interval = SAMPLE_INTERVAL_MS,
  clock = () => performance.now(),
} = {}) {
  const el = collect(root);
  const listeners = new AbortController();
  const on = (target, type, handler) =>
    target?.addEventListener(type, handler, { signal: listeners.signal });

  let playerRoot = findPlayerRoot(root);
  // The phone Stats tab is always open; only the overlay opens and closes.
  const tab = root.getAttribute("data-chrome") === "tab";
  let options = {};
  let open = false;
  let disposed = false;
  let windowStart = 0;
  let timer = 0;
  let samples = createRing(SAMPLE_RING_SIZE);
  let geometry = {};
  let lastMetrics = null;
  let frameWindowStarted = false;
  let hover = { name: null, fraction: null };
  let playheadAt = 0;

  function player() {
    if (!playerRoot) playerRoot = findPlayerRoot(root);
    return playerRoot;
  }

  function source() {
    return statsSource(player());
  }

  function beginFrameWindow() {
    if (frameWindowStarted) return;
    const probe = source();
    if (!probe?.beginStatsWindow) return;
    probe.beginStatsWindow();
    frameWindowStarted = true;
  }

  function endFrameWindow() {
    if (!frameWindowStarted) return;
    source()?.endStatsWindow?.();
    frameWindowStarted = false;
  }

  function syncControls() {
    root.setAttribute("data-open", open ? "true" : "false");
    root.hidden = !open;
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

      // Redrawing must not blank the tooltip the pointer is currently showing. Reposition
      // it against the new geometry; clear only the other tiles.
      if (hover.name === name && hover.fraction !== null) {
        showTooltip(name, hover.fraction);
      } else {
        if (el[`tip-${name}`]) el[`tip-${name}`].textContent = "";
        svg?.querySelector(".xh")?.setAttribute("visibility", "hidden");
      }
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

    beginFrameWindow();
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
    endFrameWindow();
  }

  function startWindow() {
    stopWindow();
    windowStart = clock();
    samples = createRing(SAMPLE_RING_SIZE);
    lastMetrics = null;
    hover = { name: null, fraction: null };
    beginFrameWindow();
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
    const value = tab || Boolean(next);
    const changed = value !== open;

    // Hiding the card would drop focus inside it; hand it to the player's stats button.
    if (!value && root.contains(document.activeElement))
      player()?.querySelector('[data-sk="stats"]')?.focus();

    open = value;
    syncControls();
    // The player module owns its stats button; tell it the panel's current state.
    root.dispatchEvent(new CustomEvent("sk:stats-state", {
      detail: { open },
      bubbles: true,
    }));

    if (changed) {
      if (open) {
        startWindow();
        renderStream();
      } else {
        stopWindow();
      }
    }

    if (changed && persist) void persistOpen(open);
  }

  async function copy() {
    const metrics = source()?.read?.() ?? null;
    const snapshot = buildSnapshot({
      recordingId: options.recordingId ?? metrics?.recordingId,
      capturedAt: new Date().toISOString(),
      mode: metrics?.mode ?? (isLiveState(options.state) ? "live" : "vod"),
      playlistUrl: options.playlistUrl ?? metrics?.playlistUrl,
      level: metrics?.level ?? null,
      samples: samples.values(),
      errors: metrics?.errors ?? [],
      viewers: currentViewers() ? viewerSummary(currentViewers().samples) : null,
    });

    const written = await copyText(JSON.stringify(snapshot, null, 2));
    await notify(written ? "Stats copied" : "Could not copy stats");
  }

  // ---- Stream row: the viewer count at the playhead, from ViewersPanel's samples ----

  function currentViewers() {
    const data = viewersFor(options.recordingId);
    return data?.samples?.length ? data : null;
  }

  function renderStream() {
    const data = currentViewers();
    if (el["stats-stream"]) el["stats-stream"].hidden = !data;
    if (!data) return;

    const summary = viewerSummary(data.samples);
    if (el["stream-at"]) el["stream-at"].textContent = `Viewers at ${formatTimecode(playheadAt)}`;
    if (el["stream-viewers"])
      el["stream-viewers"].textContent =
        `${formatCount(viewerCountAt(data.samples, playheadAt))} · peak ${formatCount(summary.peak)}`;
  }

  function showTooltip(name, fraction) {
    const current = geometry[name];
    const svg = el[`spark-${name}`];
    const tip = el[`tip-${name}`];
    if (!current?.points?.length || !svg || !tip) return;

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
  }

  function bindHover(name) {
    const svg = el[`spark-${name}`];
    const tip = el[`tip-${name}`];
    if (!svg || !tip) return;

    on(svg, "pointermove", (event) => {
      if (!geometry[name]?.points?.length) return;

      const rect = svg.getBoundingClientRect();
      const fraction = rect.width > 0 ? (event.clientX - rect.left) / rect.width : 0;
      hover = { name, fraction: Math.min(1, Math.max(0, fraction)) };
      showTooltip(name, hover.fraction);
    });

    on(svg, "pointerleave", () => {
      if (hover.name === name) hover = { name: null, fraction: null };
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
      hover = { name: null, fraction: null };
      renderValues(null);
      renderSparklines();
      renderTables();
      playheadAt = 0;
      renderStream();
      if (open) startWindow();
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
    unsubscribe();
    listeners.abort();
  }

  on(el["stats-close"], "click", () => applyOpen(false, { persist: true }));
  on(el["copy-stats"], "click", () => void copy());

  const unsubscribe = subscribeViewers((data) => {
    if (data.recordingId === options.recordingId) renderStream();
  });

  // The player owns its stats button. It announces the new state immediately; this
  // listener updates the panel DOM and sampler before the .NET persistence round-trip.
  on(document, "sk:stats-open", (event) => {
    const next = event instanceof CustomEvent ? event.detail?.open : undefined;
    if (typeof next === "boolean") applyOpen(next);
  });

  for (const name of Object.keys(TILES)) bindHover(name);

  // The player announces its position on the document; only this recording's moves the
  // Stream row.
  on(document, "sk:playhead", (event) => {
    const detail = event instanceof CustomEvent ? event.detail : null;
    if (!detail || detail.recordingId !== options.recordingId || !Number.isFinite(detail.time))
      return;
    playheadAt = Math.max(0, detail.time);
    if (open) renderStream();
  });

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
