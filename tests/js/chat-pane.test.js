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
      <span class="sk-chat-title">Chat</span>
      <span data-sk="chat-state" role="status"></span>
    </header>
    <div data-sk="chat-body">
      <div data-sk="chat-log" role="log" aria-live="off" tabindex="0"></div>
      <span data-sk="chat-notice" role="status" aria-live="polite" hidden></span>
      <div data-sk="chat-footer" hidden>
        <button data-sk="chat-jump" type="button">Jump to current time</button>
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

function playing(time) {
  document.dispatchEvent(new CustomEvent("sk:playhead", {
    detail: { recordingId: "r1", time, duration: 60, paused: false },
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

  it("toggles open state with C and notifies Blazor", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const invokeMethodAsync = vi.fn().mockResolvedValue(undefined);
    const pane = createChatPane(root, {
      fetch: fakeFetch(),
      storage: storage(),
      dotNet: { invokeMethodAsync },
    });
    pane.setOpen(true);

    document.dispatchEvent(new KeyboardEvent("keydown", { key: "c", bubbles: true }));
    expect(root.dataset.open).toBe("false");
    expect(root.querySelector("[data-sk='chat-body']").inert).toBe(true);
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
    document.dispatchEvent(new CustomEvent("sk:chat-toggle"));
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

  it("keeps the phone Chat tab open when the player asks to toggle", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const invokeMethodAsync = vi.fn().mockResolvedValue(undefined);
    const pane = createChatPane(root, {
      fetch: fakeFetch(),
      storage: storage(),
      dotNet: { invokeMethodAsync },
    });
    await pane.load({ ...VIEW, open: false, fixedOpen: true });

    document.dispatchEvent(new CustomEvent("sk:chat-toggle"));
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "c", bubbles: true }));

    expect(root.dataset.open).toBe("true");
    expect(invokeMethodAsync).not.toHaveBeenCalled();
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

  describe("chat sync", () => {
    /** A loaded pane with the playhead at 10 s and its first window fetched. */
    async function syncPane(view = VIEW, store = storage()) {
      document.body.innerHTML = MARKUP;
      const root = document.querySelector("[data-sk-chat]");
      const fetch = fakeFetch();
      const pane = createChatPane(root, { fetch, storage: store });
      await pane.load({ ...view });
      playhead(10);
      await flush();
      const offsets = () => fetch.mock.calls.map(([url]) => url.searchParams.get("syncOffsetSec"));
      return { pane, offsets };
    }

    it("sends the viewer's offset from Settings with every fetch", async () => {
      const store = storage();
      // A per-recording offset an earlier version saved no longer applies.
      store.setItem("sk-chat", JSON.stringify({ v: 1, offsets: { r1: 9 } }));
      const { pane, offsets } = await syncPane({ ...VIEW, syncOffsetSec: -4 }, store);

      expect(offsets().length).toBeGreaterThan(0);
      expect(new Set(offsets())).toEqual(new Set(["-4.000"]));
      pane.dispose();
    });

    it("applies a changed offset at once, refetching around the playhead", async () => {
      const { pane, offsets } = await syncPane();
      const before = offsets().length;

      pane.setSyncOffset(1.5);
      await flush();
      expect(offsets().slice(before)).toEqual(["1.500"]);

      // The same offset again fetches nothing; one past the limit is clamped to it.
      pane.setSyncOffset(1.5);
      pane.setSyncOffset(45);
      await flush();
      expect(offsets().slice(before)).toEqual(["1.500", "30.000"]);
      pane.dispose();
    });

    it("never writes the offset to browser storage", async () => {
      const setItem = vi.spyOn(Storage.prototype, "setItem");
      const { pane } = await syncPane();

      pane.setSyncOffset(2);
      await flush();

      expect(setItem).not.toHaveBeenCalled();
      pane.dispose();
    });
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

  it("shows the load error outside the footer and clears it on recovery", async () => {
    vi.useFakeTimers();
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    let calls = 0;
    const fetch = vi.fn(async () => {
      calls++;
      if (calls === 1) return { ok: false, status: 503, json: async () => ({}) };
      return {
        ok: true,
        json: async () => ({
          v: 1, recordingId: "r1", state: "complete", hasChat: true, source: "mock",
          leadInSec: 0, fromSec: 0, toSec: 120, syncOffsetSec: 0, gaps: [],
          hasMoreBefore: false, hasMoreAfter: false, messages: [message],
        }),
      };
    });
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ ...VIEW });
    await vi.advanceTimersByTimeAsync(0);

    const notice = document.querySelector("[data-sk='chat-notice']");
    expect(notice.hidden).toBe(false);
    expect(notice.textContent).toBe("Chat could not be loaded.");
    expect(notice.closest("[data-sk='chat-footer']")).toBeNull();
    expect(document.querySelector("[data-sk='chat-footer']")).toBeNull();

    await vi.advanceTimersByTimeAsync(1000);
    playhead(10);
    await vi.advanceTimersByTimeAsync(0);

    expect(notice.hidden).toBe(true);
    expect(notice.textContent).toBe("");
    expect(document.querySelector("[data-sk='chat-footer']")).toBeNull();

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

    await pane.load({ ...VIEW, open: false, state: "recording", hasChat: false });
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

  describe("smooth scrolling", () => {
    afterEach(() => {
      vi.unstubAllGlobals();
    });

    // One message a second from 0 s, so a playhead at t reveals m0 … m(t).
    const steady = Array.from({ length: 30 }, (_, i) =>
      ({ ...message, id: `m${i}`, mt: i, text: `message ${i}`, emotes: [] }));

    /**
     * Gives the log a layout jsdom lacks: 20 px rows in a 40 px viewport. scrollTop rounds to
     * whole pixels and clamps to the end, as a browser on a 1x display does.
     */
    function layout(log) {
      let top = 0;
      Object.defineProperty(log, "clientHeight", { configurable: true, get: () => 40 });
      Object.defineProperty(log, "scrollHeight", {
        configurable: true, get: () => log.children.length * 20,
      });
      Object.defineProperty(log, "scrollTop", {
        configurable: true,
        get: () => top,
        set: (value) => { top = Math.min(Math.max(Math.round(value), 0), end(log)); },
      });
      return log;
    }

    async function openPane({ fetch = windowFetch(steady), view = VIEW } = {}) {
      document.body.innerHTML = MARKUP;
      const log = layout(document.querySelector("[data-sk='chat-log']"));
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });
      await pane.load({ ...view });
      await flush();
      // From here on animation frames run only when a test advances them.
      vi.useFakeTimers();
      return { pane, log };
    }

    // Playback in one-second steps, as the player's throttled playhead reports it.
    function playPaused(from, to) {
      for (let t = from; t <= to; t++) playhead(t);
    }

    // The same, with the player reporting that it is playing.
    function playPlaying(from, to) {
      for (let t = from; t <= to; t++) playing(t);
    }

    const rows = (log) => [...log.querySelectorAll(".sk-chat-row")];
    const end = (log) => Math.max(log.scrollHeight - log.clientHeight, 0);
    const jump = () => document.querySelector("[data-sk='chat-jump']");
    const footer = () => document.querySelector("[data-sk='chat-footer']");
    const frames = (count) => vi.advanceTimersByTime(count * 16);
    // Long enough for any glide to land.
    const settle = () => frames(120);

    /** Each frame's scrollTop over `count` frames. */
    function track(log, count) {
      const tops = [];
      for (let i = 0; i < count; i++) {
        frames(1);
        tops.push(log.scrollTop);
      }
      return tops;
    }

    function scrollAway(log) {
      log.dispatchEvent(new WheelEvent("wheel", { deltaY: -100, bubbles: true }));
      log.scrollTop = 0;
      log.dispatchEvent(new Event("scroll"));
    }

    it("keeps rendered rows and appends new ones as playback runs on", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 2);
      const before = rows(log);
      expect(before.map((row) => row.dataset.messageId)).toEqual(["m0", "m1", "m2"]);

      playPaused(3, 4);
      const after = rows(log);
      expect(after.map((row) => row.dataset.messageId)).toEqual(["m0", "m1", "m2", "m3", "m4"]);
      // The same elements, not a rebuild: a rebuilt log jumps and drops the browser's anchor.
      for (const [index, row] of before.entries()) expect(after[index]).toBe(row);

      pane.dispose();
    });

    it("glides new rows in over several frames, easing in and landing on the end", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();
      const start = log.scrollTop;
      expect(start).toBe(end(log));

      playhead(4);
      expect(log.scrollTop).toBe(start);
      const tops = track(log, 40);
      const steps = tops.map((top, i) => top - (i === 0 ? start : tops[i - 1]));

      expect(steps.every((step) => step >= 0)).toBe(true);
      expect(steps.filter((step) => step > 0).length).toBeGreaterThan(5);
      // Eases in: the first frame moves less than the fastest one.
      expect(steps[0]).toBeLessThan(Math.max(...steps));
      expect(tops.at(-1)).toBe(end(log));

      pane.dispose();
    });

    it("keeps its speed when more rows arrive mid-glide instead of starting over", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();
      playhead(4);
      // Still speeding up, where a restart from rest shows most.
      frames(3);
      const before = log.scrollTop;
      frames(1);
      const stepBefore = log.scrollTop - before;
      expect(stepBefore).toBeGreaterThan(0);

      playhead(5);
      const at = log.scrollTop;
      frames(1);
      // A restarted animation would start again from rest; the glide only gains distance.
      expect(log.scrollTop - at).toBeGreaterThanOrEqual(stepBefore);
      settle();
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });

    it("lands instantly when the end is more than a screen away", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();
      // Three rows (60 px) arrive before any frame runs, as after a throttled tab.
      playPaused(4, 6);
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });

    it("lands instantly after a short seek ahead within the kept window", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 2);
      settle();
      // Eight seconds ahead keeps the scrollback but must not sweep through the burst.
      playhead(10);
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });

    it("scrolls new rows in instantly when the viewer prefers reduced motion", async () => {
      vi.stubGlobal("matchMedia", (query) =>
        ({ matches: query === "(prefers-reduced-motion: reduce)" }));
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();

      playhead(4);
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });

    it("keeps following while its own glide is under way", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();
      // Two rows (40 px), so the glide is still short of the bottom after a frame.
      playPaused(4, 5);
      frames(1);
      // Mid-glide the log is still above the bottom; that is not the viewer leaving.
      expect(end(log) - log.scrollTop).toBeGreaterThan(24);
      log.dispatchEvent(new Event("scroll"));
      settle();
      playhead(6);
      settle();
      expect(log.scrollTop).toBe(end(log));
      expect(footer()).toBeNull();

      pane.dispose();
    });

    it("keeps following when the viewer clicks a row or scrolls down mid-glide", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();
      playhead(4);
      frames(2);
      rows(log)[0].dispatchEvent(new MouseEvent("pointerdown", { bubbles: true }));
      log.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
      log.dispatchEvent(new WheelEvent("wheel", { deltaY: 100, bubbles: true }));
      log.dispatchEvent(new Event("scroll"));
      settle();
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });

    it("stops gliding and following when the viewer scrolls up mid-glide", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();
      playhead(4);
      frames(2);

      scrollAway(log);
      settle();
      playhead(5);
      settle();

      // Neither the glide under way nor the new row pulls the log back down.
      expect(log.scrollTop).toBe(0);

      pane.dispose();
    });

    it("stops following on an upward scroll key or a scrollbar press", async () => {
      for (const intent of [
        (log) => log.dispatchEvent(new KeyboardEvent("keydown", { key: "PageUp", bubbles: true })),
        (log) => log.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true })),
      ]) {
        const { pane, log } = await openPane();
        playPaused(1, 3);
        settle();
        playhead(4);
        frames(2);
        intent(log);
        log.scrollTop = 0;
        log.dispatchEvent(new Event("scroll"));
        playhead(5);
        settle();
        expect(log.scrollTop).toBe(0);
        pane.dispose();
        vi.useRealTimers();
      }
    });

    it("jumps to the current time instantly", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 5);
      settle();
      scrollAway(log);
      playPaused(6, 8);

      jump().click();
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });

    describe("replay reset", () => {
      it("reattaches to the playhead when playback resumes after a pause", async () => {
        const { pane, log } = await openPane();
        playPaused(1, 3);
        settle();
        scrollAway(log);
        expect(log.scrollTop).toBe(0);

        playing(4);
        settle();

        expect(log.scrollTop).toBe(end(log));
        expect(footer()).toBeNull();

        pane.dispose();
      });

      it("renders the footer only while the viewer is behind", async () => {
        const { pane, log } = await openPane();
        playPaused(1, 3);
        settle();
        expect(footer()).toBeNull();

        scrollAway(log);
        expect(footer()).not.toBeNull();
        expect(footer().hidden).toBe(false);
        expect(jump().hidden).toBe(false);
        expect(jump().textContent).toBe("Jump to current time");

        jump().click();
        expect(footer()).toBeNull();
        expect(log.scrollTop).toBe(end(log));

        pane.dispose();
      });

      it("reattaches on a forward seek beyond the seek threshold", async () => {
        const { pane, log } = await openPane();
        playPlaying(1, 3);
        settle();
        scrollAway(log);
        expect(footer()).not.toBeNull();

        playing(10);

        expect(log.scrollTop).toBe(end(log));
        expect(footer()).toBeNull();

        pane.dispose();
      });

      it("lands on the seeked chat for a qualifying seek while paused", async () => {
        const { pane, log } = await openPane();
        playPaused(1, 3);
        settle();
        scrollAway(log);

        playhead(10);

        expect(log.scrollTop).toBe(end(log));
        expect(footer()).toBeNull();

        pane.dispose();
      });

      it("hides the footer when the chat is unavailable or not captured", async () => {
        const { pane, log } = await openPane();
        playPaused(1, 3);
        settle();
        scrollAway(log);
        expect(footer()).not.toBeNull();

        await pane.load({ ...VIEW, state: "unavailable", hasChat: false });
        expect(footer()).toBeNull();

        await pane.load({ ...VIEW });
        expect(footer()).not.toBeNull();

        pane.dispose();
      });

      it("reattaches when the pane is reopened after playback moved on", async () => {
        const { pane, log } = await openPane();
        playPlaying(1, 3);
        settle();
        scrollAway(log);

        pane.setOpen(false);
        playing(4);
        pane.setOpen(true);

        expect(log.scrollTop).toBe(end(log));
        expect(footer()).toBeNull();

        pane.dispose();
      });

      it("keeps the scrollback on a pause, a rate change, a small move or another recording", async () => {
        const { pane, log } = await openPane();
        playPlaying(1, 3);
        settle();
        scrollAway(log);

        playing(4); // an ordinary tick, less than the seek threshold
        playing(4); // the same time again, as a rate change reports
        playhead(4); // a pause by itself
        document.dispatchEvent(new CustomEvent("sk:playhead", {
          detail: { recordingId: "r2", time: 30, duration: 60, paused: false },
        }));

        expect(log.scrollTop).toBe(0);
        expect(footer()).not.toBeNull();

        pane.dispose();
      });

      it("counts chat that arrives while the viewer is behind", async () => {
        vi.useFakeTimers();
        document.body.innerHTML = MARKUP;
        const log = layout(document.querySelector("[data-sk='chat-log']"));
        let now = 10;
        const fetch = vi.fn(async (url) => ({
          ok: true,
          json: async () => ({
            v: 1, recordingId: "r1", state: "recording", hasChat: true, source: "mock",
            leadInSec: 0,
            fromSec: Number(url.searchParams.get("fromSec")),
            toSec: Number(url.searchParams.get("toSec")),
            syncOffsetSec: 0, gaps: [], hasMoreBefore: false, hasMoreAfter: false,
            messages: steady.filter((entry) => entry.mt <= now),
          }),
        }));
        const pane = createChatPane(document.querySelector("[data-sk-chat]"),
          { fetch, storage: storage() });
        await pane.load({ ...VIEW, state: "recording" });
        await vi.advanceTimersByTimeAsync(0);
        playing(5);
        await vi.advanceTimersByTimeAsync(0);
        scrollAway(log);
        expect(log.scrollTop).toBe(0);

        now = 13;
        await vi.advanceTimersByTimeAsync(500);

        expect(log.scrollTop).toBe(0);
        expect(jump().textContent).toBe("Jump to current time (3)");
        expect(footer()).not.toBeNull();

        pane.dispose();
        vi.useRealTimers();
      });
    });

    it("stops its glide when the pane is disposed", async () => {
      const { pane, log } = await openPane();
      playPaused(1, 3);
      settle();
      playhead(4);
      frames(1);
      const top = log.scrollTop;
      pane.dispose();
      settle();
      expect(log.scrollTop).toBe(top);
    });

    it("lands at the end when backfill fills the log above a following view", async () => {
      vi.useFakeTimers();
      // One message every 20 s; the fresh window at 1000 s shows two rows, then backfill
      // adds older rows above them.
      const fetch = vi.fn(async (url) => {
        const from = Number(url.searchParams.get("fromSec"));
        const to = Number(url.searchParams.get("toSec"));
        const messages = [];
        for (let i = Math.ceil(from / 20); i * 20 <= to; i++) {
          messages.push({ ...message, id: `m${i}`, mt: i * 20, text: `message ${i}`, emotes: [] });
        }
        return {
          ok: true,
          json: async () => ({
            v: 1, recordingId: "r1", state: "complete", hasChat: true, source: "mock",
            leadInSec: 0, fromSec: from, toSec: to, syncOffsetSec: 0, gaps: [],
            hasMoreBefore: from > 0, hasMoreAfter: false, messages,
          }),
        };
      });
      document.body.innerHTML = MARKUP;
      const log = layout(document.querySelector("[data-sk='chat-log']"));
      const pane = createChatPane(document.querySelector("[data-sk-chat]"),
        { fetch, storage: storage() });
      await pane.load({ ...VIEW });
      await vi.advanceTimersByTimeAsync(0);
      playhead(1000);
      await vi.advanceTimersByTimeAsync(2000);

      expect(rows(log).length).toBeGreaterThan(2);
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });

    it("redraws kept rows when the channel colour changes", async () => {
      const view = { ...VIEW, channelLogin: "alice", channelColor: 3 };
      const { pane, log } = await openPane({ view });
      playPaused(1, 2);
      await pane.load({ ...view, channelColor: 5 });
      playhead(3);
      const colors = [...log.querySelectorAll("[data-chat-name]")]
        .map((name) => name.dataset.channelColor);
      expect(colors).toEqual(["5", "5", "5", "5"]);

      pane.dispose();
    });

    it("places gap rows among kept rows as later chat arrives", async () => {
      const fetch = vi.fn(async () => ({
        ok: true,
        json: async () => ({
          v: 1, recordingId: "r1", state: "complete", hasChat: true, source: "mock",
          leadInSec: 0, fromSec: 0, toSec: 120, syncOffsetSec: 0,
          gaps: [{ from: 2.5, to: 2.5, seconds: 40 }],
          hasMoreBefore: false, hasMoreAfter: false, messages: steady,
        }),
      }));
      const { pane, log } = await openPane({ fetch });

      playPaused(1, 2);
      const kept = rows(log)[0];
      playPaused(3, 4);
      const order = [...log.children].map((child) =>
        child.classList.contains("sk-chat-gap") ? "gap" : child.dataset.messageId);
      expect(order).toEqual(["m0", "m1", "m2", "gap", "m3", "m4"]);
      expect(rows(log)[0]).toBe(kept);

      pane.dispose();
    });

    it("keeps a later gap hidden, so new rows glide in at the end", async () => {
      const fetch = vi.fn(async () => ({
        ok: true,
        json: async () => ({
          v: 1, recordingId: "r1", state: "complete", hasChat: true, source: "mock",
          leadInSec: 0, fromSec: 0, toSec: 120, syncOffsetSec: 0,
          gaps: [{ from: 20, to: 20, seconds: 40 }],
          hasMoreBefore: false, hasMoreAfter: false, messages: steady,
        }),
      }));
      const { pane, log } = await openPane({ fetch });
      playPaused(1, 3);
      settle();
      expect(log.querySelector(".sk-chat-gap")).toBeNull();

      playhead(4);
      expect(log.lastElementChild.dataset.messageId).toBe("m4");
      // An append glides; a row put in above another would land instantly.
      expect(log.scrollTop).toBeLessThan(end(log));
      settle();
      expect(log.scrollTop).toBe(end(log));

      pane.dispose();
    });
  });

  it("toggles the badge text state without refetching or rebuilding the log", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const badges = [
      { set: "moderator", version: "1", info: null },
      { set: "subscriber", version: "12", info: "12" },
      { set: "glhf-pledge", version: "1", info: null },
    ];
    const fetch = fakeFetch({ ...message, badges });
    const pane = createChatPane(root, { fetch });

    await pane.load({ ...VIEW });
    playhead(10);
    await flush();

    const log = root.querySelector("[data-sk='chat-log']");
    const row = log.querySelector("[data-message-id]");
    expect(root.dataset.badgeText).toBe("true");
    expect([...log.querySelectorAll(".sk-chat-badge")].map((chip) => chip.textContent))
      .toEqual(["MOD", "SUB", "glhf-pledge"]);

    const calls = fetch.mock.calls.length;

    pane.setShowBadgeText(false);

    // The root carries the state the pane rule reads. No refetch, and the row nodes stay put,
    // so turning it back on needs no rebuild and the viewer keeps their place.
    expect(root.dataset.badgeText).toBe("false");
    expect(fetch.mock.calls).toHaveLength(calls);
    expect(log.querySelector("[data-message-id]")).toBe(row);

    pane.setShowBadgeText(true);

    expect(root.dataset.badgeText).toBe("true");
  });

  it("keeps the painted badge text when an older payload omits it", async () => {
    document.body.innerHTML = MARKUP.replace(
      'data-open="true"', 'data-open="true" data-badge-text="false"');
    const root = document.querySelector("[data-sk-chat]");
    const pane = createChatPane(root, { fetch: fakeFetch() });

    await pane.load({ ...VIEW });

    expect(root.dataset.badgeText).toBe("false");
  });

  it("starts with the badge text off when the payload says so", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const pane = createChatPane(root, { fetch: fakeFetch() });

    await pane.load({ ...VIEW, showBadgeText: false });

    expect(root.dataset.badgeText).toBe("false");

    // A recording reload carries the viewer's current value.
    await pane.load({ ...VIEW, showBadgeText: true });

    expect(root.dataset.badgeText).toBe("true");
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
    pane.setSyncOffset(1);
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
