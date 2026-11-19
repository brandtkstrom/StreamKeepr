import {
  buildSnapshot,
  copyText,
  formatCount,
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
import { formatTimecode } from "/js/timecode.js";
import {
  drawViewerChart,
  formatViewerTip,
  placeViewerPlayhead,
  viewerChartGeometry,
  viewerChartLabel,
  viewerChartPointAt,
  viewerChartTimeAt,
  viewerCountAt,
  viewerSummary,
} from "/js/viewer-chart.js";

// Mirrors the data-sk hooks StatsPanel.razor renders (StatsPanelTests guards the markup).
const HOOKS = [
  "stats-toggle", "stats-hint", "stats-body", "copy-stats",
  "value-bitrate", "spark-bitrate", "tip-bitrate", "table-bitrate", "rows-bitrate",
  "value-frame", "spark-frame", "tip-frame", "table-frame", "rows-frame",
  "value-bandwidth", "spark-bandwidth", "tip-bandwidth", "table-bandwidth", "rows-bandwidth",
  "value-buffer", "spark-buffer", "tip-buffer", "table-buffer", "rows-buffer",
  "kv-resolution", "kv-codecs", "kv-dropped", "kv-decoder",
  "kv-last-segment", "kv-segments", "kv-errors", "kv-playlist",
  "viewer-chart-empty", "viewer-tile", "viewer-at", "viewer-count",
  "viewer-now", "viewer-peak", "viewer-average",
  "viewer-chart", "viewer-svg", "tip-viewers", "table-viewers", "rows-viewers",
];

// A live recording's viewer samples are refetched this often while the panel is open.
export const VIEWER_REFRESH_MS = 60_000;

const NO_VIEWERS = Object.freeze({ recordingId: null, durationSec: 0, samples: [], loaded: false });

function viewersUrl(recordingId) {
  return `/api/recordings/${encodeURIComponent(recordingId)}/viewers`;
}

/** Keeps well-formed samples only; the endpoint already orders them oldest first. */
function readSamples(body) {
  const list = Array.isArray(body?.samples) ? body.samples : [];
  return list
    .filter((sample) => Number.isFinite(sample?.at) && Number.isFinite(sample?.count))
    .map((sample) => ({ at: sample.at, count: sample.count }));
}

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

export function createStatsPanel(root, {
  dotNet = null,
  interval = SAMPLE_INTERVAL_MS,
  clock = () => performance.now(),
  fetch = globalThis.fetch?.bind(globalThis),
  viewerRefreshMs = VIEWER_REFRESH_MS,
} = {}) {
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
  let frameWindowStarted = false;
  let hover = { name: null, fraction: null };
  let viewers = NO_VIEWERS;
  let viewerGeometry = null;
  let viewerRequest = null;
  let viewerSequence = 0;
  let viewerTimer = 0;
  let playheadAt = 0;
  let dragging = false;
  let viewerHover = null;
  let seekInFlight = false;
  let pendingSeek = null;

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
    el["stats-toggle"]?.setAttribute("aria-checked", open ? "true" : "false");

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
    const value = Boolean(next);
    const changed = value !== open;

    // inert would blur whatever is inside on some browsers, so move focus deliberately first.
    if (!value && el["stats-body"]?.contains(document.activeElement))
      el["stats-toggle"]?.focus();

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
        startViewers();
      } else {
        stopWindow();
        stopViewers();
      }
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
      viewers: viewers.recordingId === options.recordingId ? viewerSummary(viewers.samples) : null,
    });

    const written = await copyText(JSON.stringify(snapshot, null, 2));
    await notify(written ? "Stats copied" : "Could not copy stats");
  }

  // ---- Stream section: viewer tile and chart ----

  function setHidden(node, hidden) {
    if (node) node.hidden = hidden;
  }

  function renderViewerTable(samples) {
    const body = el["rows-viewers"];
    if (!body) return;
    body.replaceChildren(...samples.map((sample) => {
      const row = document.createElement("tr");
      const time = document.createElement("th");
      time.scope = "row";
      time.textContent = formatTimecode(sample.at);
      const value = document.createElement("td");
      value.textContent = formatCount(sample.count);
      row.append(time, value);
      return row;
    }));
  }

  function renderPlayhead() {
    if (!viewerGeometry) return;
    const count = viewerCountAt(viewers.samples, playheadAt);
    if (el["viewer-at"]) el["viewer-at"].textContent = `Viewers at ${formatTimecode(playheadAt)}`;
    if (el["viewer-count"]) el["viewer-count"].textContent = formatCount(count);
    placeViewerPlayhead(el["viewer-chart"], viewerGeometry, playheadAt, count);
  }

  function renderViewers() {
    const { samples } = viewers;
    const has = samples.length > 0;
    const live = isLive(options.state);

    setHidden(el["viewer-chart-empty"], has);
    setHidden(el["viewer-tile"], !has);
    setHidden(el["viewer-chart"], !has);
    setHidden(el["table-viewers"], !has);
    el["viewer-chart"]?.setAttribute("aria-label", viewerChartLabel(samples, { live }));
    renderViewerTable(samples);

    if (!has) {
      viewerGeometry = null;
      el["viewer-svg"]?.replaceChildren();
      el["viewer-chart"]?.querySelector("[data-chart-overlay]")?.remove();
      if (el["tip-viewers"]) el["tip-viewers"].textContent = "";
      return;
    }

    viewerGeometry = drawViewerChart(
      el["viewer-chart"],
      el["viewer-svg"],
      viewerChartGeometry(samples, { durationSec: viewers.durationSec }),
      { live });
    // A refresh must not leave a stale tooltip: reposition the active one against the new data.
    if (viewerHover !== null) showViewerTip(viewerHover);
    else hideViewerTip();

    const summary = viewerSummary(samples);
    if (el["viewer-now"]) el["viewer-now"].textContent = formatCount(summary.now);
    if (el["viewer-peak"])
      el["viewer-peak"].textContent =
        `${formatCount(summary.peak)} at ${formatTimecode(summary.peakAt)}`;
    if (el["viewer-average"]) el["viewer-average"].textContent = formatCount(summary.average);
    renderPlayhead();
  }

  async function loadViewers() {
    const recordingId = options.recordingId;
    if (disposed || !recordingId || typeof fetch !== "function") return;

    viewerRequest?.abort();
    const request = new AbortController();
    viewerRequest = request;
    const sequence = ++viewerSequence;
    // Only the latest request for the current recording may change the section.
    const current = () =>
      !disposed && !request.signal.aborted && sequence === viewerSequence &&
      recordingId === options.recordingId;

    try {
      const response = await fetch(viewersUrl(recordingId), {
        signal: request.signal,
        cache: "no-store",
        headers: { Accept: "application/json" },
      });
      if (!current()) return;

      // An unknown recording has no samples; any other failure keeps what is shown.
      let body = { recordingId, durationSec: 0, samples: [] };
      if (response.ok) body = await response.json();
      else if (response.status !== 404)
        throw new Error(`Viewer samples returned HTTP ${response.status}.`);

      if (!current() || body?.recordingId !== recordingId) return;
      viewers = {
        recordingId,
        durationSec: Number.isFinite(body.durationSec) ? body.durationSec : 0,
        samples: readSamples(body),
        loaded: true,
      };
      renderViewers();
    } catch (error) {
      if (request.signal.aborted) return;
      console.warn("Stream Keepr: viewer samples could not be loaded.", error);
    } finally {
      if (viewerRequest === request) viewerRequest = null;
    }
  }

  function stopViewers() {
    clearInterval(viewerTimer);
    viewerTimer = 0;
    viewerRequest?.abort();
    viewerRequest = null;
  }

  /** Opening fetches once per recording; a live recording also refetches on reopen and every 60 s. */
  function startViewers() {
    stopViewers();
    if (!open || disposed) return;

    const live = isLive(options.state);
    if (live || !viewers.loaded || viewers.recordingId !== options.recordingId)
      void loadViewers();
    if (live) viewerTimer = setInterval(() => void loadViewers(), viewerRefreshMs);
  }

  function chartFraction(event) {
    const rect = el["viewer-chart"]?.getBoundingClientRect();
    if (!rect || !(rect.width > 0)) return 0;
    return Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
  }

  function showViewerTip(fraction) {
    const tip = el["tip-viewers"];
    const point = viewerChartPointAt(viewerGeometry, fraction);
    if (!tip || !point) return;

    tip.textContent = formatViewerTip(point.at, point.count);
    tip.style.left = `${(point.x / viewerGeometry.width) * 100}%`;
    const crosshair = el["viewer-svg"]?.querySelector(".xh");
    if (crosshair) {
      crosshair.setAttribute("x1", String(point.x));
      crosshair.setAttribute("x2", String(point.x));
      crosshair.setAttribute("visibility", "visible");
    }
  }

  function hideViewerTip() {
    if (el["tip-viewers"]) el["tip-viewers"].textContent = "";
    el["viewer-svg"]?.querySelector(".xh")?.setAttribute("visibility", "hidden");
  }

  // Seeks go through .NET to the PlayerBridge. A drag sends at most one call at a time and
  // keeps only the latest position meanwhile, so the circuit is never flooded.
  async function sendSeek(seconds) {
    if (seekInFlight) {
      pendingSeek = seconds;
      return;
    }

    seekInFlight = true;
    try {
      await dotNet?.invokeMethodAsync("Seek", seconds);
    } catch (error) {
      console.debug("Stream Keepr: the chart seek was not delivered.", error);
    } finally {
      seekInFlight = false;
    }

    if (pendingSeek !== null && !disposed) {
      const next = pendingSeek;
      pendingSeek = null;
      await sendSeek(next);
    }
  }

  function seekToPointer(event) {
    if (!viewerGeometry) return;
    void sendSeek(viewerChartTimeAt(viewerGeometry, chartFraction(event)));
  }

  function bindViewerChart() {
    const chart = el["viewer-chart"];
    if (!chart) return;

    const hover = (event) => {
      viewerHover = chartFraction(event);
      showViewerTip(viewerHover);
    };

    on(chart, "pointerdown", (event) => {
      if (event.button !== 0 || !viewerGeometry) return;
      dragging = true;
      // data-active shows the tooltip where there is no :hover (touch).
      chart.setAttribute("data-active", "");
      if (event.pointerId !== undefined) chart.setPointerCapture?.(event.pointerId);
      hover(event);
      seekToPointer(event);
    });
    on(chart, "pointermove", (event) => {
      if (!viewerGeometry) return;
      hover(event);
      if (dragging) seekToPointer(event);
    });
    const endDrag = () => {
      dragging = false;
      chart.removeAttribute("data-active");
    };
    on(chart, "pointerup", endDrag);
    on(chart, "pointercancel", endDrag);
    on(chart, "pointerleave", () => {
      endDrag();
      viewerHover = null;
      hideViewerTip();
    });
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
      stopViewers();
      viewers = NO_VIEWERS;
      playheadAt = 0;
      renderViewers();
      if (open) {
        startWindow();
        startViewers();
      }
      return;
    }

    renderValues(lastMetrics);

    // A capture that has just finished needs one last fetch for its final samples and duration.
    const wasLive = isLive(previous.state);
    if (wasLive !== isLive(options.state)) {
      if (wasLive) viewers = { ...viewers, loaded: false };
      startViewers();
    }
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
    stopViewers();
    listeners.abort();
  }

  on(el["stats-toggle"], "click", () => applyOpen(!open, { persist: true }));
  on(el["copy-stats"], "click", () => void copy());

  // The player owns its stats button. It announces the new state immediately; this
  // listener updates the panel DOM and sampler before the .NET persistence round-trip.
  on(document, "sk:stats-open", (event) => {
    const next = event instanceof CustomEvent ? event.detail?.open : undefined;
    if (typeof next === "boolean") applyOpen(next);
  });

  for (const name of Object.keys(TILES)) bindHover(name);
  bindViewerChart();

  // The player announces its position on the document; only this recording's moves the
  // tile and the chart's playhead.
  on(document, "sk:playhead", (event) => {
    const detail = event instanceof CustomEvent ? event.detail : null;
    if (!detail || detail.recordingId !== options.recordingId || !Number.isFinite(detail.time))
      return;
    playheadAt = Math.max(0, detail.time);
    renderPlayhead();
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
