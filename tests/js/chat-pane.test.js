import { afterEach, describe, expect, it, vi } from "vitest";
import {
  buildEmoteCatalog,
  createChatPane,
  matchEmotes,
} from "../../src/StreamKeepr/Components/Features/Chat/ChatPane.razor.js";

const KEKW = {
  provider: "bttv",
  id: "5ff36d0a95e8e11f4f6f0a1c",
  code: "KEKW",
  animated: false,
  src: "https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/1x.webp",
  srcset: "https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/1x.webp 1x, " +
    "https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/2x.webp 2x, " +
    "https://cdn.betterttv.net/emote/5ff36d0a95e8e11f4f6f0a1c/3x.webp 3x",
};

const emoteCodes = (segments) =>
  segments.filter((segment) => segment.type === "emote").map((segment) => segment.text);

const MARKUP = `
  <aside class="sk-chat" data-sk-chat data-open="true">
    <header class="sk-chat-head">
      <button type="button" data-sk="chat-toggle" aria-expanded="true" aria-label="Collapse chat">Chat</button>
      <span data-sk="chat-state" role="status"></span>
    </header>
    <div data-sk="chat-body">
      <div data-sk="chat-log" role="log" aria-live="off" tabindex="0"></div>
      <div>
        <div data-sk="chat-sync" hidden>
          <button data-sk="chat-sync-back2"></button><button data-sk="chat-sync-back1"></button>
          <button data-sk="chat-sync-reset"></button><button data-sk="chat-sync-forward1"></button>
          <button data-sk="chat-sync-forward2"></button><span data-sk="chat-sync-value"></span>
        </div>
        <button data-sk="chat-jump" hidden></button>
        <span data-sk="chat-notice"></span>
      </div>
    </div>
  </aside>`;

const message = {
  id: "m1",
  mt: 5,
  at: 1794160805000,
  uid: "42",
  login: "alice",
  display: "Alice",
  color: "#00FF00",
  badges: [{ set: "moderator", version: "1", info: null }],
  text: "hello Kappa",
  emotes: [{ id: "25", ranges: [{ from: 6, to: 10 }] }],
  bits: null,
  reply: null,
  action: false,
};

function storage() {
  const data = new Map();
  return {
    getItem: (key) => data.get(key) ?? null,
    setItem: (key, value) => data.set(key, value),
  };
}

function fakeFetch(payload = message) {
  return vi.fn(async (url) => ({
    ok: true,
    json: async () => ({
      v: 1,
      recordingId: "r1",
      state: "complete",
      hasChat: true,
      source: "mock",
      leadInSec: 12,
      fromSec: 0,
      toSec: 120,
      syncOffsetSec: Number(url.searchParams.get("syncOffsetSec")) || 0,
      messages: [payload],
      gaps: [],
      hasMoreBefore: false,
      hasMoreAfter: false,
    }),
  }));
}

/** A complete window holding `messages`, with no lead-in, offset or gaps. */
function windowFetch(messages) {
  return vi.fn(async () => ({
    ok: true,
    json: async () => ({
      v: 1, recordingId: "r1", state: "complete", hasChat: true, source: "mock",
      leadInSec: 0, fromSec: 0, toSec: 120, syncOffsetSec: 0, gaps: [],
      hasMoreBefore: false, hasMoreAfter: false, messages,
    }),
  }));
}

const VIEW = {
  recordingId: "r1",
  open: true,
  state: "complete",
  hasChat: true,
  limit: 300,
  maxSyncOffsetSec: 30,
};

const emoteMessage = { ...message, text: "hello KEKW", emotes: [] };

function emoteFetch(entries, { available = true, chat = emoteMessage } = {}) {
  const chatFetch = fakeFetch(chat);
  return vi.fn((url, init) => url.pathname.endsWith("/emotes")
    ? Promise.resolve({
      ok: true,
      json: async () => ({
        v: 1, available, stale: false, fetchedAt: "2026-11-14T12:34:56Z", entries,
      }),
    })
    : chatFetch(url, init));
}

function playhead(time) {
  document.dispatchEvent(new CustomEvent("sk:playhead", {
    detail: { recordingId: "r1", time, duration: 60, paused: true },
  }));
}

function flush() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

afterEach(() => {
  vi.useRealTimers();
  document.body.replaceChildren();
  vi.restoreAllMocks();
});

