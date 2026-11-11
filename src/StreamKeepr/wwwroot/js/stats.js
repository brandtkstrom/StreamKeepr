// Stats for nerds sampling seam (player contract §10).
//
// The player owns hls.js and every dynamic player control. It creates one probe and
// registers it under the player root; StatsPanel.razor.js samples that probe on its own
// 2 s timer. The player never renders stats DOM, and the panel never re-renders the player.
//
// Everything below the DOM wiring is pure so Vitest can cover the ring, the sample shape,
// the snapshot and the native fallback without a browser or hls.js.

export const SAMPLE_INTERVAL_MS = 2000;
export const SAMPLE_RING_SIZE = 20;
export const ERROR_RING_SIZE = 10;
export const SNAPSHOT_VERSION = 1;

/** A fixed-size FIFO. Pushing past the capacity drops the oldest item. */
export function createRing(capacity) {
  if (!Number.isInteger(capacity) || capacity < 1)
    throw new RangeError("A ring needs a positive integer capacity.");

  const items = [];

  return {
    capacity,
    get size() {
      return items.length;
    },
    push(item) {
      items.push(item);
      if (items.length > capacity) items.shift();
    },
    clear() {
      items.length = 0;
    },
    values() {
      return items.slice();
    },
  };
}

const finiteOrNull = (value) => (Number.isFinite(value) ? value : null);
const positiveOrNull = (value) => {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : null;
};

function roundOrNull(value, decimals) {
  if (!Number.isFinite(value)) return null;
  const factor = 10 ** decimals;
  return Math.round(value * factor) / factor;
}

function integerOrNull(value) {
  return Number.isFinite(value) ? Math.round(value) : null;
}

function parseResolution(value) {
  if (typeof value !== "string") return { width: null, height: null };
  const match = /^(\d+)x(\d+)$/i.exec(value.trim());
  if (!match) return { width: null, height: null };
  return { width: Number(match[1]), height: Number(match[2]) };
}

function codecParts(attrs) {
  if (typeof attrs?.CODECS !== "string") return [];
  return attrs.CODECS.split(",").map((part) => part.trim()).filter(Boolean);
}

/** Projects one hls.js level, keeping only the fields the panel shows. */
export function readLevel(selected, index = 0, count = 1) {
  if (!selected) return null;

  const attrs = selected.attrs ?? {};
  const resolution = parseResolution(attrs.RESOLUTION);
  const codecs = codecParts(attrs);

  return {
    index: integerOrNull(index) ?? 0,
    count: Math.max(1, integerOrNull(count) ?? 1),
    width: positiveOrNull(selected.width) ?? positiveOrNull(resolution.width),
    height: positiveOrNull(selected.height) ?? positiveOrNull(resolution.height),
    frameRate: positiveOrNull(attrs["FRAME-RATE"]) ?? positiveOrNull(selected.frameRate),
    bitrate: positiveOrNull(selected.bitrate) ?? positiveOrNull(attrs.BANDWIDTH),
    videoCodec: selected.videoCodec ||
      codecs.find((codec) => /^(avc|hvc|hev|av01|vp0?9|vp8)/i.test(codec)) || null,
    audioCodec: selected.audioCodec ||
      codecs.find((codec) => /^(mp4a|ac-3|ec-3|opus|flac)/i.test(codec)) || null,
  };
}

/**
 * Estimates the presentation frame rate over the stats window from
 * requestVideoFrameCallback metadata. The level's FRAME-RATE attribute wins when
 * present; this is the fallback. The window resets when Stats opens.
 */
export function createFrameRateEstimator() {
  let first = null;
  let previous = null;
  let value = null;

  function reset() {
    first = null;
    previous = null;
    value = null;
  }

  return {
    get value() {
      return value;
    },
    sample(metadata) {
      if (!metadata ||
          !Number.isFinite(metadata.mediaTime) ||
          !Number.isFinite(metadata.presentedFrames)) {
        return value;
      }

      const current = {
        mediaTime: metadata.mediaTime,
        presentedFrames: metadata.presentedFrames,
      };

      // A seek or a counter reset starts a new measurement rather than a bogus rate.
      if (previous &&
          (current.mediaTime < previous.mediaTime ||
           current.presentedFrames < previous.presentedFrames)) {
        first = null;
      }

      if (!first) first = current;
      previous = current;

      const seconds = current.mediaTime - first.mediaTime;
      const frames = current.presentedFrames - first.presentedFrames;
      if (seconds > 0.001 && frames > 0) {
        const rate = frames / seconds;
        if (Number.isFinite(rate) && rate > 0 && rate < 241) value = rate;
      }

      return value;
    },
    reset,
  };
}

