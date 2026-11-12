// Recording player (player contract §§2–5, 13). VideoPlayer.razor renders the skeleton once;
// this module owns hls.js, every control, and all dynamic player DOM. hls.js is imported lazily
// from the vendored copy, only on recording pages, through an injectable loader for tests.

import {
  createStatsProbe,
  registerStatsSource,
  unregisterStatsSource,
} from "/js/stats.js";
import { createProgress } from "/js/player-progress.js";
import { readPlayerPrefs, writePlayerPrefs } from "/js/player-prefs.js";
import { playerAction } from "/js/player-keys.js";

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
const IDLE_MS = 3000;
// Following keeps the playhead within this much of the live sync position (contract §4).
const LIVE_SLACK_SEC = 2;
const WAITING_RETRY_MS = 3000;
// hls.js error details that mean a live capture has no playable playlist yet.
const WAITING_DETAILS = new Set([
  "manifestLoadError", "manifestParsingError", "levelLoadError", "levelEmptyError",
]);
const LIVE_EDGE = Symbol("live edge");
const NATIVE_HLS = "application/vnd.apple.mpegurl";

const UNSUPPORTED_MESSAGE = "This browser can't play HLS video. Try a current version of Chrome, Edge, Firefox, or Safari.";
const FAILED_MESSAGE = "The video could not be played.";
const UNAVAILABLE_MESSAGE = "This recording is unavailable.";

// Mirrors Primitives/StatusChip.razor markup for the states the player shows.
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
  "stats", "edge", "live", "waiting", "pip", "theater", "fullscreen",
];

// JS twin of Ui/Format.Timecode (the source of truth); keep the two in step.
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

