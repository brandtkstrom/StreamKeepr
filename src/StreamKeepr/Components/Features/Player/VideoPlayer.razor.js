// Recording player (player contract §§2–5, 13). VideoPlayer.razor renders the skeleton once;
// this module owns hls.js, every control, and all dynamic player DOM. hls.js is imported lazily
// from the vendored copy, only on recording pages, through an injectable loader for tests.

// Defaults plus the contract's overrides. The stitched playlist targets 12 s segments, so one
// target duration behind the end is the live edge.
export const HLS_CONFIG = Object.freeze({
  enableWorker: true,
  lowLatencyMode: false,
  backBufferLength: 90,
  liveSyncDurationCount: 1,
  liveMaxLatencyDurationCount: Infinity,
  maxLiveSyncPlaybackRate: 1,
});

const NETWORK_RETRY_MS = [1000, 2000, 4000];
const MAX_MEDIA_RECOVERIES = 3; // recover, recover, then swap the audio codec
const SKIP_SECONDS = 10;
const SEEK_STEP_SECONDS = 5;
const VOLUME_STEP = 0.05;
const SPINNER_DELAY_MS = 500;
const NATIVE_HLS = "application/vnd.apple.mpegurl";

const UNSUPPORTED_MESSAGE = "This browser can't play HLS video. Try a current version of Chrome, Edge, Firefox, or Safari.";
const FAILED_MESSAGE = "The video could not be played.";

const CHIPS = {
  recording: '<span class="sk-status rec"><span class="dot" aria-hidden="true"></span>Rec</span>',
  reconnecting:
    '<span class="sk-status partial"><span class="sk-icon" aria-hidden="true">sync_problem</span>Reconnecting</span>',
  partial:
    '<span class="sk-status partial"><span class="sk-icon" aria-hidden="true">warning</span>Partial</span>',
  saved: "",
};

/** The hooks VideoPlayer.razor must render (VideoPlayerTests checks the markup). */
export const HOOKS = [
  "video", "title", "streamer", "chip", "center", "spinner", "overlay", "overlay-message",
  "retry", "seek", "buffered", "played", "ticks", "knob", "hover-time", "play", "back",
  "forward", "mute", "volume", "volume-level", "current", "duration", "speed", "speed-menu",
];

