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
// Rendered rows, not the fetch limit; the server's limits arrive in the load payload.
const MAX_ROWS = 300;
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

function writeOffset(storage, recordingId, value) {
  try {
    const offsets = readOffsets(storage);
    offsets[recordingId] = value;
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
  let loadedTo = 0;
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
      currentTime + WINDOW_AFTER_SEC);
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
    if (open) loadCurrentWindow();
  }

  function resetWindow() {
    messages = new Map();
    gaps = [];
    loaded = false;
    loadedTo = 0;
    lastRenderKey = "";
    following = true;
    newCount = 0;
  }

  async function load(next) {
    if (disposed || !next) return;
    const changed = view?.recordingId !== next.recordingId;
    view = next;
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
      if (!open || disposed || paused || document.hidden || loading) return;
      if (Date.now() < retryAt) return;
      if (!view || view.state === "complete" || view.state === "unavailable") return;
      const from = Math.max(0, loadedTo - 5);
      const to = Math.max(currentTime + 10, currentTime + WINDOW_AFTER_SEC);
      if (to <= from) return;
      void fetchWindow(from, to);
    }, POLL_MS);
  }

  function stopPolling() {
    if (!pollTimer) return;
    window.clearInterval(pollTimer);
    pollTimer = 0;
  }

  async function fetchWindow(fromSec, toSec) {
    if (disposed || !view || !open || typeof fetchImpl !== "function") return;
    const from = Math.max(0, Number(fromSec) || 0);
    const to = Math.max(from + 1, Number(toSec) || from + WINDOW_AFTER_SEC);
    controller?.abort();
    controller = new AbortController();
    const request = controller;
    loading = true;
    el["chat-log"]?.setAttribute("aria-busy", "true");

    const url = apiUrl(view.recordingId);
    url.searchParams.set("fromSec", from.toFixed(3));
    url.searchParams.set("toSec", to.toFixed(3));
    url.searchParams.set("limit", String(view.limit));
    url.searchParams.set("syncOffsetSec", syncOffset.toFixed(3));

    try {
      const response = await fetchImpl(url, { signal: request.signal });
      if (!response.ok) throw new Error(`chat ${response.status}`);
      const data = await response.json();
      if (disposed || request.signal.aborted) return;

      notice("");
      view.state = data.state;
      view.hasChat = Boolean(data.hasChat);
      retryAt = 0;
      retryDelay = RETRY_MIN_MS;
      loadedTo = Math.max(loadedTo, data.toSec);

      let added = 0;
      for (const message of data.messages || []) {
        const key = message.id || `${message.at}-${message.login}-${message.text}`;
        if (!messages.has(key)) added++;
        messages.set(key, message);
      }
      for (const gap of data.gaps || []) {
        if (!gaps.some((item) => item.from === gap.from && item.to === gap.to)) gaps.push(gap);
      }
      gaps.sort((a, b) => a.from - b.from);

      // Keep a bounded replay window; a 12-hour live session must not grow the DOM or map forever.
      for (const [key, message] of messages) {
        if (message.mt < currentTime - 300) messages.delete(key);
      }
      if (messages.size > 2000) {
        messages = new Map([...messages].sort((a, b) => a[1].mt - b[1].mt).slice(-2000));
      }

      loaded = true;
      if (!following && added > 0) {
        newCount += added;
        updateJump();
      }
      updateStatus();
      updateSync();
      render();
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

  function liveState() {
    return view?.state === "recording" || view?.state === "reconnecting";
  }

  function updateStatus() {
    if (!view) return;
    if (view.state === "unavailable") {
      status("Chat unavailable", "unavailable");
      return;
    }
    if (view.state === "starting") {
      status("Waiting for chat…", "starting");
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
    const visible = [...messages.values()]
      .filter((message) => Number(message.mt) <= currentTime + 0.5)
      .sort((a, b) => a.mt - b.mt || String(a.id).localeCompare(String(b.id)));
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

    const rows = visible.slice(-MAX_ROWS);
    const gapQueue = [...gaps];
    for (const message of rows) {
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
    const seconds = Math.max(1, Math.round(Number(gap.seconds ?? gap.to - gap.from)));
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
      loadCurrentWindow();
      return;
    }

    const backward = currentTime < previous - 2;
    const farForward = currentTime > previous + WINDOW_AFTER_SEC;
    if (backward || farForward) {
      resetWindow();
      loadCurrentWindow({ replace: true });
      return;
    }

    render();
  }

  function setSync(value) {
    if (!view) return;
    syncOffset = clamp(value, -view.maxSyncOffsetSec, view.maxSyncOffsetSec);
    writeOffset(storage, view.recordingId, syncOffset);
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
    if (!document.hidden && open && !paused) loadCurrentWindow();
  });

  applyOpen();

  return {
    load,
    setOpen,
    dispose() {
      if (disposed) return;
      disposed = true;
      controller?.abort();
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
