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

function flush() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

afterEach(() => {
  document.body.replaceChildren();
  vi.restoreAllMocks();
});

describe("chat replay pane", () => {
  it("reveals messages by media time and renders native emotes", async () => {
    document.body.innerHTML = MARKUP;
    const root = document.querySelector("[data-sk-chat]");
    const fetch = fakeFetch();
    const pane = createChatPane(root, { fetch, storage: storage() });

    await pane.load({ recordingId: "r1", open: true, state: "complete", hasChat: true });
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

    await pane.load({ recordingId: "r1", open: true, state: "complete", hasChat: true });
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
});