function bufferHealth(video) {
  if (!video || !Number.isFinite(video.currentTime)) return null;

  const ranges = video.buffered;
  if (!ranges || !Number.isFinite(ranges.length)) return null;

  let end = null;
  const current = video.currentTime;
  for (let index = 0; index < ranges.length; index++) {
    const start = ranges.start(index);
    const rangeEnd = ranges.end(index);
    if (start <= current + 0.5 && rangeEnd >= current - 0.5) {
      end = end === null ? rangeEnd : Math.max(end, rangeEnd);
    }
  }

  // No buffered range at the playhead is genuine zero health, not unknown.
  return end === null ? 0 : Math.max(0, end - current);
}

function playbackQuality(video) {
  const quality = video?.getVideoPlaybackQuality?.() ?? null;
  return {
    droppedFrames: integerOrNull(quality?.droppedVideoFrames),
    totalFrames: integerOrNull(quality?.totalVideoFrames),
  };
}

function isLive(view) {
  return view?.state === "recording" || view?.state === "reconnecting";
}

/**
 * One probe per player. The player calls attachHls/on* from its existing event handlers;
 * the panel calls read() when it samples. Counters keep accumulating while the panel is closed.
 */
export function createStatsProbe({ video, now = () => performance.now() } = {}) {
  let hls = null;
  let native = false;
  let view = null;
  let level = null;
  let levelIndex = 0;
  let segmentsLoaded = 0;
  let lastSegment = null;
  let measuredBitrate = null;
  let errorCount = 0;
  let startedAt = now();
  let disposed = false;
  let frameHandle = 0;
  let trackingFrames = false;
  let statsWindowActive = false;

  const errors = createRing(ERROR_RING_SIZE);
  const frameRate = createFrameRateEstimator();

  function refreshLevel() {
    if (!hls?.levels?.length) {
      level = null;
      return;
    }

    const selectedIndex = Number.isInteger(hls.currentLevel) && hls.currentLevel >= 0
      ? hls.currentLevel
      : levelIndex;
    const selected = hls.levels[selectedIndex] ?? hls.levels[0];
    level = readLevel(selected, selectedIndex, hls.levels.length);
  }

  function startFrameTracking() {
    if (trackingFrames || disposed || typeof video?.requestVideoFrameCallback !== "function")
      return;

    trackingFrames = true;
    const loop = (_now, metadata) => {
      if (!trackingFrames || disposed) return;
      frameRate.sample(metadata);
      frameHandle = video.requestVideoFrameCallback(loop);
    };
    frameHandle = video.requestVideoFrameCallback(loop);
  }

  function stopFrameTracking() {
    trackingFrames = false;
    if (frameHandle && typeof video?.cancelVideoFrameCallback === "function")
      video.cancelVideoFrameCallback(frameHandle);
    frameHandle = 0;
  }

  function reset() {
    startedAt = now();
    segmentsLoaded = 0;
    lastSegment = null;
    measuredBitrate = null;
    errorCount = 0;
    errors.clear();
    level = null;
    levelIndex = 0;
    frameRate.reset();
    stopFrameTracking();
    // A recording swap while the panel is open resets the window but keeps sampling.
    if (statsWindowActive) startFrameTracking();
  }

  function errorAt() {
    return Math.max(0, Math.round((now() - startedAt) / 1000));
  }

  function onError(data) {
    errorCount += 1;
    errors.push({
      at: errorAt(),
      type: String(data?.type ?? "error"),
      details: String(data?.details ?? ""),
      fatal: Boolean(data?.fatal),
    });
  }

  function onFragLoaded(data) {
    const frag = data?.frag;
    // hls.js puts the loader stats on the fragment; accept data.stats too for the fake
    // loader in Vitest and any future event shape.
    const stats = data?.stats ?? frag?.stats;

    // Init-segment loads are not media segments; keep the counters and bitrate estimate
    // to real fragments.
    if (frag?.sn === "initSegment") {
      refreshLevel();
      return;
    }

    segmentsLoaded += 1;

    const loading = stats?.loading;
    const loadMs = loading &&
      Number.isFinite(loading.start) &&
      Number.isFinite(loading.end)
      ? Math.max(0, loading.end - loading.start)
      : null;

    lastSegment = {
      index: integerOrNull(frag?.sn) ?? segmentsLoaded,
      durationSec: finiteOrNull(frag?.duration),
      loadMs,
    };

    if (Number.isFinite(stats?.total) &&
        stats.total > 0 &&
        Number.isFinite(frag?.duration) &&
        frag.duration > 0) {
      measuredBitrate = (stats.total * 8) / frag.duration;
    }

    refreshLevel();
  }

  function read() {
    refreshLevel();
    const quality = playbackQuality(video);

    return {
      mode: native ? "native" : isLive(view) ? "live" : "vod",
      engine: native ? "native" : "hls",
      recordingId: view?.recordingId ?? null,
      playlistUrl: view?.playlistUrl ?? null,
      level,
      viewport: {
        width: positiveOrNull(video?.videoWidth),
        height: positiveOrNull(video?.videoHeight),
      },
      bitrateBps: native ? null : level?.bitrate ?? measuredBitrate,
      frameRate: level?.frameRate ?? frameRate.value,
      bandwidthBps: native ? null : positiveOrNull(hls?.bandwidthEstimate),
      bufferSec: bufferHealth(video),
      droppedFrames: quality.droppedFrames,
      totalFrames: quality.totalFrames,
      lastSegment: native ? null : lastSegment,
      segmentsLoaded: native ? null : segmentsLoaded,
      errorCount: native ? 0 : errorCount,
      errors: native ? [] : errors.values(),
    };
  }

  return {
    read,
    reset,
    /** Starts the open-panel window: the rVFC estimate only runs while Stats is open. */
    beginStatsWindow() {
      if (disposed) return;
      statsWindowActive = true;
      frameRate.reset();
      stopFrameTracking();
      startFrameTracking();
    },
    /** Stops the rVFC loop when the panel collapses; counters keep accumulating. */
    endStatsWindow() {
      statsWindowActive = false;
      stopFrameTracking();
    },
    setView(next) {
      view = next ?? null;
    },
    attachHls(instance, Hls) {
      hls = instance;
      native = false;
      level = null;
      refreshLevel();

      const events = Hls?.Events ?? {};
      if (events.MANIFEST_PARSED)
        instance.on(events.MANIFEST_PARSED, () => refreshLevel());
      if (events.LEVEL_SWITCHED) {
        instance.on(events.LEVEL_SWITCHED, (_event, data) => {
          levelIndex = integerOrNull(data?.level) ?? 0;
          refreshLevel();
        });
      }
      if (events.FRAG_LOADED)
        instance.on(events.FRAG_LOADED, (_event, data) => onFragLoaded(data));
      if (events.ERROR)
        instance.on(events.ERROR, (_event, data) => onError(data));
    },
    detachEngine() {
      hls = null;
      level = null;
    },
    setNative(value) {
      native = Boolean(value);
      if (native) {
        hls = null;
        level = null;
      }
    },
    onFragLoaded,
    onError,
    onNativeError(error) {
      if (!native) return;
      errorCount += 1;
      errors.push({
        at: errorAt(),
        type: "mediaError",
        details: error?.message || (error?.code != null ? `code ${error.code}` : ""),
        fatal: true,
      });
    },
    dispose() {
      disposed = true;
      statsWindowActive = false;
      stopFrameTracking();
      hls = null;
      view = null;
      level = null;
    },
  };
}

