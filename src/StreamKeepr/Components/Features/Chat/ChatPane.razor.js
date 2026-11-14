// Chat replay pane. Blazor renders the skeleton once; this module owns the log, polling,
// auto-scroll, sync controls, and the C shortcut. The player owns the <video> element.
import { shortcutBlocked } from "/js/player-keys.js";

const HOOKS = [
  "chat-toggle", "chat-state", "chat-body", "chat-log", "chat-sync", "chat-sync-value",
  "chat-sync-back2", "chat-sync-back1", "chat-sync-reset", "chat-sync-forward1",
  "chat-sync-forward2", "chat-jump", "chat-notice",
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

function formatOffset(value) {
  const sign = value > 0 ? "+" : "";
  return `${sign}${value.toFixed(1)} s`;
}

function validColor(value) {
  return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value) ? value : null;
}

function readOffsets(storage) {
  try {
    const parsed = JSON.parse(storage?.getItem("sk-chat") || "null");
    return parsed?.v === 1 && parsed.offsets && typeof parsed.offsets === "object"
      ? parsed.offsets
      : {};
  } catch {
    return {};
  }
}

function readOffset(storage, recordingId, maxOffset) {
  const offsets = readOffsets(storage);
  const value = Number(offsets[recordingId]);
  return Number.isFinite(value) ? clamp(value, -maxOffset, maxOffset) : 0;
}

function writeOffset(storage, recordingId, value, maxOffset) {
  try {
    const offsets = readOffsets(storage);
    offsets[recordingId] = clamp(value, -maxOffset, maxOffset);
    const entries = Object.entries(offsets).slice(-100);
    storage?.setItem("sk-chat", JSON.stringify({ v: 1, offsets: Object.fromEntries(entries) }));
  } catch {
    // A disabled or full localStorage must not break replay.
  }
}

function apiUrl(recordingId) {
  return new URL(
    `/api/recordings/${encodeURIComponent(recordingId)}/chat`,
    window.location.origin,
  );
}

