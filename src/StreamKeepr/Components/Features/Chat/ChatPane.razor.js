// Chat replay pane. Blazor renders the skeleton once; this module owns the log, polling,
// auto-scroll, and the C shortcut, and applies the viewer's chat sync offset, which .NET sends
// from Settings › Chat. The player owns the <video> element.
import { shortcutBlocked } from "/js/player-keys.js";
import { isLiveState } from "/js/recording-state.js";

const HOOKS = [
  "chat-state", "chat-body", "chat-log", "chat-jump", "chat-notice",
];

const POLL_MS = 500;
// After a failed fetch, wait before the next attempt; the wait doubles up to the cap.
const RETRY_MIN_MS = 1000;
const RETRY_MAX_MS = 30_000;
const WINDOW_BEFORE_SEC = 30;
const WINDOW_AFTER_SEC = 120;
// Each continuation re-reads this much before what is loaded; ids dedupe the overlap.
const OVERLAP_SEC = 5;
// A complete recording fetches its next window when the playhead gets this close to its end.
const LOOKAHEAD_SEC = 30;
// Scrollback kept and rendered. Older rows drop off the top so a 12-hour live session does not
// grow the DOM forever. Not the fetch limit; the server's limits arrive in the load payload.
const MAX_ROWS = 1000;
// A fresh window starts 30 s back, so after it loads the pane fetches earlier chat in chunks
// until the log overflows, the chat starts, or it has reached this far back.
const BACKFILL_CHUNK_SEC = 120;
const BACKFILL_MAX_SEC = 1800;
// A playhead move larger than this between events is a seek, not playback.
const SEEK_SEC = 2;
// The glide to the newest row is a critically damped spring on the distance left. Rows that
// arrive mid-glide add distance without resetting its speed, so busy chat flows on instead of
// stopping and starting with every batch, as a restarted native smooth scroll does. Higher
// settles sooner; 12 per second settles in about 0.4 s.
const GLIDE_RATE = 12;
// The longest frame the glide integrates; a longer one (a throttled tab) counts as this.
const GLIDE_MAX_STEP_SEC = 0.05;
// Keys that scroll the log towards older chat; they hand scrolling back to the viewer.
const SCROLL_UP_KEYS = new Set(["ArrowUp", "PageUp", "Home"]);
const BADGE_LABELS = {
  broadcaster: "BROADCASTER",
  founder: "FOUNDER",
  lead_moderator: "LEAD MOD",
  moderator: "MOD",
  staff: "STAFF",
  subscriber: "SUB",
  turbo: "TURBO",
  vip: "VIP",
};

const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

function validColor(value) {
  return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value) ? value : null;
}

