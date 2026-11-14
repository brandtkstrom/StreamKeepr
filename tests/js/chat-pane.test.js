import { afterEach, describe, expect, it, vi } from "vitest";
import { createChatPane } from "../../src/StreamKeepr/Components/Features/Chat/ChatPane.razor.js";

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

const VIEW = {
  recordingId: "r1",
  open: true,
  state: "complete",
  hasChat: true,
  limit: 300,
  maxSyncOffsetSec: 30,
};

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