// One probe per player root. WeakMap so a replaced player never leaks a source.
const sources = new WeakMap();

export function registerStatsSource(root, source) {
  if (!root || !source) return () => {};
  sources.set(root, source);
  return () => {
    if (sources.get(root) === source) sources.delete(root);
  };
}

export function unregisterStatsSource(root) {
  sources.delete(root);
}

export function statsSource(root) {
  return root ? sources.get(root) ?? null : null;
}

/** Turns one probe reading into the ring/snapshot sample shape. */
export function sampleFrom({ at = 0, probe = null } = {}) {
  return {
    at: Math.max(0, Math.round(at)),
    bitrateMbps: roundOrNull(
      Number.isFinite(probe?.bitrateBps) ? probe.bitrateBps / 1e6 : null, 1),
    frameRate: roundOrNull(probe?.frameRate, 2),
    bandwidthMbps: roundOrNull(
      Number.isFinite(probe?.bandwidthBps) ? probe.bandwidthBps / 1e6 : null, 1),
    bufferSec: roundOrNull(probe?.bufferSec, 1),
    droppedFrames: integerOrNull(probe?.droppedFrames),
  };
}

/** Builds the versioned copy-stats snapshot. Unavailable values stay null. */
export function buildSnapshot({
  recordingId = null,
  capturedAt = null,
  mode = "vod",
  playlistUrl = null,
  level = null,
  samples = [],
  errors = [],
} = {}) {
  return {
    v: SNAPSHOT_VERSION,
    recordingId: recordingId ?? null,
    capturedAt: capturedAt ?? new Date().toISOString(),
    mode,
    playlistUrl: playlistUrl ?? null,
    level: level
      ? {
          index: integerOrNull(level.index),
          width: integerOrNull(level.width),
          height: integerOrNull(level.height),
          frameRate: roundOrNull(level.frameRate, 2),
          bitrate: integerOrNull(level.bitrate),
          videoCodec: level.videoCodec ?? null,
          audioCodec: level.audioCodec ?? null,
        }
      : null,
    samples: samples.slice(-SAMPLE_RING_SIZE).map((sample) => ({
      at: integerOrNull(sample.at) ?? 0,
      bitrateMbps: finiteOrNull(sample.bitrateMbps),
      frameRate: finiteOrNull(sample.frameRate),
      bandwidthMbps: finiteOrNull(sample.bandwidthMbps),
      bufferSec: finiteOrNull(sample.bufferSec),
      droppedFrames: integerOrNull(sample.droppedFrames),
    })),
    errors: errors.slice(-ERROR_RING_SIZE).map((error) => ({
      at: integerOrNull(error.at) ?? 0,
      type: String(error.type ?? "error"),
      details: String(error.details ?? ""),
      fatal: Boolean(error.fatal),
    })),
  };
}