describe("chat replay pane", () => {
  it("reveals messages by media time and renders native emotes", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = fakeFetch();
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW });
    document.dispatchEvent(new CustomEvent("sk:playhead", {
      detail: { recordingId: "r1", time: 10, duration: 60, paused: true },
    }));
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    expect(log.textContent).toContain("hello");
    expect(log.querySelector("img")?.getAttribute("alt")).toBe("Kappa");
    expect(document.querySelector("[data-sk='chat-state']").textContent).toBe("Chat replay");

    pane.dispose();
  });

  it("toggles open state and notifies Blazor", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const invokeMethodAsync = vi.fn().mockResolvedValue(undefined);
    const pane = createChatPane(root, {
      fetch: fakeFetch(),
      storage: storage(),
      dotNet: { invokeMethodAsync },
    });
    pane.setOpen(true);

    document.querySelector("[data-sk='chat-toggle']").click();
    expect(root.dataset.open).toBe("false");
    expect(document.querySelector("[data-sk='chat-toggle']").getAttribute("aria-expanded"))
      .toBe("false");
    expect(invokeMethodAsync).toHaveBeenCalledWith("SetChatOpen", false);

    pane.dispose();
  });

  it("announces itself and its open state to the player", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const announced = [];
    const listen = (event) => announced.push(event.detail);
    document.addEventListener("sk:chat", listen);

    const pane = createChatPane(root, { fetch: fakeFetch(), storage: storage() });
    pane.setOpen(true);
    document.querySelector("[data-sk='chat-toggle']").click();
    pane.dispose();
    document.removeEventListener("sk:chat", listen);

    expect(announced).toEqual([
      { available: true, open: false },
      { available: true, open: true },
      { available: true, open: false },
      { available: false, open: false },
    ]);
  });

  it("toggles and persists when the player's Chat button asks", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const invokeMethodAsync = vi.fn().mockResolvedValue(undefined);
    const pane = createChatPane(root, {
      fetch: fakeFetch(),
      storage: storage(),
      dotNet: { invokeMethodAsync },
    });

    document.dispatchEvent(new CustomEvent("sk:chat-toggle"));

    expect(root.dataset.open).toBe("true");
    expect(invokeMethodAsync).toHaveBeenCalledWith("SetChatOpen", true);
    pane.dispose();
  });

  it("names the channel's own messages in its channel colour", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = windowFetch([
      { ...message, id: "own", mt: 2, login: "Mock_Nova", display: "Mock_Nova", color: "#123456" },
      { ...message, id: "viewer", mt: 3 },
    ]);
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, channelLogin: "mock_nova", channelColor: 5 });
    playhead(10);
    await flush();

    const own = document.querySelector("[data-message-id='own'] [data-chat-name]");
    expect(own.dataset.channelColor).toBe("5");
    expect(own.style.color).toBe("var(--channel-5)");
    const viewer = document.querySelector("[data-message-id='viewer'] [data-chat-name]");
    expect(viewer.hasAttribute("data-channel-color")).toBe(false);
    expect(viewer.style.color).toBe("rgb(0, 255, 0)");
    pane.dispose();
  });

  it("keeps a chatter's Twitch colour only where it reaches 4.5:1 on the pane", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    root.style.backgroundColor = "rgb(55, 55, 64)"; // the surface token, #373740
    const fetch = windowFetch([
      { ...message, id: "bright", mt: 2, color: "#00FF00" },
      { ...message, id: "dark", mt: 3, color: "#9146FF" },
    ]);
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW });
    playhead(10);
    await flush();

    const name = (id) => document.querySelector(`[data-message-id='${id}'] [data-chat-name]`);
    expect(name("bright").style.color).toBe("rgb(0, 255, 0)");
    // #9146FF is 2.6:1 on #373740, so the name keeps text-primary from the log.
    expect(name("dark").style.color).toBe("");
    pane.dispose();
  });

  it("persists and sends the per-recording sync offset", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const store = storage();
    const fetch = fakeFetch();
    const pane = createChatPane(root, { fetch, storage: store });

    await pane.load({ ...VIEW });
    document.dispatchEvent(new CustomEvent("sk:playhead", {
      detail: { recordingId: "r1", time: 10, duration: 60, paused: true },
    }));
    await flush();

    document.querySelector("[data-sk='chat-sync-forward1']").click();
    await flush();

    expect(JSON.parse(store.getItem("sk-chat")).offsets.r1).toBe(1);
    expect(fetch.mock.calls.some(([url]) => url.searchParams.get("syncOffsetSec") === "1.000"))
      .toBe(true);

    pane.dispose();
  });

  it("keeps one fetch in flight until the first window loads", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = vi.fn(() => new Promise(() => {}));
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW });
    for (let i = 0; i < 6; i++) playhead(10 + i * 0.25);
    await flush();

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0][1].signal.aborted).toBe(false);

    pane.dispose();
  });

  it("backs off before retrying a failed fetch", async () => {
    vi.useFakeTimers();
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = vi.fn(async () => ({ ok: false, status: 503, json: async () => ({}) }));
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW });
    await vi.advanceTimersByTimeAsync(0);
    playhead(10);
    playhead(10.25);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(document.querySelector("[data-sk='chat-notice']").textContent)
      .toBe("Chat could not be loaded.");

    await vi.advanceTimersByTimeAsync(1000);
    playhead(11);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(2);

    // The next wait doubles.
    await vi.advanceTimersByTimeAsync(1000);
    playhead(12);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(2);

    pane.dispose();
  });

  it("toggles with C even while focus is inside the pane", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const pane = createChatPane(root, { fetch: fakeFetch(), storage: storage() });
    pane.setOpen(true);

    document.querySelector("[data-sk='chat-log']").dispatchEvent(
      new KeyboardEvent("keydown", { key: "c", bubbles: true, cancelable: true }));
    expect(root.dataset.open).toBe("false");

    document.body.dispatchEvent(
      new KeyboardEvent("keydown", { key: "c", repeat: true, bubbles: true, cancelable: true }));
    expect(root.dataset.open).toBe("false");

    pane.dispose();
  });

  it("refetches around a seek that lands while the first window is loading", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const pending = [];
    const fetch = vi.fn((url, { signal }) => new Promise((resolve) => {
      pending.push({ url, signal, resolve });
    }));
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW });
    playhead(1800);
    playhead(1800.25);
    await flush();

    expect(fetch).toHaveBeenCalledTimes(2);
    expect(pending[0].signal.aborted).toBe(true);
    expect(Number(pending[1].url.searchParams.get("fromSec"))).toBe(1770);

    pane.dispose();
  });

  it("keeps a captured live chat visible when a stale summary payload arrives", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const pane = createChatPane(root, { storage: storage() });

    await pane.load({ ...VIEW, open: false, state: "recording", hasChat: true });
    expect(document.querySelector("[data-sk='chat-sync']").hidden).toBe(false);

    await pane.load({ ...VIEW, open: false, state: "recording", hasChat: false });
    expect(document.querySelector("[data-sk='chat-sync']").hidden).toBe(false);
    expect(document.querySelector("[data-sk='chat-state']").textContent)
      .toBe("Chat connected");

    pane.dispose();
  });

  it("does not call a live timeline gap 'waiting for chat' once chat exists", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const pane = createChatPane(root, { storage: storage() });

    await pane.load({ ...VIEW, open: false, state: "starting", hasChat: true });
    expect(document.querySelector("[data-sk='chat-state']").textContent)
      .toBe("Chat connected");
    expect(document.querySelector("[data-sk='chat-sync']").hidden).toBe(false);

    pane.dispose();
  });

  it("keeps a live poll inside the endpoint's 600 s window", async () => {
    vi.useFakeTimers();
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = vi.fn(async (url) => ({
      ok: true,
      json: async () => ({
        v: 1, recordingId: "r1", state: "recording", hasChat: true, source: "mock",
        leadInSec: 12, fromSec: Number(url.searchParams.get("fromSec")),
        toSec: Number(url.searchParams.get("toSec")), syncOffsetSec: 0,
        messages: [], gaps: [], hasMoreBefore: false, hasMoreAfter: false,
      }),
    }));
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, state: "recording" });
    await vi.advanceTimersByTimeAsync(0);
    document.dispatchEvent(new CustomEvent("sk:playhead", {
      detail: { recordingId: "r1", time: 100, duration: 4000, paused: false },
    }));
    await vi.advanceTimersByTimeAsync(0);
    // Play on to t = 3000 in small steps, as a viewer at the live edge would.
    for (let t = 101; t <= 3000; t += 1) {
      document.dispatchEvent(new CustomEvent("sk:playhead", {
        detail: { recordingId: "r1", time: t, duration: 4000, paused: false },
      }));
    }
    await vi.advanceTimersByTimeAsync(600);

    const [url] = fetch.mock.calls.at(-1);
    const width = Number(url.searchParams.get("toSec")) - Number(url.searchParams.get("fromSec"));
    expect(width).toBeLessThanOrEqual(600);

    pane.dispose();
  });

  describe("scrollback", () => {
    // Behaves like ChatReader: one message every 1/rate s of media time, the window clamped to
    // the duration, `limit` keeping the earliest rows, and the requested toSec echoed back.
    // While live, a message exists only once the live edge has passed it. `earlier` reports
    // hasMoreBefore for any window after 0, as the server does when older chat exists.
    function chatServer({
      state, rate = 1, duration = 100_000, liveEdge = () => Infinity, earlier = false,
    }) {
      return vi.fn(async (url) => {
        const from = Number(url.searchParams.get("fromSec"));
        const to = Math.min(Number(url.searchParams.get("toSec")), duration);
        const limit = Number(url.searchParams.get("limit"));
        const newest = Math.min(to, liveEdge());
        const messages = [];
        for (let i = Math.ceil(from * rate); i / rate <= newest; i++) {
          messages.push({ ...message, id: `m${i}`, mt: i / rate, text: `message ${i}`, emotes: [] });
        }
        return {
          ok: true,
          json: async () => ({
            v: 1, recordingId: "r1", state, hasChat: true, source: "mock", leadInSec: 12,
            fromSec: from, toSec: to, syncOffsetSec: 0,
            messages: messages.slice(0, limit), gaps: [],
            hasMoreBefore: earlier && from > 0, hasMoreAfter: messages.length > limit,
          }),
        };
      });
    }

    function play(time) {
      document.dispatchEvent(new CustomEvent("sk:playhead", {
        detail: { recordingId: "r1", time, duration: 100_000, paused: false },
      }));
    }

    function renderedIds() {
      return [...document.querySelectorAll("[data-sk='chat-log'] .sk-chat-row")]
        .map((row) => row.dataset.messageId);
    }

    // Rows render up to 0.5 s past the playhead, so at t the newest row is at t + 0.5.
    async function playThrough(from, to, step = 1) {
      for (let t = from; t <= to; t += step) {
        play(t);
        await vi.advanceTimersByTimeAsync(step * 1000);
      }
    }

    it("keeps live messages as polls follow the playhead", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      let now = 600;
      const fetch = chatServer({ state: "recording", liveEdge: () => now + 2 });
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW, state: "recording" });
      // Join at the live edge, then play on in player-sized steps while the pane polls.
      for (now = 600; now <= 720; now += 0.25) {
        play(now);
        await vi.advanceTimersByTimeAsync(250);
      }

      const ids = renderedIds();
      // The first window reached 30 s back; nothing since has been dropped.
      expect(ids[0]).toBe("m570");
      expect(ids.at(-1)).toBe("m720");
      expect(ids).toHaveLength(151);
      // No poll asks for a window that starts after the playhead.
      for (const [url] of fetch.mock.calls) {
        expect(Number(url.searchParams.get("fromSec"))).toBeLessThanOrEqual(720);
      }

      pane.dispose();
    });

    it("fetches ahead and keeps earlier messages as a complete recording plays", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      const fetch = chatServer({ state: "complete", duration: 1000 });
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW });
      await playThrough(10, 400, 5);

      const ids = renderedIds();
      // Past the first window's end (130 s) and past the old 300 s retention.
      expect(ids[0]).toBe("m0");
      expect(ids.at(-1)).toBe("m400");
      expect(ids).toHaveLength(401);

      pane.dispose();
    });

    it("continues past windows the server truncated at the limit", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      // Twenty messages a second with a limit of 50: each window holds only 2.45 s, less than
      // the 5 s overlap, so a continuation must start at the last row it received.
      const fetch = chatServer({ state: "complete", rate: 20, duration: 1000 });
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW, limit: 50 });
      await playThrough(10, 40);

      const ids = renderedIds();
      expect(ids[0]).toBe("m0");
      expect(ids.at(-1)).toBe("m810");
      expect(ids).toHaveLength(811);

      pane.dispose();
    });

    it("drops the oldest rows once the retained history is full", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      const fetch = chatServer({ state: "complete", rate: 25, duration: 1000 });
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW });
      await playThrough(10, 60, 5);

      const ids = renderedIds();
      expect(ids).toHaveLength(1000);
      expect(ids[0]).toBe("m513");
      expect(ids.at(-1)).toBe("m1512");

      pane.dispose();
    });

    it("backfills earlier chat when a quiet live pane opens", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      // One message every 20 s: the fresh 30 s window alone would show two rows.
      let now = 1000;
      const fetch = chatServer({
        state: "recording", rate: 0.05, liveEdge: () => now + 2, earlier: true,
      });
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW, state: "recording" });
      await vi.advanceTimersByTimeAsync(0);
      play(now);
      await vi.advanceTimersByTimeAsync(2000);

      const ids = renderedIds();
      expect(ids[0]).toBe("m0");
      expect(ids.at(-1)).toBe("m50");
      expect(ids).toHaveLength(51);

      pane.dispose();
    });

    it("stops backfilling 30 minutes before the window it opened", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      const fetch = chatServer({ state: "complete", rate: 0.05, duration: 10_000, earlier: true });
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW });
      await vi.advanceTimersByTimeAsync(0);
      play(5000);
      await vi.advanceTimersByTimeAsync(2000);

      // The window opened at 4970 s; backfill reaches back to 3170 s, the first row at 3180 s.
      const ids = renderedIds();
      expect(ids[0]).toBe("m159");
      expect(ids.at(-1)).toBe("m250");

      pane.dispose();
    });

    it("does not backfill a chunk the limit cut short", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      // Ten messages a second: an earlier 120 s chunk overflows the limit, and keeping its
      // first 300 rows would leave a hole before the open window.
      const fetch = chatServer({ state: "complete", rate: 10, duration: 10_000, earlier: true });
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW });
      await vi.advanceTimersByTimeAsync(0);
      play(1000);
      await vi.advanceTimersByTimeAsync(2000);

      // Only the open window's 300 rows, with no older rows above a hole.
      const ids = renderedIds();
      expect(ids[0]).toBe("m9700");
      expect(ids.at(-1)).toBe("m9999");
      expect(ids).toHaveLength(300);

      pane.dispose();
    });

    it("drops a backfill chunk that lands after a seek", async () => {
      vi.useFakeTimers();
      document.body.innerHTML = MARKUP;
      const server = chatServer({ state: "complete", rate: 0.05, duration: 10_000, earlier: true });
      let release = null;
      // Hold the first backfill chunk (850–970 s) until after the seek.
      const fetch = vi.fn((url, init) => Number(url.searchParams.get("toSec")) === 970
        ? new Promise((resolve) => { release = () => resolve(server(url, init)); })
        : server(url, init));
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });

      await pane.load({ ...VIEW });
      await vi.advanceTimersByTimeAsync(0);
      play(1000);
      await vi.advanceTimersByTimeAsync(0);
      expect(release).not.toBeNull();

      play(3000);
      await vi.advanceTimersByTimeAsync(0);
      release();
      await vi.advanceTimersByTimeAsync(2000);

      // Every row is from the new window and its own backfill, none from before 1000 s.
      const ids = renderedIds();
      expect(ids.every((id) => Number(id.slice(1)) * 20 > 1000)).toBe(true);
      expect(ids.at(-1)).toBe("m150");

      pane.dispose();
    });
  });
});

