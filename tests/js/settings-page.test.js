import { afterEach, describe, expect, it, vi } from "vitest";
import { install, shouldExit, uninstall } from "../../src/StreamKeepr/Components/Pages/Settings.razor.js";

function press(target, init = {}) {
  const event = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

describe("Settings.razor.js", () => {
  afterEach(() => {
    uninstall(document);
    document.body.innerHTML = "";
  });

  it("exits on Escape from the page", () => {
    const page = { invokeMethodAsync: vi.fn(() => Promise.resolve()) };
    install(page, document);

    const event = press(document.body);

    expect(page.invokeMethodAsync).toHaveBeenCalledWith("ExitAsync");
    expect(event.defaultPrevented).toBe(true);
  });

  it("ignores other keys, modifiers, composition and handled events", () => {
    expect(shouldExit(new KeyboardEvent("keydown", { key: "Enter" }), document)).toBe(false);
    expect(shouldExit(new KeyboardEvent("keydown", { key: "Escape", ctrlKey: true }), document)).toBe(false);
    expect(shouldExit(new KeyboardEvent("keydown", { key: "Escape", isComposing: true }), document)).toBe(false);

    const handled = new KeyboardEvent("keydown", { key: "Escape", cancelable: true });
    handled.preventDefault();
    expect(shouldExit(handled, document)).toBe(false);
  });

  it("leaves Escape to fields, dialogs, menus and open popovers", () => {
    document.body.innerHTML = `
      <input id="field" />
      <div role="dialog"><button id="in-dialog">OK</button></div>`;
    const page = { invokeMethodAsync: vi.fn(() => Promise.resolve()) };
    install(page, document);

    press(document.getElementById("field"));
    press(document.getElementById("in-dialog"));
    expect(page.invokeMethodAsync).not.toHaveBeenCalled();

    // An open dialog or menu anywhere owns Escape, even when focus sits outside it.
    press(document.body);
    expect(page.invokeMethodAsync).not.toHaveBeenCalled();

    document.body.innerHTML = `<div class="mud-popover mud-popover-open"><div role="menu"></div></div>`;
    press(document.body);
    expect(page.invokeMethodAsync).not.toHaveBeenCalled();
  });

  it("still exits while only a tooltip is open", () => {
    document.body.innerHTML = `<div class="mud-popover mud-popover-open mud-tooltip">Exit settings (Esc)</div>`;
    const page = { invokeMethodAsync: vi.fn(() => Promise.resolve()) };
    install(page, document);

    press(document.body);

    expect(page.invokeMethodAsync).toHaveBeenCalledTimes(1);
  });

  it("stops listening once uninstalled", () => {
    const page = { invokeMethodAsync: vi.fn(() => Promise.resolve()) };
    install(page, document);
    uninstall(document);

    press(document.body);

    expect(page.invokeMethodAsync).not.toHaveBeenCalled();
  });
});
