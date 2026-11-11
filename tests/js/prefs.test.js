import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

async function loadPrefs() {
  vi.resetModules();
  delete window.skPrefs;
  await import("../../src/StreamKeepr/wwwroot/js/prefs.js");
  return window.skPrefs;
}

function clearCookies() {
  for (const cookie of document.cookie ? document.cookie.split("; ") : []) {
    const name = cookie.split("=")[0];
    if (name) document.cookie = `${name}=; Path=/; Max-Age=0`;
  }
}

describe("prefs.js", () => {
  beforeEach(() => {
    clearCookies();
  });

  afterEach(() => {
    clearCookies();
  });

  it("sets sk-tz on first load and exposes the browser zone", async () => {
    const prefs = await loadPrefs();

    expect(prefs.getTimeZone()).toBeTruthy();
    expect(prefs.readCookie("sk-tz")).toBe(prefs.getTimeZone());
  });

  it("does not overwrite an existing sk-tz cookie", async () => {
    document.cookie = "sk-tz=Europe%2FBerlin; Path=/";

    const prefs = await loadPrefs();

    expect(prefs.readCookie("sk-tz")).toBe("Europe/Berlin");
  });

  it("reads and writes encoded cookie values", async () => {
    const prefs = await loadPrefs();
    const value = '{"v":1,"drawer":"rail"}';

    prefs.writeCookie("sk-ui", value);

    expect(prefs.readCookie("sk-ui")).toBe(value);
    expect(document.cookie).toContain("sk-ui=");
  });

  it("parses a UI cookie and rejects malformed values", async () => {
    const prefs = await loadPrefs();
    prefs.writeCookie("sk-ui", '{"v":1,"drawer":"rail"}');

    expect(prefs.getUi()).toEqual({ v: 1, drawer: "rail" });

    prefs.writeCookie("sk-ui", "{not json");
    expect(prefs.getUi()).toBeNull();
  });

  it("writes the versioned UI state and caps it at 4 KB", async () => {
    const prefs = await loadPrefs();
    const state = {
      v: 1,
      drawer: "expanded",
      theater: false,
      stats: false,
      details: true,
      collapsed: ["pixelwren"],
    };

    expect(prefs.writeUi(state)).toBe(true);
    expect(prefs.getUi()).toEqual(state);

    const oversized = { ...state, collapsed: ["x".repeat(5000)] };
    expect(prefs.writeUi(oversized)).toBe(false);
    expect(prefs.getUi()).toEqual(state);
  });

  it("writes an IANA time zone and ignores blank values", async () => {
    const prefs = await loadPrefs();

    expect(prefs.setTimeZone("America/New_York")).toBe(true);
    expect(prefs.readCookie("sk-tz")).toBe("America/New_York");
    expect(prefs.setTimeZone("   ")).toBe(false);
    expect(prefs.readCookie("sk-tz")).toBe("America/New_York");
  });
});