describe("third-party emotes in the pane", () => {
  it("fetches the catalog once and renders a safe static image", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = emoteFetch([KEKW]);
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const image = document.querySelector("[data-sk='chat-log'] img[alt='KEKW']");
    expect(image).not.toBeNull();
    expect(image.getAttribute("title")).toBe("KEKW");
    expect(image.getAttribute("loading")).toBe("lazy");
    expect(image.getAttribute("decoding")).toBe("async");
    expect(image.getAttribute("referrerpolicy")).toBe("no-referrer");
    expect(image.getAttribute("src")).toBe(KEKW.src);
    expect(image.getAttribute("srcset")).toBe(KEKW.srcset);
    expect(fetch.mock.calls.filter(([url]) => url.pathname === "/api/recordings/r1/emotes"))
      .toHaveLength(1);

    pane.dispose();
  });

  it("renders global entries when no channel catalog is present", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = emoteFetch([KEKW]);
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    expect(log.querySelector("img[alt='KEKW']")).not.toBeNull();
    expect(log.textContent).toContain("hello ");

    pane.dispose();
  });

  it("renders an animated entry's server-provided static frame", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const catjam = {
      provider: "bttv",
      id: "566ca38765dbbdab32ec0560",
      code: "CatJAM",
      animated: true,
      src: "https://cdn.betterttv.net/emote/566ca38765dbbdab32ec0560/static/1x.webp",
      srcset: "https://cdn.betterttv.net/emote/566ca38765dbbdab32ec0560/static/1x.webp 1x, " +
        "https://cdn.betterttv.net/emote/566ca38765dbbdab32ec0560/static/2x.webp 2x",
    };
    const fetch = emoteFetch([catjam], {
      chat: { ...emoteMessage, text: "hello CatJAM" },
    });
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const image = document.querySelector("[data-sk='chat-log'] img[alt='CatJAM']");
    expect(image).not.toBeNull();
    expect(image.getAttribute("src")).toBe(catjam.src);
    expect(image.getAttribute("srcset")).toBe(catjam.srcset);

    pane.dispose();
  });

  it("keeps a modifier code as text when the catalog omits it", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = emoteFetch([KEKW], {
      chat: { ...emoteMessage, text: "w! hello KEKW" },
    });
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    expect(log.querySelector("img[alt='w!']")).toBeNull();
    expect(log.textContent).toContain("w! hello");
    expect(log.querySelector("img[alt='KEKW']")).not.toBeNull();

    pane.dispose();
  });

  it("omits an image set that is not entirely https", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const unsafe = {
      ...KEKW,
      code: "UNSAFE",
      srcset: "http://cdn.betterttv.net/emote/unsafe/1x.webp 1x, " +
        "https://cdn.betterttv.net/emote/unsafe/2x.webp 2x",
    };
    const fetch = emoteFetch([unsafe], {
      chat: { ...emoteMessage, text: "hello UNSAFE" },
    });
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const image = document.querySelector("[data-sk='chat-log'] img[alt='UNSAFE']");
    expect(image).not.toBeNull();
    expect(image.getAttribute("src")).toBe(unsafe.src);
    expect(image.hasAttribute("srcset")).toBe(false);

    pane.dispose();
  });

  it("makes no catalog request while disabled or closed", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = emoteFetch([KEKW]);
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: false });
    await flush();
    expect(fetch.mock.calls.some(([url]) => url.pathname.endsWith("/emotes"))).toBe(false);

    await pane.load({ ...VIEW, open: false, emotesEnabled: true });
    await flush();
    expect(fetch.mock.calls.some(([url]) => url.pathname.endsWith("/emotes"))).toBe(false);

    pane.setOpen(true);
    await flush();
    expect(fetch.mock.calls.filter(([url]) => url.pathname === "/api/recordings/r1/emotes"))
      .toHaveLength(1);

    pane.dispose();
  });

  it("requests the catalog once per recording load", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = emoteFetch([KEKW]);
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    expect(fetch.mock.calls.filter(([url]) => url.pathname === "/api/recordings/r1/emotes"))
      .toHaveLength(1);

    await pane.load({ ...VIEW, recordingId: "r2", emotesEnabled: true });
    await flush();
    expect(fetch.mock.calls.filter(([url]) => url.pathname === "/api/recordings/r2/emotes"))
      .toHaveLength(1);

    pane.dispose();
  });

  it("re-renders once when the catalog arrives after messages are visible", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const chatFetch = fakeFetch(emoteMessage);
    let release;
    const fetch = vi.fn((url, init) => url.pathname.endsWith("/emotes")
      ? new Promise((resolve) => {
        release = () => resolve({
          ok: true,
          json: async () => ({
            v: 1, available: true, stale: false, fetchedAt: null, entries: [KEKW],
          }),
        });
      })
      : chatFetch(url, init));
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    expect(log.querySelector("img[alt='KEKW']")).toBeNull();
    expect(log.textContent).toContain("KEKW");

    release();
    await flush();

    expect(log.querySelectorAll("img[alt='KEKW']")).toHaveLength(1);
    expect(fetch.mock.calls.filter(([url]) => url.pathname.endsWith("/emotes"))).toHaveLength(1);

    pane.dispose();
  });

  it("keeps text when the catalog is unavailable or the request fails", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const unavailable = emoteFetch([KEKW], { available: false });
    const pane = createChatPane(root, { fetch: unavailable, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();
    expect(document.querySelector("[data-sk='chat-log'] img[alt='KEKW']")).toBeNull();
    expect(document.querySelector("[data-sk='chat-log']").textContent).toContain("KEKW");
    pane.dispose();

    document.body.innerHTML = MARKUP;
    const failing = vi.fn((url, init) => url.pathname.endsWith("/emotes")
      ? Promise.resolve({ ok: false, status: 503, json: async () => ({}) })
      : fakeFetch(emoteMessage)(url, init));
    const failedPane = createChatPane(document.querySelector("[data-sk-chat]"),
      { fetch: failing, storage: storage() });
    await failedPane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    playhead(11);
    playhead(12);
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    expect(log.textContent).toContain("KEKW");
    expect(log.querySelector("img[alt='KEKW']")).toBeNull();
    expect(document.querySelector("[data-sk='chat-notice']").textContent).toBe("");
    expect(failing.mock.calls.filter(([url]) => url.pathname.endsWith("/emotes"))).toHaveLength(1);
    failedPane.dispose();
  });

  it("keeps reply quotes and Twitch ranges as plain text and native images", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const kappa = { ...KEKW, id: "kappa", code: "Kappa", src: "https://cdn.betterttv.net/emote/kappa/1x.webp" };
    const fetch = emoteFetch([kappa, KEKW], {
      chat: {
        ...message,
        text: "hello Kappa KEKW",
        reply: { display: "Bob", login: "bob", text: "KEKW" },
      },
    });
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    const images = [...log.querySelectorAll("img")];
    // Kappa is a Twitch range, so the BTTV entry with the same code loses.
    expect(images.find((image) => image.alt === "Kappa").src)
      .toContain("static-cdn.jtvnw.net");
    expect(log.querySelector("img[alt='KEKW']")).not.toBeNull();
    expect(log.querySelector(".sk-chat-reply").querySelector("img")).toBeNull();
    expect(log.querySelector(".sk-chat-reply").textContent).toContain("KEKW");

    pane.dispose();
  });

  it("ignores a catalog response that arrives after the recording changes", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const other = { ...KEKW, id: "other", code: "OTHER", src: "https://cdn.betterttv.net/emote/other/1x.webp" };
    const chatFetch = fakeFetch({ ...emoteMessage, text: "hello OTHER" });
    let release;
    const fetch = vi.fn((url, init) => {
      if (url.pathname === "/api/recordings/r1/emotes") {
        return new Promise((resolve) => {
          release = () => resolve({
            ok: true,
            json: async () => ({
              v: 1, available: true, stale: false, fetchedAt: null, entries: [KEKW],
            }),
          });
        });
      }
      if (url.pathname === "/api/recordings/r2/emotes") {
        return Promise.resolve({
          ok: true,
          json: async () => ({
            v: 1, available: true, stale: false, fetchedAt: null, entries: [other],
          }),
        });
      }
      return chatFetch(url, init);
    });
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    await pane.load({ ...VIEW, recordingId: "r2", emotesEnabled: true });
    await flush();
    document.dispatchEvent(new CustomEvent("sk:playhead", {
      detail: { recordingId: "r2", time: 10, duration: 60, paused: true },
    }));
    await flush();

    release();
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    expect(log.querySelectorAll("img[alt='OTHER']")).toHaveLength(1);
    expect(log.querySelector("img[alt='KEKW']")).toBeNull();

    pane.dispose();
  });

  it("falls back to text on image error and never retries after a rebuild", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = emoteFetch([KEKW]);
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW, emotesEnabled: true });
    await flush();
    playhead(10);
    await flush();

    const log = document.querySelector("[data-sk='chat-log']");
    const image = log.querySelector("img[alt='KEKW']");
    expect(image).not.toBeNull();
    image.dispatchEvent(new Event("error"));

    expect(log.querySelector("img[alt='KEKW']")).toBeNull();
    expect(log.textContent).toContain("KEKW");

    // A sync change rebuilds the log from the same messages.
    document.querySelector("[data-sk='chat-sync-forward1']").click();
    await flush();

    expect(log.querySelector("img[alt='KEKW']")).toBeNull();
    expect(log.textContent).toContain("KEKW");
    expect(fetch.mock.calls.filter(([url]) => url.pathname === "/api/recordings/r1/emotes"))
      .toHaveLength(1);

    pane.dispose();
  });
});