export function createChatPane(root, {
  dotNet = null,
  fetch: fetchImpl = globalThis.fetch?.bind(globalThis),
  storage = globalThis.localStorage,
} = {}) {
  const el = Object.fromEntries(HOOKS.map((name) => [name, root.querySelector(`[data-sk="${name}"]`)]));
  const listeners = new AbortController();
  const on = (target, type, handler, options = {}) =>
    target?.addEventListener(type, handler, { signal: listeners.signal, ...options });

  let view = null;
  let open = false;
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
  let pollTimer = 0;
  // The backfill in progress, if any. It has its own request so polls never abort it.
  let backfill = null;

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
    el["chat-toggle"]?.setAttribute("aria-expanded", open ? "true" : "false");
    el["chat-toggle"]?.setAttribute("aria-label", open ? "Collapse chat" : "Open chat");
    if (el["chat-body"]) el["chat-body"].inert = !open;
    if (open) startPolling();
    else stopPolling();
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
    following = true;
    newCount = 0;
  }

  async function load(next) {
    if (disposed || !next) return;
    const changed = view?.recordingId !== next.recordingId;
    const hadChat = view?.hasChat === true;
    view = next;
    // A session's chat log can appear while the recording is live, but it never disappears.
    // Keep a true value when a stale summary payload says false, and keep status in step.
    if (!changed && hadChat) view = { ...next, hasChat: true };
    open = Boolean(next.open);
    if (changed) {
      resetWindow();
      syncOffset = readOffset(storage, next.recordingId, next.maxSyncOffsetSec);
      updateSync();
      status(next.hasChat ? "Loading chat…" : "No chat captured", next.hasChat ? "loading" : "empty");
      notice("");
    }
    applyOpen();
    updateStatus();
    updateSync();
    if (view.state === "complete" || view.state === "unavailable") stopPolling();
    if (open && (changed || !loaded)) loadCurrentWindow();
  }

  function updateSync() {
    if (el["chat-sync"]) el["chat-sync"].hidden = !view?.hasChat;
    if (el["chat-sync-value"]) el["chat-sync-value"].textContent = formatOffset(syncOffset);
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
      updateSync();
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
    return view?.state === "recording" || view?.state === "reconnecting";
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

  function onScroll() {
    const nearBottom = el["chat-log"].scrollHeight - el["chat-log"].scrollTop -
      el["chat-log"].clientHeight < 24;
    if (nearBottom !== following) {
      following = nearBottom;
      if (following) newCount = 0;
      updateJump();
    }
  }

  function jumpToCurrent() {
    following = true;
    newCount = 0;
    updateJump();
    if (el["chat-log"]) el["chat-log"].scrollTop = el["chat-log"].scrollHeight;
  }

  function render() {
    if (!el["chat-log"]) return;
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
    const key = visible.map((message) => message.id).join("|") + "|" +
      gaps.map((gap) => `${gap.from}:${gap.to}`).join("|") + "|" +
      (view?.state ?? "") + "|" + (view?.hasChat === false ? "empty" : "");
    if (key === lastRenderKey) return;
    lastRenderKey = key;

    const log = el["chat-log"];
    log.replaceChildren();
    if (visible.length === 0) {
      if (view?.state === "starting" || (liveState() && view?.hasChat === false)) {
        log.textContent = "Waiting for the first chat message…";
      } else if (view?.hasChat === false) {
        log.textContent = "Chat was not captured for this recording.";
      } else {
        log.textContent = "No chat at this point.";
      }
      return;
    }

    const gapQueue = [...gaps];
    for (const message of visible) {
      while (gapQueue.length > 0 && gapQueue[0].to <= message.mt) {
        appendGap(gapQueue.shift());
      }
      if (gapQueue.length > 0 && gapQueue[0].from <= message.mt) {
        appendGap(gapQueue.shift());
      }
      log.append(renderMessage(message));
    }
    while (gapQueue.length > 0) appendGap(gapQueue.shift());

    if (following) log.scrollTop = log.scrollHeight;
    updateJump();
  }

  function appendGap(gap) {
    // `seconds` is wall-clock time; a gap between parts is a single point on the media timeline.
    const seconds = Math.max(1, Math.round(Number(gap.seconds)));
    const row = document.createElement("div");
    row.className = "sk-chat-gap";
    row.textContent = `Chat unavailable for ${seconds}s`;
    el["chat-log"].append(row);
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
    name.textContent = message.display || message.login || "unknown";
    const color = validColor(message.color);
    if (color) name.style.color = color;
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
    const chars = Array.from(value);
    const ranges = [];
    for (const emote of emotes) {
      for (const range of emote.ranges || []) {
        if (range.from >= 0 && range.to >= range.from && range.to < chars.length) {
          ranges.push({ from: range.from, to: range.to, id: emote.id });
        }
      }
    }
    ranges.sort((a, b) => a.from - b.from);

    let index = 0;
    for (const range of ranges) {
      if (range.from < index) continue;
      if (range.from > index) {
        parent.append(document.createTextNode(chars.slice(index, range.from).join("")));
      }
      const name = chars.slice(range.from, range.to + 1).join("");
      if (/^[A-Za-z0-9_]+$/.test(range.id)) {
        const image = document.createElement("img");
        const base = `https://static-cdn.jtvnw.net/emoticons/v2/${encodeURIComponent(range.id)}/default/dark`;
        image.src = `${base}/1.0`;
        image.srcset = `${base}/1.0 1x, ${base}/2.0 2x, ${base}/3.0 3x`;
        image.alt = name;
        image.title = name;
        image.loading = "lazy";
        image.decoding = "async";
        parent.append(image);
      } else {
        parent.append(document.createTextNode(name));
      }
      index = range.to + 1;
    }

    if (index < chars.length) {
      parent.append(document.createTextNode(chars.slice(index).join("")));
    }
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
    const backward = currentTime < previous - 2;
    const farForward = currentTime > previous + WINDOW_AFTER_SEC;
    if (backward || farForward || currentTime < windowFrom) {
      resetWindow();
      loadCurrentWindow({ replace: true });
      return;
    }

    render();
    // A live pane polls on a timer; a complete one reads ahead as the playhead nears the end.
    if (!liveState() && !endReached && currentTime + LOOKAHEAD_SEC > loadedTo) continueWindow();
  }

  function setSync(value) {
    if (!view) return;
    syncOffset = clamp(value, -view.maxSyncOffsetSec, view.maxSyncOffsetSec);
    writeOffset(storage, view.recordingId, syncOffset, view.maxSyncOffsetSec);
    updateSync();
    resetWindow();
    loadCurrentWindow({ replace: true });
  }

  on(el["chat-toggle"], "click", toggleAndPersist);
  on(el["chat-log"], "scroll", onScroll, { passive: true });
  on(el["chat-jump"], "click", jumpToCurrent);
  on(el["chat-sync-back2"], "click", () => setSync(syncOffset - 2));
  on(el["chat-sync-back1"], "click", () => setSync(syncOffset - 1));
  on(el["chat-sync-reset"], "click", () => setSync(0));
  on(el["chat-sync-forward1"], "click", () => setSync(syncOffset + 1));
  on(el["chat-sync-forward2"], "click", () => setSync(syncOffset + 2));
  on(document, "sk:playhead", onPlayhead);
  on(document, "keydown", (event) => {
    // Holding C must not flap the pane.
    if (event.repeat || event.key.toLowerCase() !== "c") return;
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
    dispose() {
      if (disposed) return;
      disposed = true;
      controller?.abort();
      stopBackfill();
      stopPolling();
      listeners.abort();
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

export function dispose(root) {
  panes.get(root)?.dispose();
  panes.delete(root);
}