const countFormat = new Intl.NumberFormat("en-US");

function formatCount(value) {
  return Number.isFinite(value) ? countFormat.format(value) : "—";
}

export function formatHint(engine, level) {
  if (engine === "native") return "Native HLS";
  if (!engine) return "—";

  const count = level?.count ?? 1;
  if (count > 1) return `hls.js · level ${level?.index ?? 0} / ${count}`;
  return "hls.js · source";
}

export function formatResolution(level, viewport) {
  const encoded = level?.width && level?.height
    ? `${level.width}×${level.height}`
    : null;
  const seen = viewport?.width && viewport?.height
    ? `${viewport.width}×${viewport.height}`
    : null;

  if (encoded && seen && encoded !== seen) return `${encoded} → ${seen} viewport`;
  if (encoded) return encoded;
  if (seen) return seen;
  return "—";
}

export function formatCodecs(level) {
  return [level?.videoCodec, level?.audioCodec].filter(Boolean).join(" · ") || "—";
}

export function formatDroppedFrames(dropped, total) {
  if (!Number.isFinite(dropped)) return "—";
  if (!Number.isFinite(total)) return formatCount(dropped);
  return `${formatCount(dropped)} of ${formatCount(total)}`;
}

export function formatSegments(count) {
  return Number.isFinite(count) ? formatCount(count) : "—";
}

export function formatLastSegment(segment) {
  if (!segment) return "—";

  const index = Number.isFinite(segment.index) ? `#${formatCount(segment.index)}` : "—";
  const duration = Number.isFinite(segment.durationSec)
    ? `${segment.durationSec.toFixed(2)} s`
    : "—";
  const load = Number.isFinite(segment.loadMs) ? `${Math.round(segment.loadMs)} ms` : "—";
  return `${index} · ${duration} · ${load}`;
}

export function formatErrors(count, last = null) {
  if (!Number.isFinite(count) || count <= 0) return "None";

  const label = count === 1 ? "1 error" : `${formatCount(count)} errors`;
  const detail = last
    ? [last.type, last.details].filter(Boolean).join(" · ")
    : "";
  return detail ? `${label} · ${detail}` : label;
}

/** The displayed value keeps the warning glyph in text, never colour alone. */
export function formatErrorValue(count, last = null) {
  const text = formatErrors(count, last);
  return Number.isFinite(count) && count > 0 ? `⚠ ${text}` : text;
}

/** Clipboard write with no legacy fallback. Returns the success flag the snackbar reports. */
export async function copyText(text, clipboard = globalThis.navigator?.clipboard) {
  if (!clipboard || typeof clipboard.writeText !== "function") return false;

  try {
    await clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