describe("third-party emote matching", () => {
  it("matches only whole space-delimited codes, never inside a larger word", () => {
    const catalog = buildEmoteCatalog([KEKW]);

    expect(emoteCodes(matchEmotes("KappaKappa", [], catalog))).toEqual([]);
    expect(emoteCodes(matchEmotes("hello KEKW", [], catalog))).toEqual(["KEKW"]);
    expect(emoteCodes(matchEmotes("KEKW KEKW", [], catalog))).toEqual(["KEKW", "KEKW"]);
  });

  it("is case-sensitive and keeps punctuation as part of the code", () => {
    const brazil = { ...KEKW, id: "a", code: "Brazil", src: "https://cdn.betterttv.net/emote/a/1x.webp" };
    const upper = { ...KEKW, id: "b", code: "BRAZIL", src: "https://cdn.betterttv.net/emote/b/1x.webp" };
    const smile = { ...KEKW, id: "c", code: "D:", src: "https://cdn.betterttv.net/emote/c/1x.webp" };
    const tf = { ...KEKW, id: "d", code: ":tf:", src: "https://cdn.betterttv.net/emote/d/1x.webp" };
    const amp = { ...KEKW, id: "e", code: "M&Mjc", src: "https://cdn.betterttv.net/emote/e/1x.webp" };
    const catalog = buildEmoteCatalog([brazil, upper, smile, tf, amp]);

    expect(matchEmotes("Brazil", [], catalog)[0].entry).toBe(brazil);
    expect(matchEmotes("BRAZIL", [], catalog)[0].entry).toBe(upper);
    expect(emoteCodes(matchEmotes("D: D", [], catalog))).toEqual(["D:"]);
    expect(emoteCodes(matchEmotes(":tf: M&Mjc", [], catalog))).toEqual([":tf:", "M&Mjc"]);
  });

  it("leaves a modifier code as text when a provider drops it from the catalog", () => {
    const catalog = buildEmoteCatalog([KEKW]);
    const segments = matchEmotes("w! hello", [], catalog);

    expect(emoteCodes(segments)).toEqual([]);
    expect(segments.map((segment) => segment.text).join("")).toBe("w! hello");
  });

  it("keeps every character and the first catalog entry for a code", () => {
    const first = { ...KEKW, id: "first" };
    const second = { ...KEKW, id: "second" };
    const catalog = buildEmoteCatalog([first, second]);
    const segments = matchEmotes("  KEKW   x ", [], catalog);

    expect(segments.map((segment) => segment.type === "emote" ? "KEKW" : segment.text).join(""))
      .toBe("  KEKW   x ");
    expect(segments.find((segment) => segment.type === "emote").entry).toBe(first);
  });

  it("falls back to text when the first entry for a code has an invalid src", () => {
    const invalid = { ...KEKW, id: "invalid", src: "http://cdn.betterttv.net/emote/a/1x.webp" };
    const valid = { ...KEKW, id: "valid", src: "https://cdn.betterttv.net/emote/b/1x.webp" };
    const catalog = buildEmoteCatalog([invalid, valid]);

    expect([...catalog.keys()]).toEqual([]);
    expect(emoteCodes(matchEmotes("KEKW", [], catalog))).toEqual([]);
  });

  it("protects native ranges and never forms a token across one", () => {
    const catalog = buildEmoteCatalog([KEKW]);

    // "KE" and "KW" are adjacent on screen but the native range separates them.
    const split = matchEmotes("KEKW", [{ from: 2, to: 3, id: "25" }], catalog);
    expect(emoteCodes(split)).toEqual([]);
    expect(split.map((segment) => segment.text).join("")).toBe("KEKW");
    expect(split.find((segment) => segment.type === "native")).toMatchObject({
      text: "KW", id: "25",
    });

    const around = matchEmotes("a KEKW", [{ from: 2, to: 5, id: "25" }], catalog);
    expect(emoteCodes(around)).toEqual([]);
    expect(around.find((segment) => segment.type === "native").text).toBe("KEKW");
  });

  it("accepts only absolute https image URLs and ignores invalid native ranges", () => {
    const catalog = buildEmoteCatalog([
      { ...KEKW, id: "http", code: "HTTP", src: "http://cdn.betterttv.net/emote/a/1x.webp" },
      { ...KEKW, id: "relative", code: "RELATIVE", src: "/emote/a/1x.webp" },
      { ...KEKW, id: "empty", code: "", src: "https://cdn.betterttv.net/emote/a/1x.webp" },
      { ...KEKW, id: "ok", code: "OK", src: "https://cdn.betterttv.net/emote/ok/1x.webp" },
    ]);
    expect([...catalog.keys()]).toEqual(["OK"]);

    // Out-of-range, reversed, and overlapping ranges cannot split or drop text.
    const segments = matchEmotes("hello OK", [
      { from: 99, to: 100, id: "25" },
      { from: 3, to: 1, id: "25" },
      { from: 1, to: 4, id: "25" },
      { from: 3, to: 6, id: "25" },
    ], catalog);
    expect(segments.map((segment) => segment.text).join("")).toBe("hello OK");
    expect(emoteCodes(segments)).toEqual(["OK"]);
  });

  it("uses code-point ranges around non-BMP characters", () => {
    const catalog = buildEmoteCatalog([KEKW]);
    // Code points: 0🔥 1(space) 2K 3E 4K 5W. A UTF-16 index would split the emoji.
    const native = matchEmotes("🔥 KEKW", [{ from: 2, to: 5, id: "25" }], catalog);
    expect(emoteCodes(native)).toEqual([]);
    expect(native.find((segment) => segment.type === "native").text).toBe("KEKW");
    expect(native[0].text).toBe("🔥 ");

    const emote = matchEmotes("🔥 KEKW 😀", [{ from: 0, to: 0, id: "25" }], catalog);
    expect(emote[0]).toMatchObject({ type: "native", text: "🔥" });
    expect(emoteCodes(emote)).toEqual(["KEKW"]);
    expect(emote.at(-1).text).toBe(" 😀");
  });
});