// WCAG relative luminance of an [r, g, b] colour in 0–255.
function luminance([r, g, b]) {
  const linear = (channel) => {
    const value = channel / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

function hexChannels(hex) {
  return [1, 3, 5].map((start) => parseInt(hex.slice(start, start + 2), 16));
}

/** The pane's ground as [r, g, b], or null when it is not an opaque rgb() colour. */
function opaqueGround(element) {
  const match = /^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)$/
    .exec(element ? getComputedStyle(element).backgroundColor : "");
  if (!match || (match[4] !== undefined && Number(match[4]) < 1)) return null;
  return match.slice(1, 4).map(Number);
}

/**
 * A chatter's Twitch colour where it reaches 4.5:1 on the pane's ground (ChatPane card);
 * otherwise null, so the name keeps text-primary. An unreadable ground keeps the colour.
 */
export function readableNameColor(value, ground) {
  const color = validColor(value);
  if (!color || !ground) return color;
  const [light, dark] = [luminance(hexChannels(color)), luminance(ground)].sort((a, b) => b - a);
  return (light + 0.05) / (dark + 0.05) >= 4.5 ? color : null;
}

function apiUrl(recordingId) {
  return new URL(
    `/api/recordings/${encodeURIComponent(recordingId)}/chat`,
    window.location.origin,
  );
}

function emotesUrl(recordingId) {
  return new URL(
    `/api/recordings/${encodeURIComponent(recordingId)}/emotes`,
    window.location.origin,
  );
}

function isHttpsUrl(value) {
  if (typeof value !== "string") return false;
  try {
    return new URL(value).protocol === "https:";
  } catch {
    return false;
  }
}

/**
 * Builds the code-to-entry lookup for a catalog response. The server already orders and dedupes
 * entries; a Map keeps the first entry for a code, which is the documented precedence. Provider
 * data is untrusted, so only absolute https image sources enter the catalog.
 */
export function buildEmoteCatalog(entries) {
  const catalog = new Map();
  const claimed = new Set();
  for (const entry of entries || []) {
    if (!entry || typeof entry.code !== "string" || entry.code === "") continue;
    // First entry wins even when its src is invalid: the code then falls back to text and a
    // later duplicate must not replace it.
    if (claimed.has(entry.code)) continue;
    claimed.add(entry.code);
    if (!isHttpsUrl(entry.src)) continue;
    catalog.set(entry.code, entry);
  }
  return catalog;
}

/**
 * Splits message text into text, Twitch-emote, and third-party-emote segments. Matching runs on
 * code points so a native range cannot split a surrogate pair; only U+0020 separates tokens, and
 * every character between native ranges is preserved. `nativeRanges` are inclusive code-point
 * ranges, as chat messages already store them.
 */
export function matchEmotes(text, nativeRanges, catalog) {
  const chars = Array.from(String(text ?? ""));
  const ranges = [];
  for (const range of nativeRanges || []) {
    if (!range || !Number.isInteger(range.from) || !Number.isInteger(range.to)) continue;
    if (range.from < 0 || range.to < range.from || range.to >= chars.length) continue;
    ranges.push(range);
  }
  ranges.sort((a, b) => a.from - b.from);

  const segments = [];
  let pending = "";

  const flushText = () => {
    if (pending !== "") {
      segments.push({ type: "text", text: pending });
      pending = "";
    }
  };

  const matchToken = (start, end) => {
    const token = chars.slice(start, end).join("");
    const entry = catalog?.get(token);
    if (entry) {
      flushText();
      segments.push({ type: "emote", text: token, entry });
    } else {
      pending += token;
    }
  };

  const matchPlain = (start, end) => {
    let tokenStart = start;
    for (let index = start; index < end; index++) {
      if (chars[index] !== " ") continue;
      if (tokenStart < index) matchToken(tokenStart, index);
      pending += " ";
      tokenStart = index + 1;
    }
    if (tokenStart < end) matchToken(tokenStart, end);
  };

  let cursor = 0;
  for (const range of ranges) {
    if (range.from < cursor) continue;
    matchPlain(cursor, range.from);
    flushText();
    segments.push({
      type: "native",
      text: chars.slice(range.from, range.to + 1).join(""),
      id: range.id,
    });
    cursor = range.to + 1;
  }
  matchPlain(cursor, chars.length);
  flushText();
  return segments;
}

export function createChatPane(root, {
  dotNet = null,
  fetch: fetchImpl = globalThis.fetch?.bind(globalThis),
} = {}) {
  const el = Object.fromEntries(HOOKS.map((name) => [name, root.querySelector(`[data-sk="${name}"]`)]));
  const listeners = new AbortController();
  const on = (target, type, handler, options = {}) =>
    target?.addEventListener(type, handler, { signal: listeners.signal, ...options });

  let view = null;
  let open = false;
  // The phone Chat tab owns the disclosure: no toggle or C may close it (#175).
  let fixedOpen = false;
  let disposed = false;
  let currentTime = 0;
  let paused = true;
  let loaded = false;
  // Media time through which every message has been fetched. Continuations start here.
  let loadedTo = 0;
  // loadedTo is the last row of a window the limit cut short; continue from it exactly, since
  // re-reading an overlap could refill the limit with rows already loaded in a busy chat.
  let loadedToIsRow = false;
  // A complete recording's window reached the end of its chat; nothing more to fetch.
  let endReached = false;
  // The fresh window last requested, raised as old rows are dropped. A playhead before
  // windowFrom, or past windowTo while the first load is in flight, needs a fresh window.
  let windowFrom = 0;
  let windowTo = 0;
  let loading = false;
  let retryAt = 0;
  let retryDelay = RETRY_MIN_MS;
  let controller = null;
  let messages = new Map();
  let gaps = [];
  let syncOffset = 0;
  let following = true;
  let newCount = 0;
  let lastRenderKey = "";
  // The log's row and gap elements by key. Renders keep them, so new chat appends instead of
  // rebuilding the log, which would jump the scroll position and lose the browser's anchor.
  let rowNodes = new Map();
  // What every row's markup depends on (state, emote catalog, background); a change redraws.
  let shellKey = "";
  // A fresh window replaces the log and lands at its end without animating.
  let freshLog = true;
  // The pane's own glide to the end, while one is under way. Scroll events it causes pass rows
  // short of the bottom; they must not read as the viewer scrolling away.
  let glideFrame = 0;
  let glideTime = 0;
  let glideVelocity = 0;
  // Sub-pixel progress a display that rounds scrollTop to whole pixels would drop.
  let glideCarry = 0;
  let pollTimer = 0;
  // The backfill in progress, if any. It has its own request so polls never abort it.
  let backfill = null;
  let emoteCatalog = new Map();
  // Codes whose image failed stay text for this recording, even when the log rebuilds.
  let failedEmotes = new Set();
  // The catalog is requested once per recording load, at the first moment the open pane is
  // enabled; a failure leaves text and is not retried per message.
  let emoteAttempted = false;
  let emoteController = null;
  // A catalog that arrives after the log is visible must rebuild it exactly once.
  let emoteVersion = 0;
  // The pane's background, read once per render for the name-contrast check.
  let ground = null;

  function status(text, state = "") {
    if (el["chat-state"]) {
      el["chat-state"].textContent = text;
      el["chat-state"].dataset.state = state;
    }
  }

  function notice(text) {
    if (el["chat-notice"]) el["chat-notice"].textContent = text || "";
  }

  function applyOpen() {
    if (!root) return;
    root.dataset.open = open ? "true" : "false";
    if (el["chat-body"]) el["chat-body"].inert = !open;
    announce();
    if (open) {
      startPolling();
      maybeFetchEmotes();
    } else {
      stopPolling();
    }
  }

  // The player's Chat button mirrors this pane (player contract §5): it shows while a pane
  // exists and its pressed state follows `open`.
  function announce(available = true) {
    document.dispatchEvent(new CustomEvent("sk:chat", {
      detail: { available, open: available && open },
    }));
  }

  function isChannelOwn(message) {
    const channel = view?.channelLogin;
    return typeof channel === "string" && channel !== "" &&
      typeof message.login === "string" && message.login.toLowerCase() === channel.toLowerCase();
  }

  function maybeFetchEmotes() {
    if (disposed || !open || !view?.emotesEnabled || emoteAttempted) return;
    emoteAttempted = true;
    void fetchEmotes(view.recordingId);
  }

  async function fetchEmotes(recordingId) {
    if (typeof fetchImpl !== "function") return;
    const request = new AbortController();
    emoteController = request;
    try {
      const response = await fetchImpl(emotesUrl(recordingId), { signal: request.signal });
      if (!response.ok) throw new Error(`emotes ${response.status}`);
      const data = await response.json();
      if (disposed || request.signal.aborted || view?.recordingId !== recordingId) return;
      emoteCatalog = buildEmoteCatalog(data.available ? data.entries : []);
      emoteVersion++;
      render();
    } catch (error) {
      // A catalog failure is invisible by design; chat text remains the fallback.
      if (error?.name !== "AbortError") {
        console.debug("Stream Keepr: emote catalog fetch failed; chat stays text.", error);
      }
    } finally {
      if (emoteController === request) emoteController = null;
    }
  }

  /**
   * Fetches the window around the playhead. Single-flight and backed off after a failure,
   * unless `replace` is set: a seek or sync change supersedes the request in flight.
   */
  function loadCurrentWindow({ replace = false } = {}) {
    if (!open || !view) return;
    if (!replace && (loading || Date.now() < retryAt)) return;
    void fetchWindow(Math.max(0, currentTime - WINDOW_BEFORE_SEC),
      currentTime + WINDOW_AFTER_SEC, { fresh: true });
  }

  /**
   * Fetches the messages after what is already loaded, keeping the scrollback. A playhead that
   * has run too far past the loaded chat starts a fresh window instead, so a request never
   * exceeds the endpoint's window cap.
   */
  function continueWindow() {
    if (!open || !view || !loaded || loading || Date.now() < retryAt) return;
    if (currentTime > loadedTo + WINDOW_AFTER_SEC) {
      resetWindow();
      loadCurrentWindow({ replace: true });
      return;
    }
    void fetchWindow(Math.max(0, loadedToIsRow ? loadedTo : loadedTo - OVERLAP_SEC),
      Math.max(loadedTo, currentTime) + WINDOW_AFTER_SEC);
  }

  function resume() {
    if (loaded) continueWindow();
    else loadCurrentWindow();
  }

  function toggleAndPersist() {
    if (fixedOpen) return;
    setOpen(!open);
    persistOpen(open);
  }

  async function persistOpen(value) {
    try {
      await dotNet?.invokeMethodAsync("SetChatOpen", value);
    } catch (error) {
      // A closed circuit has no preference store left to update; the DOM already
      // reflects the user's action.
      console.debug("Stream Keepr: chat open state was not persisted.", error);
    }
  }

  function setOpen(next) {
    if (fixedOpen) {
      if (!open) {
        open = true;
        applyOpen();
        resume();
      }
      return;
    }

    const value = Boolean(next);
    if (open === value) return;
    open = value;
    applyOpen();
    if (open) resume();
  }

  function resetWindow() {
    stopBackfill();
    messages = new Map();
    gaps = [];
    loaded = false;
    loadedTo = 0;
    loadedToIsRow = false;
    endReached = false;
    windowFrom = 0;
    windowTo = 0;
    lastRenderKey = "";
    freshLog = true;
    following = true;
    newCount = 0;
  }

  async function load(next) {
    if (disposed || !next) return;
    const changed = view?.recordingId !== next.recordingId;
    const hadChat = view?.hasChat === true;
    view = next;
    fixedOpen = Boolean(next.fixedOpen);
    // A session's chat log can appear while the recording is live, but it never disappears.
    // Keep a true value when a stale summary payload says false, and keep status in step.
    if (!changed && hadChat) view = { ...next, hasChat: true };
    open = fixedOpen || Boolean(next.open);
    if (changed) {
      resetWindow();
      emoteController?.abort();
      emoteController = null;
      emoteCatalog = new Map();
      failedEmotes = new Set();
      emoteAttempted = false;
      emoteVersion++;
      syncOffset = validOffset(next.syncOffsetSec);
      status(next.hasChat ? "Loading chat…" : "No chat captured", next.hasChat ? "loading" : "empty");
      notice("");
    }
    applyOpen();
    updateStatus();
    // The same recording reloaded with another offset: apply it like a Settings change.
    if (!changed) setSyncOffset(next.syncOffsetSec);
    if (view.state === "complete" || view.state === "unavailable") stopPolling();
    if (open && (changed || !loaded)) loadCurrentWindow();
  }

  function startPolling() {
    if (pollTimer || !view || view.state === "complete" || view.state === "unavailable") return;
    pollTimer = window.setInterval(() => {
      if (!open || disposed || paused || document.hidden) return;
      if (!view || view.state === "complete" || view.state === "unavailable") return;
      continueWindow();
    }, POLL_MS);
  }

  function stopPolling() {
    if (!pollTimer) return;
    window.clearInterval(pollTimer);
    pollTimer = 0;
  }

  async function fetchWindow(fromSec, toSec, { fresh = false } = {}) {
    if (disposed || !view || !open || typeof fetchImpl !== "function") return;
    const from = Math.max(0, Number(fromSec) || 0);
    const to = Math.max(from + 1, Number(toSec) || from + WINDOW_AFTER_SEC);
    controller?.abort();
    controller = new AbortController();
    if (fresh) {
      windowFrom = from;
      windowTo = to;
    }
    const playheadAtRequest = currentTime;
    const request = controller;
    loading = true;
    el["chat-log"]?.setAttribute("aria-busy", "true");

    try {
      const data = await requestChat(from, to, request.signal);
      if (disposed || request.signal.aborted) return;

      notice("");
      view.state = data.state;
      view.hasChat = Boolean(data.hasChat);
      retryAt = 0;
      retryDelay = RETRY_MIN_MS;

      const received = data.messages || [];
      const added = mergeChat(received, data.gaps);

      // A `starting` or `unavailable` timeline returns no messages, so it proves nothing loaded.
      const readable = data.state !== "starting" && data.state !== "unavailable";
      if (readable) {
        // The server echoes the requested toSec, and a window hit by the limit ends at its
        // last row; either way, nothing past that end is known yet.
        const truncated = received.length >= view.limit;
        const lastRow = truncated ? Number(received.at(-1).mt) : NaN;
        let end = truncated ? lastRow : Number(data.toSec);
        // While live, messages ahead of the playhead may still be written; poll for them again.
        if (liveState()) end = Math.min(end, playheadAtRequest);
        if (Number.isFinite(end) && end >= loadedTo) {
          loadedTo = end;
          loadedToIsRow = end === lastRow;
        }
        // The server clamps toSec to the recording's duration, so a short window is the end.
        endReached = !liveState() && !truncated && Number(data.toSec) < to - 0.001;
      }

      loaded = true;
      if (!following && added > 0) {
        newCount += added;
        updateJump();
      }
      updateStatus();
      render();
      if (fresh && readable && data.hasMoreBefore && from > 0) void backfillBefore(from);
    } catch (error) {
      if (error?.name !== "AbortError" && !request.signal.aborted) {
        retryAt = Date.now() + retryDelay;
        retryDelay = Math.min(retryDelay * 2, RETRY_MAX_MS);
        status("Chat unavailable", "unavailable");
        notice("Chat could not be loaded.");
        console.debug("Stream Keepr: chat window fetch failed; retrying later.", error);
      }
    } finally {
      if (controller === request) {
        loading = false;
        controller = null;
        el["chat-log"]?.removeAttribute("aria-busy");
      }
    }
  }

  async function requestChat(from, to, signal) {
    const url = apiUrl(view.recordingId);
    url.searchParams.set("fromSec", from.toFixed(3));
    url.searchParams.set("toSec", to.toFixed(3));
    url.searchParams.set("limit", String(view.limit));
    url.searchParams.set("syncOffsetSec", syncOffset.toFixed(3));
    const response = await fetchImpl(url, { signal });
    if (!response.ok) throw new Error(`chat ${response.status}`);
    return response.json();
  }

  /** Adds messages and gaps to the log's data; returns how many messages are new. */
  function mergeChat(received, newGaps) {
    let added = 0;
    for (const message of received) {
      const key = messageKey(message);
      if (!messages.has(key)) added++;
      messages.set(key, message);
    }
    for (const gap of newGaps || []) {
      if (!gaps.some((item) => item.from === gap.from && item.to === gap.to)) gaps.push(gap);
    }
    gaps.sort((a, b) => a.from - b.from);
    return added;
  }

  function logFull() {
    const log = el["chat-log"];
    return Boolean(log) && log.scrollHeight > log.clientHeight;
  }

  /**
   * Fills the log with chat from before a fresh window, newest chunk first. Stops once the log
   * overflows, the chat or recording starts, it reaches BACKFILL_MAX_SEC back, or a chunk hits
   * the limit: the limit keeps a chunk's earliest rows, so a busy chunk would leave a hole, and
   * a busy chat already fills the log. A failure just ends the backfill.
   */
  async function backfillBefore(start) {
    stopBackfill();
    const job = { controller: new AbortController() };
    backfill = job;
    let to = start;
    try {
      while (to > 0 && start - to < BACKFILL_MAX_SEC && !logFull()) {
        if (disposed || !open || !view) return;
        const from = Math.max(0, to - BACKFILL_CHUNK_SEC);
        const data = await requestChat(from, to, job.controller.signal);
        if (backfill !== job) return;
        const received = data.messages || [];
        if (received.length >= view.limit) return;
        mergeChat(received, data.gaps);
        windowFrom = Math.min(windowFrom, from);
        render();
        if (!data.hasMoreBefore) return;
        to = from;
      }
    } catch (error) {
      if (error?.name !== "AbortError") {
        console.debug("Stream Keepr: chat backfill stopped after a failed fetch.", error);
      }
    } finally {
      if (backfill === job) backfill = null;
    }
  }

  function stopBackfill() {
    backfill?.controller.abort();
    backfill = null;
  }

  function liveState() {
    return isLiveState(view?.state);
  }

  function messageKey(message) {
    return message.id || `${message.at}-${message.login}-${message.text}`;
  }

  function updateStatus() {
    if (!view) return;
    if (view.state === "unavailable") {
      status("Chat unavailable", "unavailable");
      return;
    }
    if (view.state === "starting") {
      // The timeline can be briefly unreadable during a live part transition. Once a chat
      // log exists the connection is up; do not call that "waiting".
      status(view.hasChat === true ? "Chat connected" : "Waiting for chat…",
        view.hasChat === true ? "live" : "starting");
      return;
    }
    if (view.hasChat === false) {
      status(liveState() ? "Waiting for chat…" : "No chat captured",
        liveState() ? "starting" : "empty");
      return;
    }
    if (liveState()) {
      status("Chat connected", "live");
      return;
    }
    status("Chat replay", "complete");
  }

  function updateJump() {
    if (!el["chat-jump"]) return;
    el["chat-jump"].hidden = following || newCount === 0;
    el["chat-jump"].textContent = newCount > 0
      ? `Jump to current time (${newCount})`
      : "Jump to current time";
  }

  function nearBottom() {
    const log = el["chat-log"];
    return log.scrollHeight - log.scrollTop - log.clientHeight < 24;
  }

  function onScroll() {
    // Only the viewer's own input (onScrollIntent) stops following mid-glide.
    if (glideFrame !== 0) return;
    const atBottom = nearBottom();
    if (atBottom !== following) {
      following = atBottom;
      if (following) newCount = 0;
      updateJump();
    }
  }

  /** Wheel up, touch, pointer or key input on the log hands scrolling back to the viewer. */
  function onScrollIntent(event) {
    // Clicks on rows (names, text selection) and keys that do not scroll up keep following;
    // a press on the log itself is its scrollbar.
    if (event.type === "wheel" && event.deltaY >= 0) return;
    if (event.type === "keydown" && !SCROLL_UP_KEYS.has(event.key) &&
      !(event.key === " " && event.shiftKey)) return;
    if (event.type === "pointerdown" && event.target !== el["chat-log"]) return;
    stopGlide();
  }

  function prefersReducedMotion() {
    return globalThis.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches === true;
  }

  /**
   * Scrolls the log to its newest row. "smooth" glides unless the viewer prefers reduced
   * motion or the end is more than a screen away, where a glide would sweep through old chat
   * (a burst, or a tab that was throttled); "instant" is for a fresh window or a seek.
   */
  function scrollToEnd(behavior) {
    const log = el["chat-log"];
    if (!log) return;
    const distance = log.scrollHeight - log.clientHeight - log.scrollTop;
    if (behavior !== "smooth" || prefersReducedMotion() || distance > log.clientHeight) {
      stopGlide();
      log.scrollTop = log.scrollHeight;
      return;
    }
    // A glide under way keeps its speed; the new rows only move its end.
    if (glideFrame === 0) glideFrame = requestAnimationFrame(glide);
  }

  /** One frame of the glide: a critically damped spring, so it eases in and never overshoots. */
  function glide(now) {
    const log = el["chat-log"];
    const dt = glideTime === 0 ? 1 / 60 : Math.min((now - glideTime) / 1000, GLIDE_MAX_STEP_SEC);
    glideTime = now;
    const top = log.scrollTop + glideCarry;
    // Read each frame: pruned rows shift scrollTop and the end together, and new rows move the end.
    const distance = log.scrollHeight - log.clientHeight - top;
    glideVelocity += (GLIDE_RATE * GLIDE_RATE * distance - 2 * GLIDE_RATE * glideVelocity) * dt;
    const step = Math.max(glideVelocity * dt, 0);
    if (distance < 0.5 || step >= distance) {
      stopGlide();
      log.scrollTop = log.scrollHeight;
      return;
    }
    log.scrollTop = top + step;
    glideCarry = top + step - log.scrollTop;
    glideFrame = requestAnimationFrame(glide);
  }

  function stopGlide() {
    if (glideFrame !== 0) cancelAnimationFrame(glideFrame);
    glideFrame = 0;
    glideTime = 0;
    glideVelocity = 0;
    glideCarry = 0;
  }

  function jumpToCurrent() {
    following = true;
    newCount = 0;
    updateJump();
    // Up to MAX_ROWS of scrollback may lie between; an animation would sweep through it all.
    scrollToEnd("instant");
  }

  /** Draws the log. `instant`: a seek within the kept window lands without animating. */
  function render({ instant = false } = {}) {
    const log = el["chat-log"];
    if (!log) return;
    let visible = [...messages.values()]
      .filter((message) => Number(message.mt) <= currentTime + 0.5)
      .sort((a, b) => a.mt - b.mt || String(a.id).localeCompare(String(b.id)));
    if (visible.length > MAX_ROWS) {
      // Drop the oldest scrollback. A seek back before what is kept fetches a fresh window.
      for (const message of visible.slice(0, visible.length - MAX_ROWS)) {
        messages.delete(messageKey(message));
      }
      visible = visible.slice(-MAX_ROWS);
      windowFrom = Math.max(windowFrom, Number(visible[0].mt));
    }
    // A gap shows once the playhead reaches it, as a message does. One shown early would sit
    // below the newest row, and every new row would then go in above it instead of at the end.
    const reached = gaps.filter((gap) => Number(gap.from) <= currentTime + 0.5);
    const key = visible.map((message) => message.id).join("|") + "|" +
      reached.map((gap) => `${gap.from}:${gap.to}`).join("|") + "|" +
      (view?.state ?? "") + "|" + (view?.hasChat === false ? "empty" : "") +
      `|emotes:${emoteVersion}`;
    if (key === lastRenderKey) return;
    lastRenderKey = key;

    ground = opaqueGround(root);
    const shell = [view?.state, view?.hasChat, view?.channelLogin, view?.channelColor,
      emoteVersion, ground].join("|");
    const rebuild = freshLog || shell !== shellKey || rowNodes.size === 0;
    freshLog = false;
    shellKey = shell;
    if (rebuild) {
      rowNodes = new Map();
      log.replaceChildren();
    }

    if (visible.length === 0) {
      rowNodes = new Map();
      if (view?.state === "starting" || (liveState() && view?.hasChat === false)) {
        log.textContent = "Waiting for the first chat message…";
      } else if (view?.hasChat === false) {
        log.textContent = "Chat was not captured for this recording.";
      } else {
        log.textContent = "No chat at this point.";
      }
      return;
    }

    // The log in order, as [key, create] pairs; gaps sit where they fall among the messages.
    const items = [];
    const pushGap = (gap) => items.push([`g:${gap.from}:${gap.to}`, () => renderGap(gap)]);
    const gapQueue = [...reached];
    for (const message of visible) {
      while (gapQueue.length > 0 && gapQueue[0].to <= message.mt) pushGap(gapQueue.shift());
      if (gapQueue.length > 0 && gapQueue[0].from <= message.mt) pushGap(gapQueue.shift());
      items.push([`m:${messageKey(message)}`, () => renderMessage(message)]);
    }
    while (gapQueue.length > 0) pushGap(gapQueue.shift());

    const wanted = new Set(items.map(([itemKey]) => itemKey));
    for (const [itemKey, node] of rowNodes) {
      if (!wanted.has(itemKey)) {
        node.remove();
        rowNodes.delete(itemKey);
      }
    }
    // Walk the log once: kept nodes in place are skipped, so playing on only appends at the end
    // and drops pruned rows from the top. `appended` is a new row after every kept one;
    // `inserted` is one above a kept row, as backfill adds older chat.
    let cursor = log.firstChild;
    let appended = false;
    let inserted = false;
    for (const [itemKey, create] of items) {
      let node = rowNodes.get(itemKey);
      if (!node) {
        node = create();
        rowNodes.set(itemKey, node);
        if (cursor === null) appended = true;
        else inserted = true;
      }
      if (node === cursor) {
        cursor = cursor.nextSibling;
      } else {
        log.insertBefore(node, cursor);
      }
    }

    // Only new chat at the end animates. Older rows above (backfill) would otherwise leave a
    // following view on the oldest rows, since anchoring holds no anchor at the top.
    if (following && (rebuild || appended || inserted)) {
      scrollToEnd(rebuild || inserted || instant ? "instant" : "smooth");
    }
    updateJump();
  }

  function renderGap(gap) {
    // `seconds` is wall-clock time; a gap between parts is a single point on the media timeline.
    const seconds = Math.max(1, Math.round(Number(gap.seconds)));
    const row = document.createElement("div");
    row.className = "sk-chat-gap";
    row.textContent = `Chat unavailable for ${seconds}s`;
    return row;
  }

  function renderMessage(message) {
    const row = document.createElement("div");
    row.className = "sk-chat-row";
    row.dataset.messageId = message.id || "";
    row.dataset.mt = String(message.mt);
    if (message.action) row.classList.add("action");

    if (message.reply?.text) {
      const reply = document.createElement("div");
      reply.className = "sk-chat-reply";
      const who = message.reply.display || message.reply.login || "";
      reply.textContent = `↳ ${who}: ${message.reply.text}`;
      row.append(reply);
    }

    for (const badge of message.badges || []) {
      const label = BADGE_LABELS[badge.set] || badge.set;
      const chip = document.createElement("span");
      chip.className = "sk-chat-badge";
      chip.textContent = label;
      chip.title = badge.info ? `${label} ${badge.info}` : label;
      row.append(chip);
    }

    const name = document.createElement("span");
    name.className = "sk-chat-name";
    name.dataset.chatName = "";
    name.textContent = message.display || message.login || "unknown";
    const channelColor = Number(view?.channelColor);
    if (isChannelOwn(message) && Number.isInteger(channelColor) && channelColor > 0) {
      // The streamer's own messages take the channel colour the drawer shows (ChannelColor).
      name.dataset.channelColor = String(channelColor);
      name.style.color = `var(--channel-${channelColor})`;
    } else {
      const color = readableNameColor(message.color, ground);
      if (color) name.style.color = color;
    }
    row.append(name);

    const colon = document.createElement("span");
    colon.className = "sk-chat-colon";
    colon.textContent = ": ";
    row.append(colon);

    const text = document.createElement("span");
    text.className = "sk-chat-text";
    appendText(text, message.text || "", message.emotes || []);
    row.append(text);
    return row;
  }

  function appendText(parent, value, emotes) {
    // matchEmotes validates ranges against the code-point array, so the bounds check exists once.
    const ranges = [];
    for (const emote of emotes) {
      for (const range of emote.ranges || []) {
        if (!range) continue;
        ranges.push({ from: range.from, to: range.to, id: emote.id });
      }
    }

    for (const segment of matchEmotes(value, ranges, emoteCatalog)) {
      if (segment.type === "emote") {
        parent.append(emoteImage(segment.entry, segment.text));
      } else if (segment.type === "native") {
        parent.append(nativeEmoteImage(segment.id, segment.text));
      } else {
        parent.append(document.createTextNode(segment.text));
      }
    }
  }

  /** The shared alt/title and lazy/async attributes for native and third-party emotes. */
  function applyEmoteImageAttributes(image, { src, srcset, code }) {
    image.src = src;
    if (srcset) image.setAttribute("srcset", srcset);
    image.alt = code;
    image.title = code;
    image.setAttribute("loading", "lazy");
    image.setAttribute("decoding", "async");
  }

  function nativeEmoteImage(id, name) {
    if (typeof id !== "string" || !/^[A-Za-z0-9_]+$/.test(id)) {
      return document.createTextNode(name);
    }
    const image = document.createElement("img");
    const base = `https://static-cdn.jtvnw.net/emoticons/v2/${encodeURIComponent(id)}/default/dark`;
    applyEmoteImageAttributes(image, {
      src: `${base}/1.0`,
      srcset: `${base}/1.0 1x, ${base}/2.0 2x, ${base}/3.0 3x`,
      code: name,
    });
    return image;
  }

  function safeSrcSet(value) {
    if (typeof value !== "string") return "";
    const candidates = value.split(",").map((candidate) => candidate.trim()).filter(Boolean);
    if (candidates.length === 0) return "";
    for (const candidate of candidates) {
      if (!isHttpsUrl(candidate.split(/\s+/)[0])) return "";
    }
    return candidates.join(", ");
  }

  function emoteImage(entry, code) {
    if (failedEmotes.has(code) || !isHttpsUrl(entry?.src)) return document.createTextNode(code);
    const image = document.createElement("img");
    applyEmoteImageAttributes(image, {
      src: entry.src,
      srcset: safeSrcSet(entry.srcset),
      code,
    });
    image.setAttribute("referrerpolicy", "no-referrer");
    // The error handler replaces the image with this code; the row never retries it.
    image.dataset.emoteCode = code;
    return image;
  }

  function onEmoteError(event) {
    const image = event.target;
    const code = image?.dataset?.emoteCode;
    if (!code) return;
    failedEmotes.add(code);
    image.replaceWith(document.createTextNode(code));
  }

  function onPlayhead(event) {
    const detail = event.detail || {};
    if (!view || detail.recordingId !== view.recordingId) return;
    const previous = currentTime;
    currentTime = Number.isFinite(detail.time) ? detail.time : 0;
    paused = Boolean(detail.paused);

    if (!loaded) {
      if (currentTime < windowFrom || currentTime > windowTo) {
        // A seek away from the window being loaded.
        resetWindow();
        loadCurrentWindow({ replace: true });
      } else {
        loadCurrentWindow();
      }
      return;
    }

    // Playing on keeps the scrollback; a seek before it, back, or far ahead starts over.
    // Polls never move windowFrom past the playhead, so they cannot trigger this.
    const backward = currentTime < previous - SEEK_SEC;
    const farForward = currentTime > previous + WINDOW_AFTER_SEC;
    if (backward || farForward || currentTime < windowFrom) {
      resetWindow();
      loadCurrentWindow({ replace: true });
      return;
    }

    render({ instant: currentTime > previous + SEEK_SEC });
    // A live pane polls on a timer; a complete one reads ahead as the playhead nears the end.
    if (!liveState() && !endReached && currentTime + LOOKAHEAD_SEC > loadedTo) continueWindow();
  }

  /** An offset within the endpoint's limit; anything else is 0. */
  function validOffset(value) {
    const number = Number(value);
    const max = view?.maxSyncOffsetSec ?? 0;
    return Number.isFinite(number) ? clamp(number, -max, max) : 0;
  }

  /**
   * Applies the viewer's chat sync offset (Settings › Chat): chat is refetched around the
   * playhead with it. The same offset again fetches nothing.
   */
  function setSyncOffset(value) {
    if (disposed || !view) return;
    const next = validOffset(value);
    if (next === syncOffset) return;
    syncOffset = next;
    resetWindow();
    // A closed pane fetches nothing now; opening it loads a fresh window with this offset.
    loadCurrentWindow({ replace: true });
  }

  on(el["chat-log"], "scroll", onScroll, { passive: true });
  for (const type of ["wheel", "touchmove", "pointerdown", "keydown"]) {
    on(el["chat-log"], type, onScrollIntent, { passive: true });
  }
  // Image errors do not bubble, so capture them at the log.
  on(el["chat-log"], "error", onEmoteError, { capture: true });
  on(el["chat-jump"], "click", jumpToCurrent);
  on(document, "sk:playhead", onPlayhead);
  on(document, "sk:chat-toggle", toggleAndPersist);
  on(document, "keydown", (event) => {
    // Holding C must not flap the pane.
    if (fixedOpen || event.repeat || event.key.toLowerCase() !== "c") return;
    if (shortcutBlocked(event, document)) return;
    event.preventDefault();
    toggleAndPersist();
  });
  on(document, "visibilitychange", () => {
    if (!document.hidden && open && !paused) resume();
  });

  applyOpen();

  return {
    load,
    setOpen,
    setSyncOffset,
    dispose() {
      if (disposed) return;
      disposed = true;
      controller?.abort();
      emoteController?.abort();
      stopBackfill();
      stopPolling();
      stopGlide();
      listeners.abort();
      announce(false);
      rowNodes = new Map();
      if (el["chat-log"]) el["chat-log"].replaceChildren();
    },
  };
}

const panes = new WeakMap();

export async function init(root, self, view) {
  if (!root) return;
  panes.get(root)?.dispose();
  const pane = createChatPane(root, { dotNet: self });
  panes.set(root, pane);
  await pane.load(view);
}

export async function load(root, view) {
  await panes.get(root)?.load(view);
}

export function setOpen(root, open) {
  panes.get(root)?.setOpen(open);
}

export function setSyncOffset(root, offset) {
  panes.get(root)?.setSyncOffset(offset);
}

export function dispose(root) {
  panes.get(root)?.dispose();
  panes.delete(root);
}
