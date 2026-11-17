import { afterEach, describe, expect, it, vi } from "vitest";
import { closed, opened, shouldClose } from "../../src/StreamKeepr/Components/Features/Settings/SettingsSheet.razor.js";

function press(target, init = {}) {
  const event = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

function sheetMarkup(extra = "") {
  return `
    <a href="/settings" id="toggle" data-settings-toggle>Settings</a>
    <aside id="sk-settings">
      <div data-settings-sheet>
        <button id="close" data-settings-close>Close settings</button>
        <input id="field" />
        ${extra}
      </div>
    </aside>
    <div data-sk-player id="player" tabindex="0">
      <video data-sk="video"></video>
      <button data-sk="play" id="play">Play</button>
    </div>`;
}

function component() {
  return { invokeMethodAsync: vi.fn(() => Promise.resolve()) };
}

function playhead(detail) {
  document.getElementById("player").dispatchEvent(new CustomEvent("sk:playhead", { detail, bubbles: true }));
}

describe("SettingsSheet.razor.js", () => {
  afterEach(() => {
    closed();
    document.body.innerHTML = "";
  });

  it("moves focus to the sheet's Close button when it opens and back to the opener when it closes", async () => {
    document.body.innerHTML = sheetMarkup();
    document.getElementById("toggle").focus();

    await opened(component(), "#sk-settings");
    expect(document.activeElement.id).toBe("close");

    // Blazor removes the sheet's contents before the module hears that it closed.
    document.querySelector("[data-settings-sheet]").remove();
    closed();
    expect(document.activeElement.id).toBe("toggle");
  });

  it("waits for a sheet that renders late, as the phone dialog does", async () => {
    document.body.innerHTML = `<a href="/settings" id="toggle" data-settings-toggle>Settings</a>`;
    const ready = opened(component(), "#sk-settings");
    setTimeout(() => document.body.insertAdjacentHTML("beforeend", sheetMarkup().replace(/<a [^>]*>Settings<\/a>/, "")), 30);

    await ready;
    expect(document.activeElement.id).toBe("close");
  });

  it("falls back to the Settings toggle when the opener is gone, and leaves focus that moved on", async () => {
    document.body.innerHTML = `${sheetMarkup()}<div role="menu"><a id="menu-item" href="/settings">Settings</a></div>`;
    document.getElementById("menu-item").focus();
    await opened(component(), "#sk-settings");
    document.querySelector("[role='menu']").remove();
    closed();
    expect(document.activeElement.id).toBe("toggle");

    // Focus the viewer moved to the player (the desktop sheet has no overlay) stays there.
    document.getElementById("toggle").focus();
    await opened(component(), "#sk-settings");
    document.getElementById("player").focus();
    closed();
    expect(document.activeElement.id).toBe("player");
  });

  it("falls back to the player when no Settings toggle is visible, as on a phone's watch page", async () => {
    document.body.innerHTML = `${sheetMarkup()}<div role="menu"><a id="menu-item" href="/settings">Settings</a></div>`;
    document.getElementById("toggle").remove();
    document.getElementById("menu-item").focus();
    await opened(component(), "#sk-settings");
    document.querySelector("[role='menu']").remove();
    document.querySelector("[data-settings-sheet]").remove();

    closed();

    expect(document.activeElement.id).toBe("player");
  });

  it("falls back to the visible bottom-bar Settings when the app bar's is hidden, as on a phone's Home", async () => {
    document.body.innerHTML = `
      <a href="/settings" id="appbar-toggle" data-settings-toggle>Settings</a>
      <aside id="sk-settings"><div data-settings-sheet><button data-settings-close>Close settings</button></div></aside>
      <nav aria-label="Main"><a href="/settings" id="bottom-toggle" data-settings-toggle>Settings</a></nav>
      <div role="menu"><a id="menu-item" href="/settings">Settings</a></div>`;
    // jsdom has no layout; mark the app bar's toggle hidden as components.css does below 960px.
    document.getElementById("appbar-toggle").checkVisibility = () => false;
    document.getElementById("menu-item").focus();
    await opened(component(), "#sk-settings");
    document.querySelector("[role='menu']").remove();
    document.querySelector("[data-settings-sheet]").remove();

    closed();

    expect(document.activeElement.id).toBe("bottom-toggle");
  });

  it("closes on Escape from inside the sheet", async () => {
    document.body.innerHTML = sheetMarkup();
    const sheet = component();
    await opened(sheet, "#sk-settings");

    const event = press(document.getElementById("close"));

    expect(sheet.invokeMethodAsync).toHaveBeenCalledWith("CloseFromKeyboard");
    expect(event.defaultPrevented).toBe(true);
  });

  it("leaves Escape to fields, the page beside the sheet, other overlays and repeats", async () => {
    document.body.innerHTML = sheetMarkup();
    const sheet = component();
    await opened(sheet, "#sk-settings");

    press(document.getElementById("field"));
    press(document.getElementById("player"));
    press(document.getElementById("close"), { repeat: true });
    press(document.getElementById("close"), { shiftKey: true });
    expect(sheet.invokeMethodAsync).not.toHaveBeenCalled();

    // A confirmation or menu opened from the sheet owns Escape until it closes.
    document.body.insertAdjacentHTML("beforeend", `<div role="dialog"><button id="confirm">Remove</button></div>`);
    press(document.getElementById("close"));
    expect(sheet.invokeMethodAsync).not.toHaveBeenCalled();
  });

  it("is not blocked by a closed menu, such as the player's hidden speed menu", async () => {
    document.body.innerHTML = `${sheetMarkup()}<div role="menu" hidden></div>`;
    const sheet = component();
    await opened(sheet, "#sk-settings");

    press(document.getElementById("close"));

    expect(sheet.invokeMethodAsync).toHaveBeenCalledWith("CloseFromKeyboard");
  });

  it("counts the phone dialog's own overlay as the sheet's", () => {
    document.body.innerHTML = `
      <div class="mud-dialog-container">
        <div class="mud-overlay"></div>
        <div role="dialog"><div data-settings-sheet id="sk-settings"><button id="back">Close settings</button></div></div>
      </div>`;
    const sheet = document.getElementById("sk-settings");
    const event = new KeyboardEvent("keydown", { key: "Escape" });
    Object.defineProperty(event, "target", { value: document.getElementById("back") });

    expect(shouldClose(event, sheet, document)).toBe(true);
  });

  it("stops listening once closed", async () => {
    document.body.innerHTML = sheetMarkup();
    const sheet = component();
    await opened(sheet, "#sk-settings");
    closed();

    press(document.getElementById("close"));

    expect(sheet.invokeMethodAsync).not.toHaveBeenCalled();
  });

  it("shows the playhead in the now-playing strip and plays or pauses with the player's own button", async () => {
    document.body.innerHTML = sheetMarkup(`
      <div data-now-playing>
        <span data-now-playing-progress></span>
        <span data-now-playing-status></span>
        <button data-now-playing-toggle aria-label="Play"><svg data-icon="PlayArrow"></svg></button>
      </div>`);
    const play = vi.fn();
    document.getElementById("play").addEventListener("click", play);
    await opened(component(), "#sk-settings");

    playhead({ time: 3280, duration: 6560, paused: false });
    const toggle = document.querySelector("[data-now-playing-toggle]");
    expect(document.querySelector("[data-now-playing-status]").textContent).toBe(" · 54:40 · playing");
    expect(toggle.getAttribute("aria-label")).toBe("Pause");
    expect(toggle.querySelector("svg").dataset.icon).toBe("Pause");
    expect(document.querySelector("[data-now-playing-progress]").style.width).toBe("50%");

    toggle.click();
    expect(play).toHaveBeenCalledTimes(1);

    playhead({ time: 3281, duration: 0, paused: true });
    expect(document.querySelector("[data-now-playing-status]").textContent).toBe(" · 54:41 · paused");
    expect(toggle.getAttribute("aria-label")).toBe("Play");
    expect(toggle.querySelector("svg").dataset.icon).toBe("PlayArrow");
    expect(document.querySelector("[data-now-playing-progress]").style.width).toBe("0%");

    closed();
    playhead({ time: 9, duration: 18, paused: false });
    expect(document.querySelector("[data-now-playing-status]").textContent).toBe(" · 54:41 · paused");
  });

  it("starts the strip from the video's own state", async () => {
    document.body.innerHTML = sheetMarkup(`
      <div data-now-playing>
        <span data-now-playing-progress></span>
        <span data-now-playing-status></span>
        <button data-now-playing-toggle aria-label="Play"><svg data-icon="PlayArrow"></svg></button>
      </div>`);
    const video = document.querySelector("video");
    Object.defineProperty(video, "currentTime", { value: 61 });
    Object.defineProperty(video, "paused", { value: true });

    await opened(component(), "#sk-settings");

    expect(document.querySelector("[data-now-playing-status]").textContent).toBe(" · 1:01 · paused");
  });
});