export function createPlayer(
  root,
  { loadHls = defaultLoadHls, dotNet = null, fetch = globalThis.fetch, storage } = {},
) {
  const doc = root.ownerDocument;
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
  let live = false; // Recording/Reconnecting until the playlist reports ENDLIST
  let following = false; // live only: keep the playhead at the sync position
  let waiting = false; // live only: no playable playlist yet
  let waitingTimer = 0;
  let resumePlaying = false; // VOD→live reload keeps playing if it was
  let prefs = readPlayerPrefs(storage); // read once; applied on every load
  let theater = false;
  let idleTimer = 0;
  let wasFullscreen = false;
  const stats = createStatsProbe({ video });
  const progress = createProgress({
    save: (recordingId, seconds) => invokeDotNet("SaveProgress", recordingId, seconds),
    clear: (recordingId) => invokeDotNet("ClearProgress", recordingId),
    fetch,
  });

  // ----- helpers -----

  function seekableEnd() {
    const ranges = video.seekable;
    return ranges?.length ? ranges.end(ranges.length - 1) : NaN;
  }

  // The timeline length: the media duration, or the seekable end while native HLS reports a
  // live stream as Infinity.
  function duration() {
    if (Number.isFinite(video.duration) && video.duration > 0) return video.duration;
    const end = seekableEnd();
    return Number.isFinite(end) && end > 0 ? end : NaN;
  }
  const hasMetadata = () => video.readyState >= 1 && Number.isFinite(duration());

  /** hls.js's live sync position (one target duration behind the end), or end − 1 before it has one. */
  function liveSync() {
    const sync = hls?.liveSyncPosition;
    if (Number.isFinite(sync)) return sync;
    const end = seekableEnd();
    return Number.isFinite(end) ? Math.max(0, end - 1) : NaN;
  }

  function setSpinner(visible) {
    if (el.spinner) el.spinner.hidden = !visible;
  }

  function setBusy(busy) {
    if (busy) root.setAttribute("aria-busy", "true");
    else root.removeAttribute("aria-busy");
  }

  function showOverlay(message, canRetry) {
    clearTimeout(spinnerTimer);
    setSpinner(false);
    setBusy(false);
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

  function renderLive() {
    if (el.live) {
      el.live.hidden = !live;
      el.live.classList.toggle("at-edge", live && following);
    }
    if (el.edge) {
      const end = seekableEnd();
      const total = duration();
      el.edge.hidden = !live || !Number.isFinite(end) || !Number.isFinite(total);
      if (!el.edge.hidden) el.edge.style.left = percent(end / total);
    }
  }

  function enterWaiting() {
    if (!waiting) {
      waiting = true;
      clearTimeout(spinnerTimer);
      setSpinner(true);
      el.waiting.hidden = false;
    }
  }

  function leaveWaiting() {
    clearTimeout(waitingTimer);
    waitingTimer = 0;
    if (!waiting) return;
    waiting = false;
    el.waiting.hidden = true;
  }

  /** ENDLIST (or the circuit's finished state): the capture is now an ordinary recording. */
  function becameFinished() {
    if (!live) return;
    live = false;
    following = false;
    renderLive();
    renderTicks();
    renderPosition();
    if (!video.paused && !video.ended) progress.playing();
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

  /** A seek the viewer asked for; while live it also decides whether to keep following. */
  function userSeek(time) {
    if (!Number.isFinite(time)) return;
    if (live) {
      const end = seekableEnd();
      const sync = liveSync();
      if (Number.isFinite(end) && time >= end - LIVE_SLACK_SEC) {
        // The true end would stall at the last segment; the sync position starts buffered.
        time = Number.isFinite(sync) ? sync : time;
        following = true;
      } else {
        following = Number.isFinite(sync) && time >= sync - LIVE_SLACK_SEC;
      }
    }
    seekTo(time);
    renderLive();
  }

  function goLive() {
    if (!live) return;
    const sync = liveSync();
    following = true;
    if (Number.isFinite(sync)) seekTo(sync);
    renderLive();
  }

  function setVolume(value) {
    const before = video.volume;
    video.volume = clamp(Math.round(value * 100) / 100, 0, 1);
    // Raising the volume above 0 unmutes; lowering it leaves mute alone.
    if (video.volume > before && video.volume > 0) video.muted = false;
    renderVolume();
    savePrefs();
  }

  // ----- preferences, theater, fullscreen, picture-in-picture -----

  function applyPrefs() {
    video.volume = prefs.volume;
    video.muted = prefs.muted;
    // A new source resets playbackRate to defaultPlaybackRate, so set both.
    video.defaultPlaybackRate = prefs.speed;
    video.playbackRate = prefs.speed;
  }

  function savePrefs() {
    prefs = { volume: video.volume, muted: video.muted, speed: video.playbackRate || 1 };
    writePlayerPrefs(prefs, storage);
  }

  function toggleMute() {
    video.muted = !video.muted;
    renderVolume();
    savePrefs();
  }

  function setRate(rate) {
    video.defaultPlaybackRate = rate;
    video.playbackRate = rate;
    renderSpeed();
    savePrefs();
  }

  function renderTheater() {
    if (!el.theater) return;
    el.theater.setAttribute("aria-pressed", theater ? "true" : "false");
    el.theater.classList.toggle("on", theater);
  }

  function toggleTheater() {
    // UiPreferences is the source of truth; SetTheater persists it and Changed comes back
    // through setTheater. The button reflects the request at once.
    theater = !theater;
    renderTheater();
    void invokeDotNet("SetTheater", theater);
  }

  const fullscreenElement = () => doc.fullscreenElement ?? doc.webkitFullscreenElement ?? null;

  async function toggleFullscreen() {
    try {
      if (fullscreenElement()) {
        await (doc.exitFullscreen?.() ?? doc.webkitExitFullscreen?.());
      } else if (typeof root.requestFullscreen === "function") {
        // The container keeps the custom controls.
        await root.requestFullscreen();
      } else if (typeof video.requestFullscreen === "function") {
        await video.requestFullscreen();
      } else {
        video.webkitEnterFullscreen?.(); // iOS Safari: native controls
      }
    } catch (error) {
      console.debug("Stream Keepr: fullscreen was refused.", error);
    }
  }

  function renderFullscreen() {
    const active = fullscreenElement() !== null;
    const ours = fullscreenElement() === root;
    if (el.fullscreen) {
      el.fullscreen.setAttribute("aria-label", ours ? "Exit full screen" : "Full screen");
      el.fullscreen.querySelector(".sk-icon").textContent = ours ? "fullscreen_exit" : "fullscreen";
    }
    if (el.pip) el.pip.disabled = active;
    if (ours && !wasFullscreen) root.focus();
    else if (!ours && wasFullscreen) el.fullscreen?.focus();
    wasFullscreen = ours;
  }

  const pipSupported = () =>
    doc.pictureInPictureEnabled === true && typeof video.requestPictureInPicture === "function";

  async function togglePip() {
    try {
      if (doc.pictureInPictureElement) await doc.exitPictureInPicture();
      else await video.requestPictureInPicture();
    } catch (error) {
      console.debug("Stream Keepr: picture-in-picture was refused.", error);
    }
  }

  function renderPip() {
    if (!el.pip) return;
    el.pip.hidden = !pipSupported();
    const inPip = doc.pictureInPictureElement === video;
    el.pip.setAttribute("aria-label", inPip ? "Exit picture in picture" : "Picture in picture");
    el.pip.querySelector(".sk-icon").textContent = inPip ? "picture_in_picture_off" : "picture_in_picture_alt";
  }

  // ----- idle -----

  function keyboardFocusInside() {
    const active = doc.activeElement;
    if (!active || active === doc.body || !root.contains(active)) return false;
    try {
      return active.matches(":focus-visible");
    } catch {
      return true; // no :focus-visible support: treat any focus as keyboard focus
    }
  }

  function canIdle() {
    return Boolean(view) && !video.paused && !video.ended && el["speed-menu"].hidden
      && !keyboardFocusInside();
  }

  /** Reveals the overlays and restarts the 3 s idle timer. */
  function wake() {
    root.classList.remove("idle");
    clearTimeout(idleTimer);
    idleTimer = setTimeout(() => {
      if (canIdle()) root.classList.add("idle");
    }, IDLE_MS);
  }

  function setStatsPressed(open) {
    if (el.stats)
      el.stats.setAttribute("aria-pressed", open ? "true" : "false");
  }

  function toggleStats() {
    if (!view || !el.stats) return;
    // The panel switch announces its state through sk:stats-state, which keeps this
    // button current; read the DOM so the click always toggles the shared state.
    const open = el.stats.getAttribute("aria-pressed") !== "true";
    setStatsPressed(open);
    // Let the panel apply the same state immediately; SetStatsOpen persists it.
    root.dispatchEvent(new CustomEvent("sk:stats-open", {
      detail: { open },
      bubbles: true,
    }));
    void invokeDotNet("SetStatsOpen", open);
  }

  async function invokeDotNet(identifier, ...args) {
    if (!dotNet?.invokeMethodAsync) return;
    try {
      await dotNet.invokeMethodAsync(identifier, ...args);
    } catch (error) {
      // A closed circuit cannot persist the preference; the player control still works.
      console.debug(`Stream Keepr: ${identifier} was not delivered.`, error);
    }
  }

  // ----- engine -----

  function teardownEngine() {
    clearTimeout(retryTimer);
    clearTimeout(spinnerTimer);
    leaveWaiting();
    retryTimer = 0;
    stats.detachEngine();
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
    following = false;
    networkAttempts = 0;
    mediaAttempts = 0;
    el.buffered.style.width = "0%";
  }

  function onHlsError(Hls, data) {
    // A live capture without a playable playlist is waiting, not failing: retry the manifest
    // quietly every 3 s with no limit (contract §13).
    if (live && WAITING_DETAILS.has(data?.details)) {
      enterWaiting();
      if (data.fatal) {
        clearTimeout(waitingTimer);
        waitingTimer = setTimeout(() => {
          if (hls && live) hls.loadSource(view.playlistUrl);
        }, WAITING_RETRY_MS);
      }
      return;
    }

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
      hlsClass ??= loadHls();
      try {
        Hls = await hlsClass;
      } catch (error) {
        // A failed import (offline, blocked) is retryable: forget it so Retry imports again.
        hlsClass = null;
        if (token !== generation || disposed) return;
        console.warn("Stream Keepr: hls.js could not be loaded.", error);
        showOverlay(FAILED_MESSAGE, true);
        return;
      }
    }
    if (token !== generation || disposed) return;

    if (Hls?.isSupported?.()) {
      hls = new Hls({ ...HLS_CONFIG });
      stats.attachHls(hls, Hls);
      hls.on(Hls.Events.ERROR, (_event, data) => onHlsError(Hls, data));
      hls.on(Hls.Events.FRAG_LOADED, () => {
        networkAttempts = 0;
        mediaAttempts = 0;
        leaveWaiting();
      });
      hls.on(Hls.Events.LEVEL_LOADED, (_event, data) => {
        const details = data?.details;
        if (!details) return;
        if (details.live === false) becameFinished();
        else if (live && !details.fragments?.length) enterWaiting();
        renderLive();
      });
      hls.attachMedia(video);
      hls.loadSource(source);
      return;
    }

    if (video.canPlayType?.(NATIVE_HLS)) {
      native = true;
      stats.setNative(true);
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
    const at = typeof position === "number" ? position : 0;
    lastKnownTime = at;
    renderPosition(at);
    renderLive();
    spinnerTimer = setTimeout(() => setSpinner(true), SPINNER_DELAY_MS);
    await attach(view.playlistUrl, token);
  }

  // ----- speed menu -----

  function speedItems() {
    return [...el["speed-menu"].querySelectorAll("[data-rate]")];
  }

  function openSpeedMenu() {
    root.classList.remove("idle");
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
    if (startAt === LIVE_EDGE) {
      goLive();
    } else if (startAt != null && Number.isFinite(duration())) {
      // Resume lands at least a second before the end so playback has something to play.
      seekTo(Math.min(startAt, duration() - 1));
    }
    startAt = null;
    if (resumePlaying) {
      resumePlaying = false;
      void video.play()?.catch?.(() => {});
    }
    renderTicks();
    renderPosition();
    renderLive();
  });
  on(video, "durationchange", () => {
    // Native HLS has no LEVEL_LOADED: a finite duration is its ENDLIST.
    if (native && live && Number.isFinite(video.duration)) becameFinished();
    renderTicks();
    renderPosition();
    renderLive();
  });
  on(video, "timeupdate", () => {
    if (live && following && !video.paused) {
      const sync = liveSync();
      if (Number.isFinite(sync) && sync - video.currentTime > LIVE_SLACK_SEC) seekTo(sync);
    }
    if (!dragging) renderPosition();
    lastKnownTime = video.currentTime;
    renderBuffered();
  });
  on(video, "progress", renderBuffered);
  on(video, "play", () => {
    renderPlayState();
    wake();
    progress.playing();
  });
  on(video, "pause", () => {
    renderPlayState();
    root.classList.remove("idle"); // never idle while paused
    // A pause leaves the live edge; Live edge or a seek to it follows again.
    if (live && following) {
      following = false;
      renderLive();
    }
    // A pause that ends playback is followed by "ended", which clears instead.
    if (!video.ended) progress.paused();
  });
  on(video, "ended", () => {
    renderPlayState();
    progress.ended();
  });
  on(window, "pagehide", () => progress.flush());
  on(document, "visibilitychange", () => {
    if (document.visibilityState === "hidden") progress.flush();
  });
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
    stats.onNativeError(video.error);
    if (native) showOverlay(FAILED_MESSAGE, true);
  });

  function togglePlay() {
    if (!view) return;
    if (video.ended) {
      seekTo(0);
      progress.restarted();
    }
    if (video.paused || video.ended) void video.play()?.catch?.(() => {});
    else video.pause();
    renderPlayState();
  }

  // Mouse/pen clicks on the frame toggle playback; touch taps only reveal the
  // controls (contract §6). Controls have their own listeners, so they never
  // bubble into this handler.
  let lastPointerType = "mouse";
  on(video, "pointerdown", (event) => {
    lastPointerType = event.pointerType || "mouse";
  });
  on(video, "click", (event) => {
    const pointerType = event.pointerType || lastPointerType;
    if (pointerType === "touch") return;
    togglePlay();
  });

  on(el.play, "click", togglePlay);
  on(el.center, "click", () => {
    togglePlay();
    el.play.focus();
  });
  on(el.back, "click", () => seekBy(-SKIP_SECONDS));
  on(el.forward, "click", () => seekBy(SKIP_SECONDS));
  on(el.live, "click", goLive);
  on(el.mute, "click", toggleMute);
  on(el.theater, "click", toggleTheater);
  on(el.fullscreen, "click", () => void toggleFullscreen());
  on(el.pip, "click", () => void togglePip());
  on(doc, "fullscreenchange", renderFullscreen);
  on(doc, "webkitfullscreenchange", renderFullscreen);
  on(video, "enterpictureinpicture", renderPip);
  on(video, "leavepictureinpicture", renderPip);

  for (const type of ["pointermove", "pointerdown", "keydown", "focusin"]) on(root, type, wake);

  // Global shortcuts while a recording is open; the decision lives in player-keys.js.
  on(doc, "keydown", (event) => {
    if (!view) return;
    const action = playerAction(event, doc);
    if (!action) return;
    event.preventDefault();
    runAction(action);
    wake();
  });

  function seekBy(seconds) {
    if (!hasMetadata()) return;
    userSeek(clamp(video.currentTime + seconds, 0, duration()));
  }

  function runAction(action) {
    switch (action) {
      case "toggle-play": togglePlay(); break;
      case "back-10": seekBy(-SKIP_SECONDS); break;
      case "forward-10": seekBy(SKIP_SECONDS); break;
      case "back-5": seekBy(-SEEK_STEP_SECONDS); break;
      case "forward-5": seekBy(SEEK_STEP_SECONDS); break;
      case "mute": toggleMute(); break;
      case "fullscreen": void toggleFullscreen(); break;
      case "theater": toggleTheater(); break;
      case "stats": toggleStats(); break;
    }
  }
  on(el.retry, "click", () => {
    if (!view) return;
    void start(live ? LIVE_EDGE : lastKnownTime);
  });
  on(el.stats, "click", toggleStats);
  on(document, "sk:stats-state", (event) => {
    const next = event instanceof CustomEvent ? event.detail?.open : undefined;
    if (typeof next === "boolean") setStatsPressed(next);
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
    setRate(Number(item.dataset.rate));
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
    if (commit) userSeek(ratioAt(event) * duration());
    else renderPosition();
  };
  on(el.seek, "pointerup", (event) => endDrag(event, true));
  on(el.seek, "pointercancel", (event) => endDrag(event, false));

  on(el.seek, "keydown", (event) => {
    if (!hasMetadata()) return;
    const total = duration();
    if (event.key === "ArrowRight") userSeek(Math.min(video.currentTime + SEEK_STEP_SECONDS, total));
    else if (event.key === "ArrowLeft") userSeek(Math.max(video.currentTime - SEEK_STEP_SECONDS, 0));
    else if (event.key === "Home") userSeek(0);
    else if (event.key === "End") userSeek(total);
    else return;
    // The player's global shortcuts yield to the slider.
    event.preventDefault();
    event.stopPropagation();
  });

  // ----- public API -----

  return {
    async load(next) {
      if (disposed || !next) return;
      // Route change: the previous recording's position goes out before its engine is torn down.
      if (view && view.recordingId !== next.recordingId) progress.flush();
      view = next;
      live = next.state === "recording" || next.state === "reconnecting";
      progress.track({
        recordingId: next.recordingId,
        position: () => video.currentTime,
        duration,
        live: () => live,
      });
      stats.setView(next);
      stats.reset();
      setStatsPressed(Boolean(next.statsOpen));
      theater = Boolean(next.theater);
      renderTheater();
      renderPip();
      renderFullscreen();
      applyPrefs();
      renderView();
      renderSpeed();
      renderVolume();
      renderPlayState();
      // Finished recordings resume; live captures open at the live edge and ignore ResumeSec.
      resumePlaying = false;
      const resume = Number.isFinite(view.resumeSec) && view.resumeSec > 0 ? view.resumeSec : null;
      await start(live ? LIVE_EDGE : resume);
    },

    update(next) {
      if (disposed || !next || !view || next.recordingId !== view.recordingId) return;
      // Same recording: refresh what it shows, never the source or the position.
      view = { ...next, resumeSec: view.resumeSec };
      stats.setView(view);
      renderView();
      renderPosition();

      const nowLive = next.state === "recording" || next.state === "reconnecting";
      if (nowLive && !live) {
        // VOD→live: a finished session reopened. Reload the same playlist where the viewer
        // is, keeping play/pause; Live edge is theirs to press.
        live = true;
        progress.live();
        resumePlaying = !video.paused && !video.ended;
        void start(video.currentTime);
      } else if (!nowLive && live) {
        if (waiting) {
          // The capture finished without ever producing a segment.
          leaveWaiting();
          showOverlay(UNAVAILABLE_MESSAGE, true);
        }
        becameFinished();
      }
    },

    /** UiPreferences.Changed: the shell's theater state, from either control. */
    setTheater(open) {
      if (disposed) return;
      theater = Boolean(open);
      renderTheater();
    },

    seek(time) {
      if (disposed || !Number.isFinite(time)) return;
      if (!Number.isFinite(duration())) {
        // Metadata is not loaded yet; loadedmetadata seeks to the queued position.
        startAt = Math.max(0, time);
        return;
      }
      userSeek(time);
    },

    dispose() {
      if (disposed) return;
      progress.flush();
      progress.dispose();
      clearTimeout(idleTimer);
      disposed = true;
      generation++;
      teardownEngine();
      stats.dispose();
      listeners.abort();
      view = null;
    },
    stats,
  };
}

// ----- Blazor entry points (one player per root) -----

const players = new WeakMap();

export async function init(root, self, view) {
  if (!root) return;
  players.get(root)?.dispose();
  unregisterStatsSource(root);
  const player = createPlayer(root, { dotNet: self });
  players.set(root, player);
  registerStatsSource(root, player.stats);
  await player.load(view);
}

export async function load(root, view) {
  await players.get(root)?.load(view);
}

export function update(root, view) {
  players.get(root)?.update(view);
}

export function setTheater(root, open) {
  players.get(root)?.setTheater(open);
}

export function seek(root, seconds) {
  players.get(root)?.seek(seconds);
}

export function dispose(root) {
  unregisterStatsSource(root);
  players.get(root)?.dispose();
  players.delete(root);
}
