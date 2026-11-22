import { formatCount } from "/js/stats.js";
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
import { publishViewers } from "/js/viewer-store.js";
import { isLiveState } from "/js/recording-state.js";

// The Viewers panel's chart island (design system: Viewers; the chart itself is #211). It loads
// the samples once, or every 60 s while the recording is live, draws the tile and chart, writes
// the panel header's hint, and publishes the samples for the Statistics overlay.

// Mirrors the data-sk hooks ViewerChart.razor renders (ViewersPanelTests guards the markup).
const HOOKS = [
  "viewer-tile", "viewer-at", "viewer-count", "viewer-now", "viewer-peak", "viewer-average",
  "viewer-chart", "viewer-svg", "tip-viewers", "table-viewers", "rows-viewers",
];

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

/** "1,489 at 54:40 · peak 1,658": the panel header's hint. */
export function formatViewersHint(samples, at) {
  if (!samples?.length) return "";
  const summary = viewerSummary(samples);
  return `${formatCount(viewerCountAt(samples, at))} at ${formatTimecode(at)} · peak ${formatCount(summary.peak)}`;
}

export function createViewerChart(root, {
  dotNet = null,
  fetch = globalThis.fetch?.bind(globalThis),
  refreshMs = VIEWER_REFRESH_MS,
} = {}) {
  const el = Object.fromEntries(HOOKS.map((name) => [name, root.querySelector(`[data-sk="${name}"]`)]));
  const hint = () => root.closest("[data-viewers-panel]")?.querySelector("[data-viewers-hint]") ?? null;
  const listeners = new AbortController();
  const on = (target, type, handler) =>
    target?.addEventListener(type, handler, { signal: listeners.signal });

  let options = {};
  let disposed = false;
  let viewers = NO_VIEWERS;
  let geometry = null;
  let request = null;
  let sequence = 0;
  let timer = 0;
  let playheadAt = 0;
  let dragging = false;
  let hoverAt = null;
  let seekInFlight = false;
  let pendingSeek = null;
  let reported = null;

  async function reportHasSamples(has) {
    if (reported === has) return;
    reported = has;
    try {
      await dotNet?.invokeMethodAsync("SetHasSamples", has);
    } catch (error) {
      console.debug("Stream Keepr: the viewer panel state was not delivered.", error);
    }
  }

  function renderTable(samples) {
    el["rows-viewers"]?.replaceChildren(...samples.map((sample) => {
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
    const node = hint();
    if (node) node.textContent = formatViewersHint(viewers.samples, playheadAt);
    if (!geometry) return;
    const count = viewerCountAt(viewers.samples, playheadAt);
    if (el["viewer-at"]) el["viewer-at"].textContent = `Viewers at ${formatTimecode(playheadAt)}`;
    if (el["viewer-count"]) el["viewer-count"].textContent = formatCount(count);
    placeViewerPlayhead(el["viewer-chart"], geometry, playheadAt, count);
  }

  function showTip(fraction) {
    const tip = el["tip-viewers"];
    const point = viewerChartPointAt(geometry, fraction);
    if (!tip || !point) return;

    tip.textContent = formatViewerTip(point.at, point.count);
    tip.style.left = `${(point.x / geometry.width) * 100}%`;
    const crosshair = el["viewer-svg"]?.querySelector(".xh");
    if (crosshair) {
      crosshair.setAttribute("x1", String(point.x));
      crosshair.setAttribute("x2", String(point.x));
      crosshair.setAttribute("visibility", "visible");
    }
  }

  function hideTip() {
    if (el["tip-viewers"]) el["tip-viewers"].textContent = "";
    el["viewer-svg"]?.querySelector(".xh")?.setAttribute("visibility", "hidden");
  }

  function render() {
    const { samples } = viewers;
    const has = samples.length > 0;
    const live = isLiveState(options.state);

    el["viewer-chart"]?.setAttribute("aria-label", viewerChartLabel(samples, { live }));
    renderTable(samples);

    if (!has) {
      geometry = null;
      el["viewer-svg"]?.replaceChildren();
      el["viewer-chart"]?.querySelector("[data-chart-overlay]")?.remove();
      hideTip();
      const node = hint();
      if (node) node.textContent = "";
    } else {
      geometry = drawViewerChart(
        el["viewer-chart"],
        el["viewer-svg"],
        viewerChartGeometry(samples, { durationSec: viewers.durationSec }),
        { live });
      // A refresh must not leave a stale tooltip: reposition the active one against the new data.
      if (hoverAt !== null) showTip(hoverAt);
      else hideTip();

      const summary = viewerSummary(samples);
      if (el["viewer-now"]) el["viewer-now"].textContent = formatCount(summary.now);
      if (el["viewer-peak"])
        el["viewer-peak"].textContent = `${formatCount(summary.peak)} at ${formatTimecode(summary.peakAt)}`;
      if (el["viewer-average"]) el["viewer-average"].textContent = formatCount(summary.average);
      renderPlayhead();
    }

    void reportHasSamples(has);
  }

  async function load() {
    const recordingId = options.recordingId;
    if (disposed || !recordingId || typeof fetch !== "function") return;

    request?.abort();
    const current = new AbortController();
    request = current;
    const mine = ++sequence;
    // Only the latest request for the current recording may change the chart.
    const latest = () =>
      !disposed && !current.signal.aborted && mine === sequence && recordingId === options.recordingId;

    try {
      const response = await fetch(viewersUrl(recordingId), {
        signal: current.signal,
        cache: "no-store",
        headers: { Accept: "application/json" },
      });
      if (!latest()) return;

      // An unknown recording has no samples; any other failure keeps what is shown.
      let body = { recordingId, durationSec: 0, samples: [] };
      if (response.ok) body = await response.json();
      else if (response.status !== 404)
        throw new Error(`Viewer samples returned HTTP ${response.status}.`);

      if (!latest() || body?.recordingId !== recordingId) return;
      viewers = {
        recordingId,
        durationSec: Number.isFinite(body.durationSec) ? body.durationSec : 0,
        samples: readSamples(body),
        loaded: true,
      };
      render();
      publishViewers({ recordingId, durationSec: viewers.durationSec, samples: viewers.samples });
    } catch (error) {
      if (current.signal.aborted) return;
      console.warn("Stream Keepr: viewer samples could not be loaded.", error);
    } finally {
      if (request === current) request = null;
    }
  }

  function stop() {
    clearInterval(timer);
    timer = 0;
    request?.abort();
    request = null;
  }

  /** Loads once per recording; a live recording also refetches every 60 s. */
  function start() {
    stop();
    if (disposed) return;
    if (isLiveState(options.state) || !viewers.loaded || viewers.recordingId !== options.recordingId)
      void load();
    if (isLiveState(options.state)) timer = setInterval(() => void load(), refreshMs);
  }

  function fraction(event) {
    const rect = el["viewer-chart"]?.getBoundingClientRect();
    if (!rect || !(rect.width > 0)) return 0;
    return Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
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
    if (!geometry) return;
    void sendSeek(viewerChartTimeAt(geometry, fraction(event)));
  }

  const chart = el["viewer-chart"];
  const hover = (event) => {
    hoverAt = fraction(event);
    showTip(hoverAt);
  };
  on(chart, "pointerdown", (event) => {
    if (event.button !== 0 || !geometry) return;
    dragging = true;
    // data-active shows the tooltip where there is no :hover (touch).
    chart.setAttribute("data-active", "");
    if (event.pointerId !== undefined) chart.setPointerCapture?.(event.pointerId);
    hover(event);
    seekToPointer(event);
  });
  on(chart, "pointermove", (event) => {
    if (!geometry) return;
    hover(event);
    if (dragging) seekToPointer(event);
  });
  const endDrag = () => {
    dragging = false;
    chart?.removeAttribute("data-active");
  };
  on(chart, "pointerup", endDrag);
  on(chart, "pointercancel", endDrag);
  on(chart, "pointerleave", () => {
    endDrag();
    hoverAt = null;
    hideTip();
  });

  // The player announces its position on the document; only this recording's moves the playhead.
  on(document, "sk:playhead", (event) => {
    const detail = event instanceof CustomEvent ? event.detail : null;
    if (!detail || detail.recordingId !== options.recordingId || !Number.isFinite(detail.time)) return;
    playheadAt = Math.max(0, detail.time);
    renderPlayhead();
  });

  function init(next) {
    options = next ?? {};
    render();
    start();
  }

  function update(next) {
    if (disposed) return;
    const previous = options;
    options = { ...options, ...next };

    if (previous.recordingId !== options.recordingId) {
      stop();
      viewers = NO_VIEWERS;
      playheadAt = 0;
      hoverAt = null;
      render();
      start();
      return;
    }

    // A capture that has just finished needs one last fetch for its final samples and duration.
    const wasLive = isLiveState(previous.state);
    if (wasLive !== isLiveState(options.state)) {
      if (wasLive) viewers = { ...viewers, loaded: false };
      start();
    }
  }

  function dispose() {
    if (disposed) return;
    disposed = true;
    stop();
    listeners.abort();
  }

  return { init, update, dispose };
}

const charts = new WeakMap();

export function init(root, dotNet, options) {
  if (!root) return;
  charts.get(root)?.dispose();
  const chart = createViewerChart(root, { dotNet });
  charts.set(root, chart);
  chart.init(options);
}

export function update(root, options) {
  charts.get(root)?.update(options);
}

export function dispose(root) {
  charts.get(root)?.dispose();
  charts.delete(root);
}
