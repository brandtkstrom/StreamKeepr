import { afterEach, describe, expect, it } from "vitest";
import {
  DEFAULT_PREFS,
  parsePlayerPrefs,
  PLAYER_PREFS_KEY,
  readPlayerPrefs,
  writePlayerPrefs,
} from "../../src/StreamKeepr/wwwroot/js/player-prefs.js";
import { playerAction } from "../../src/StreamKeepr/wwwroot/js/player-keys.js";

describe("player-prefs.js", () => {
  afterEach(() => localStorage.clear());

  it("parses a v1 value and clamps or replaces invalid fields", () => {
    expect(parsePlayerPrefs('{"v":1,"volume":0.4,"muted":true,"speed":1.5,"extra":1}'))
      .toEqual({ volume: 0.4, muted: true, speed: 1.5 });
    expect(parsePlayerPrefs('{"v":1,"volume":7,"muted":"yes","speed":3}'))
      .toEqual({ volume: 1, muted: false, speed: 1 });
    expect(parsePlayerPrefs('{"v":1,"volume":-2}').volume).toBe(0);
  });

  it("falls back to defaults for malformed, missing, or other-version values", () => {
    for (const text of [null, "", "{nope", "[]", "42", '{"v":2,"volume":0.1}']) {
      expect(parsePlayerPrefs(text)).toEqual(DEFAULT_PREFS);
    }
  });

  it("round-trips through localStorage under sk-player", () => {
    writePlayerPrefs({ volume: 0.25, muted: true, speed: 2 });

    expect(JSON.parse(localStorage.getItem(PLAYER_PREFS_KEY)))
      .toEqual({ v: 1, volume: 0.25, muted: true, speed: 2 });
    expect(readPlayerPrefs()).toEqual({ volume: 0.25, muted: true, speed: 2 });
  });

  it("survives storage that throws", () => {
    const broken = {
      getItem: () => { throw new Error("denied"); },
      setItem: () => { throw new Error("quota"); },
    };

    expect(readPlayerPrefs(broken)).toEqual(DEFAULT_PREFS);
    expect(() => writePlayerPrefs({ volume: 0.5 }, broken)).not.toThrow();
  });
});

describe("player-keys.js", () => {
  afterEach(() => document.body.replaceChildren());

  function press(key, target = document.body, init = {}) {
    // Attach the element's whole tree so ancestors like a menu or the drawer stay around it.
    let top = target;
    while (top.parentElement) top = top.parentElement;
    if (!top.isConnected) document.body.append(top);
    const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
    Object.defineProperty(event, "target", { value: target });
    return playerAction(event);
  }

  it("maps the player shortcuts", () => {
    expect(press(" ")).toBe("toggle-play");
    expect(press("k")).toBe("toggle-play");
    expect(press("K")).toBe("toggle-play");
    expect(press("j")).toBe("back-10");
    expect(press("l")).toBe("forward-10");
    expect(press("ArrowLeft")).toBe("back-5");
    expect(press("ArrowRight")).toBe("forward-5");
    expect(press("m")).toBe("mute");
    expect(press("f")).toBe("fullscreen");
    expect(press("t")).toBe("theater");
    expect(press("i")).toBe("stats");
    expect(press("x")).toBeNull();
  });

  it("leaves / and Esc to search and ignores modified keys", () => {
    expect(press("/")).toBeNull();
    expect(press("Escape")).toBeNull();
    expect(press("k", document.body, { ctrlKey: true })).toBeNull();
    expect(press("f", document.body, { metaKey: true })).toBeNull();
    expect(press("m", document.body, { shiftKey: true })).toBeNull();
  });

  it("ignores editable targets, menus, and dialogs", () => {
    expect(press("k", document.createElement("input"))).toBeNull();
    expect(press("m", document.createElement("textarea"))).toBeNull();
    const editable = document.createElement("div");
    editable.setAttribute("contenteditable", "true");
    expect(press("f", editable)).toBeNull();

    const menu = document.createElement("div");
    menu.setAttribute("role", "menu");
    const item = document.createElement("button");
    menu.append(item);
    expect(press("m", item)).toBeNull();

    document.body.innerHTML = "<dialog open><p>Hi</p></dialog>";
    expect(press("m")).toBeNull();
  });

  it("lets buttons, sliders, and the drawer keep their own keys", () => {
    expect(press(" ", document.createElement("button"))).toBeNull();
    expect(press("m", document.createElement("button"))).toBe("mute");

    const slider = document.createElement("div");
    slider.setAttribute("role", "slider");
    expect(press("ArrowRight", slider)).toBeNull();
    expect(press("j", slider)).toBe("back-10");

    const drawer = document.createElement("nav");
    drawer.id = "sk-drawer";
    const row = document.createElement("a");
    drawer.append(row);
    expect(press("ArrowLeft", row)).toBeNull();
    expect(press("t", row)).toBe("theater");
  });
});