export function formatTimecode(seconds) {
  const total = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds) : 0;
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = String(total % 60).padStart(2, "0");
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${secs}` : `${minutes}:${secs}`;
}

export async function defaultLoadHls() {
  const url = new URL("lib/hls.js/hls.mjs", document.baseURI).href;
  return (await import(/* @vite-ignore */ url)).default;
}

const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
const percent = (ratio) => `${Math.round(clamp(ratio, 0, 1) * 100000) / 1000}%`;
const number = (value) => String(Math.round(value * 1000) / 1000);

function hasMediaSource() {
  return typeof window.MediaSource === "function" || typeof window.ManagedMediaSource === "function";
}

export function createPlayer(root, { loadHls = defaultLoadHls } = {}) {
  const el = Object.fromEntries(HOOKS.map((name) => [name, root.querySelector(`[data-sk="${name}"]`)]));
  const video = el.video;
  const listeners = new AbortController();
  const on = (target, type, handler) =>
    target?.addEventListener(type, handler, { signal: listeners.signal });

  let view = null;
  let hls = null;
  let generation = 0;
  let disposed = false;
  let native = false;
  let startAt = null;
  let lastKnownTime = 0;
  let networkAttempts = 0;
  let mediaAttempts = 0;
  let retryTimer = 0;
  let spinnerTimer = 0;
  let dragging = false;
  let chapters = [];
  let hlsClass = null; // imported once per player, on the first load that needs it

  // ----- helpers -----

  const duration = () => (Number.isFinite(video.duration) && video.duration > 0 ? video.duration : NaN);
  const hasMetadata = () => video.readyState >= 1 && Number.isFinite(duration());

  function setSpinner(visible) {
    if (el.spinner) el.spinner.hidden = !visible;
  }

  function setBusy(busy) {
    if (busy) root.setAttribute("aria-busy", "true");
    else root.removeAttribute("aria-busy");
  }

  function showOverlay(message, canRetry) {
    setSpinner(false);
    el["overlay-message"].textContent = message;
    el.retry.hidden = !canRetry;
    el.overlay.hidden = false;
    (canRetry ? el.retry : el.overlay.querySelector("a"))?.focus();
  }

  function hideOverlay() {
    el.overlay.hidden = true;
  }

  function validChapters(list, total) {
    if (!Array.isArray(list)) return [];
    return list
      .filter((c) => c && Number.isFinite(c.startSeconds) && c.startSeconds >= 0)
      .filter((c) => !Number.isFinite(total) || c.startSeconds < total)
      .sort((a, b) => a.startSeconds - b.startSeconds);
  }

  function chapterAt(time) {
    let found = null;
    for (const chapter of chapters) {
      if (chapter.startSeconds <= time) found = chapter;
      else break;
    }
    return found;
  }

  function renderTicks() {
    const total = duration();
    chapters = validChapters(view?.chapters, total);
    el.ticks.replaceChildren();
    if (!Number.isFinite(total)) return;
    for (const chapter of chapters) {
      if (chapter.startSeconds <= 0) continue;
      const tick = document.createElement("span");
      tick.className = "tick";
      tick.style.left = percent(chapter.startSeconds / total);
      el.ticks.append(tick);
    }
  }

  function renderPosition(time = video.currentTime) {
    const total = duration();
    const ready = hasMetadata();
    const ratio = ready ? time / total : 0;

    el.played.style.width = percent(ratio);
    el.knob.style.left = percent(ratio);
    el.current.textContent = formatTimecode(time);
    el.duration.textContent = formatTimecode(ready ? total : view?.durationSec);

    el.seek.setAttribute("aria-disabled", ready ? "false" : "true");
    el.seek.setAttribute("aria-valuemax", number(ready ? total : 0));
    el.seek.setAttribute("aria-valuenow", number(ready ? clamp(time, 0, total) : 0));
    el.seek.setAttribute(
      "aria-valuetext",
      `${formatTimecode(time)} of ${formatTimecode(ready ? total : view?.durationSec)}`,
    );
    el.back.disabled = !ready;
    el.forward.disabled = !ready;
  }

  function renderBuffered() {
    const total = duration();
    const ranges = video.buffered;
    let end = 0;
    for (let i = 0; i < (ranges?.length ?? 0); i++) {
      if (ranges.start(i) <= video.currentTime + 0.5 && ranges.end(i) >= video.currentTime) {
        end = ranges.end(i);
      }
    }
    el.buffered.style.width = Number.isFinite(total) ? percent(end / total) : "0%";
  }

  function renderPlayState() {
    const playing = !video.paused && !video.ended;
    el.play.setAttribute("aria-label", playing ? "Pause" : "Play");
    el.play.querySelector(".sk-icon").textContent = playing ? "pause" : "play_arrow";
    el.center.hidden = playing;
    el.center.setAttribute("aria-label", video.ended ? "Replay" : "Play");
    el.center.querySelector(".sk-icon").textContent = video.ended ? "replay" : "play_arrow";
  }

  function renderVolume() {
    const level = video.muted ? 0 : video.volume;
    const value = Math.round(video.volume * 100);
    el["volume-level"].style.width = percent(level);
    el.volume.setAttribute("aria-valuenow", String(value));
    el.volume.setAttribute("aria-valuetext", video.muted ? `${value}%, muted` : `${value}%`);
    const silent = video.muted || video.volume === 0;
    el.mute.setAttribute("aria-label", video.muted ? "Unmute" : "Mute");
    el.mute.querySelector(".sk-icon").textContent = silent ? "volume_off" : "volume_up";
  }

  function renderSpeed() {
    const rate = video.playbackRate || 1;
    el.speed.textContent = `${rate}×`;
    el.speed.setAttribute("aria-label", `Playback speed: ${rate}×`);
    for (const item of el["speed-menu"].querySelectorAll("[data-rate]")) {
      const checked = Number(item.dataset.rate) === rate;
      item.setAttribute("aria-checked", checked ? "true" : "false");
      const icon = item.querySelector(".sk-icon");
      if (icon) icon.textContent = checked ? "check" : "";
    }
  }

  function renderView() {
    el.title.textContent = view.title ?? "";
    el.streamer.textContent = view.streamerLogin ?? "";
    el.chip.innerHTML = CHIPS[view.state] ?? "";
    video.setAttribute("aria-label", view.title ?? "Recording");
    if (view.thumbnailUrl) video.setAttribute("poster", view.thumbnailUrl);
    else video.removeAttribute("poster");
    renderTicks();
  }

  function seekTo(time) {
    const total = duration();
    if (!Number.isFinite(total)) return;
    video.currentTime = clamp(time, 0, total);
    renderPosition();
  }

  function setVolume(value) {
    video.volume = clamp(Math.round(value * 100) / 100, 0, 1);
    if (video.volume > 0) video.muted = false;
    renderVolume();
  }

  // ----- engine -----

  function teardownEngine() {
    clearTimeout(retryTimer);
    clearTimeout(spinnerTimer);
    retryTimer = 0;
    hls?.destroy();
    hls = null;
    if (native || video.hasAttribute("src")) {
      video.removeAttribute("src");
      video.load?.();
    }
    native = false;
  }

  function resetUi() {
    hideOverlay();
    setSpinner(false);
    setBusy(true);
    closeSpeedMenu(false);
    dragging = false;
    el.seek.classList.remove("drag");
    lastKnownTime = 0;
    networkAttempts = 0;
    mediaAttempts = 0;
    el.buffered.style.width = "0%";
  }

  function onHlsError(Hls, data) {
    if (!data?.fatal) return;

    if (data.type === Hls.ErrorTypes.NETWORK_ERROR && networkAttempts < NETWORK_RETRY_MS.length) {
      const delay = NETWORK_RETRY_MS[networkAttempts++];
      setSpinner(true);
      clearTimeout(retryTimer);
      // startLoad() resumes loading once a playlist exists; a manifest that never loaded has
      // to be requested again.
      retryTimer = setTimeout(() => {
        if (!hls) return;
        if (hls.levels?.length) hls.startLoad();
        else hls.loadSource(view.playlistUrl);
      }, delay);
      return;
    }

    if (data.type === Hls.ErrorTypes.MEDIA_ERROR && mediaAttempts < MAX_MEDIA_RECOVERIES) {
      mediaAttempts++;
      if (mediaAttempts === MAX_MEDIA_RECOVERIES) hls?.swapAudioCodec();
      hls?.recoverMediaError();
      return;
    }

    showOverlay(FAILED_MESSAGE, true);
  }

  async function attach(source, token) {
    let Hls = null;
    if (hasMediaSource()) {
      hlsClass ??= (async () => {
        try {
          return await loadHls();
        } catch {
          return null;
        }
      })();
      Hls = await hlsClass;
    }
    if (token !== generation || disposed) return;

    if (Hls?.isSupported?.()) {
      hls = new Hls({ ...HLS_CONFIG });
      hls.on(Hls.Events.ERROR, (_event, data) => onHlsError(Hls, data));
      hls.on(Hls.Events.FRAG_LOADED, () => {
        networkAttempts = 0;
        mediaAttempts = 0;
      });
      hls.attachMedia(video);
      hls.loadSource(source);
      return;
    }

    if (video.canPlayType?.(NATIVE_HLS)) {
      native = true;
      video.setAttribute("src", source);
      return;
    }

    setBusy(false);
    showOverlay(UNSUPPORTED_MESSAGE, false);
  }

  async function start(position) {
    const token = ++generation;
    teardownEngine();
    resetUi();
    startAt = position;
    renderPosition(position ?? 0);
    spinnerTimer = setTimeout(() => setSpinner(true), SPINNER_DELAY_MS);
    await attach(view.playlistUrl, token);
  }

  // ----- speed menu -----

  function speedItems() {
    return [...el["speed-menu"].querySelectorAll("[data-rate]")];
  }

  function openSpeedMenu() {
    el["speed-menu"].hidden = false;
    el.speed.setAttribute("aria-expanded", "true");
    const items = speedItems();
    (items.find((i) => i.getAttribute("aria-checked") === "true") ?? items[0])?.focus();
  }

  function closeSpeedMenu(returnFocus) {
    if (el["speed-menu"].hidden) return;
    el["speed-menu"].hidden = true;
    el.speed.setAttribute("aria-expanded", "false");
    if (returnFocus) el.speed.focus();
  }

  // ----- events -----

  on(video, "loadedmetadata", () => {
    if (startAt != null && Number.isFinite(duration())) {
      seekTo(startAt);
    }
    startAt = null;
    renderTicks();
    renderPosition();
  });
  on(video, "durationchange", () => {
    renderTicks();
    renderPosition();
  });
  on(video, "timeupdate", () => {
    if (!dragging) renderPosition();
    lastKnownTime = video.currentTime;
    renderBuffered();
  });
  on(video, "progress", renderBuffered);
  on(video, "play", renderPlayState);
  on(video, "pause", renderPlayState);
  on(video, "ended", renderPlayState);
  on(video, "volumechange", renderVolume);
  on(video, "ratechange", renderSpeed);
  on(video, "waiting", () => setSpinner(true));
  for (const ready of ["canplay", "playing", "seeked", "loadeddata"]) {
    on(video, ready, () => {
      clearTimeout(spinnerTimer);
      setSpinner(false);
      setBusy(false);
    });
  }
  on(video, "error", () => {
    if (native) showOverlay(FAILED_MESSAGE, true);
  });

  function togglePlay() {
    if (!view) return;
    if (video.ended) seekTo(0);
    if (video.paused || video.ended) void video.play()?.catch?.(() => {});
    else video.pause();
    renderPlayState();
  }

  on(el.play, "click", togglePlay);
  on(el.center, "click", () => {
    togglePlay();
    el.play.focus();
  });
  on(el.back, "click", () => seekTo(video.currentTime - SKIP_SECONDS));
  on(el.forward, "click", () => seekTo(video.currentTime + SKIP_SECONDS));
  on(el.mute, "click", () => {
    video.muted = !video.muted;
    renderVolume();
  });
  on(el.retry, "click", () => {
    if (!view) return;
    void start(lastKnownTime);
  });

  on(el.volume, "keydown", (event) => {
    const steps = { ArrowRight: 1, ArrowUp: 1, ArrowLeft: -1, ArrowDown: -1 };
    if (event.key in steps) setVolume(video.volume + steps[event.key] * VOLUME_STEP);
    else if (event.key === "Home") setVolume(0);
    else if (event.key === "End") setVolume(1);
    else return;
    event.preventDefault();
    event.stopPropagation();
  });
  on(el.volume, "pointerdown", (event) => {
    const rect = el.volume.getBoundingClientRect();
    if (rect.width > 0) setVolume((event.clientX - rect.left) / rect.width);
  });

  on(el.speed, "click", () => {
    if (el["speed-menu"].hidden) openSpeedMenu();
    else closeSpeedMenu(true);
  });
  on(el["speed-menu"], "click", (event) => {
    const item = event.target instanceof Element ? event.target.closest("[data-rate]") : null;
    if (!item) return;
    video.playbackRate = Number(item.dataset.rate);
    renderSpeed();
    closeSpeedMenu(true);
  });
  on(el["speed-menu"], "keydown", (event) => {
    const items = speedItems();
    const index = items.indexOf(document.activeElement);
    if (event.key === "Escape" || event.key === "Tab") {
      if (event.key === "Escape") event.preventDefault();
      closeSpeedMenu(event.key === "Escape");
    } else if (event.key === "ArrowDown") {
      event.preventDefault();
      items[(index + 1) % items.length]?.focus();
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      items[(index - 1 + items.length) % items.length]?.focus();
    } else {
      return;
    }
    event.stopPropagation();
  });
  on(document, "pointerdown", (event) => {
    if (el["speed-menu"].hidden) return;
    if (event.target instanceof Node && (el["speed-menu"].contains(event.target) || el.speed.contains(event.target))) return;
    closeSpeedMenu(false);
  });

  function ratioAt(event) {
    const rect = el.seek.getBoundingClientRect();
    return rect.width > 0 ? clamp((event.clientX - rect.left) / rect.width, 0, 1) : 0;
  }

  function hoverAt(ratio) {
    const total = duration();
    if (!Number.isFinite(total)) return;
    const time = ratio * total;
    const chapter = chapterAt(time);
    el["hover-time"].textContent = chapter?.title
      ? `${formatTimecode(time)} · ${chapter.title}`
      : formatTimecode(time);
    el["hover-time"].style.left = percent(ratio);
  }

  function preview(ratio) {
    el.played.style.width = percent(ratio);
    el.knob.style.left = percent(ratio);
    el.current.textContent = formatTimecode(ratio * duration());
  }

  on(el.seek, "pointerdown", (event) => {
    if (!hasMetadata() || event.button > 0) return;
    dragging = true;
    el.seek.classList.add("drag");
    el.seek.setPointerCapture?.(event.pointerId);
    preview(ratioAt(event));
  });
  on(el.seek, "pointermove", (event) => {
    const ratio = ratioAt(event);
    hoverAt(ratio);
    if (dragging) preview(ratio);
  });
  const endDrag = (event, commit) => {
    if (!dragging) return;
    dragging = false;
    el.seek.classList.remove("drag");
    if (commit) seekTo(ratioAt(event) * duration());
    else renderPosition();
  };
  on(el.seek, "pointerup", (event) => endDrag(event, true));
  on(el.seek, "pointercancel", (event) => endDrag(event, false));

  on(el.seek, "keydown", (event) => {
    if (!hasMetadata()) return;
    const total = duration();
    if (event.key === "ArrowRight") seekTo(video.currentTime + SEEK_STEP_SECONDS);
    else if (event.key === "ArrowLeft") seekTo(video.currentTime - SEEK_STEP_SECONDS);
    else if (event.key === "Home") seekTo(0);
    else if (event.key === "End") seekTo(total);
    else return;
    // The player's global shortcuts yield to the slider.
    event.preventDefault();
    event.stopPropagation();
  });

  // ----- public API -----

  return {
    async load(next) {
      if (disposed || !next) return;
      view = next;
      renderView();
      renderSpeed();
      renderVolume();
      renderPlayState();
      const finished = view.state === "saved" || view.state === "partial";
      const resume = finished && Number.isFinite(view.resumeSec) && view.resumeSec > 0 ? view.resumeSec : null;
      await start(resume);
    },

    update(next) {
      if (disposed || !next || !view || next.recordingId !== view.recordingId) return;
      // Same recording: refresh what it shows, never the source or the position.
      view = { ...next, resumeSec: view.resumeSec };
      renderView();
      renderPosition();
    },

    dispose() {
      if (disposed) return;
      disposed = true;
      generation++;
      teardownEngine();
      listeners.abort();
      view = null;
    },
  };
}

// ----- Blazor entry points (one player per root) -----

const players = new WeakMap();

export async function init(root, view) {
  if (!root) return;
  players.get(root)?.dispose();
  const player = createPlayer(root);
  players.set(root, player);
  await player.load(view);
}

export async function load(root, view) {
  await players.get(root)?.load(view);
}

export function update(root, view) {
  players.get(root)?.update(view);
}

export function dispose(root) {
  players.get(root)?.dispose();
  players.delete(root);
}
