import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as shell from "../../src/StreamKeepr/Components/Layout/AppShell.razor.js";

// The shell AppShell.razor renders, reduced to what the drawer's focus handling reads.
function makeShell() {
  document.body.innerHTML = `
    <div data-shell>
      <header><button id="library">Library</button><input id="search" type="search"></header>
      <nav id="sk-drawer"><button id="row">Row</button></nav>
      <main></main>
    </div>`;
  return {
    library: document.getElementById("library"),
    search: document.getElementById("search"),
    row: document.getElementById("row"),
  };
}

describe("AppShell.razor.js temporary drawer focus", () => {
  let elements;

  beforeEach(() => {
    elements = makeShell();
    shell.init({ invokeMethodAsync: () => Promise.resolve() });
  });

  afterEach(() => {
    shell.dispose();
    document.body.replaceChildren();
  });

  it("moves focus into the drawer on open and back to the trigger on close", () => {
    elements.library.focus();

    shell.setModalDrawer(true);
    expect(document.activeElement).toBe(elements.row);

    shell.setModalDrawer(false);
    expect(document.activeElement).toBe(elements.library);
  });

  it("returns focus when it was lost with the drawer", () => {
    elements.library.focus();
    shell.setModalDrawer(true);
    elements.row.blur();

    shell.setModalDrawer(false);

    expect(document.activeElement).toBe(elements.library);
  });

  it("leaves focus alone when a drawer action already moved it outside", () => {
    // Clear search focuses the search field as its navigation closes the drawer.
    elements.library.focus();
    shell.setModalDrawer(true);
    elements.search.focus();

    shell.setModalDrawer(false);

    expect(document.activeElement).toBe(elements.search);
  });
});

describe("AppShell.razor.js library over the page (Md and up)", () => {
  let calls;
  let elements;

  beforeEach(() => {
    elements = makeShell();
    // The drawer frame MudDrawer renders around the nav, and the menu button.
    const frame = document.createElement("aside");
    frame.className = "sk-drawer-frame";
    document.querySelector("[data-shell]").append(frame);
    frame.append(document.getElementById("sk-drawer"));
    elements.library.setAttribute("data-drawer-toggle", "");
    calls = [];
    shell.init({ invokeMethodAsync: (name) => { calls.push(name); return Promise.resolve(); } });
  });

  afterEach(() => {
    shell.dispose();
    document.body.replaceChildren();
  });

  const press = (target) => target.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true }));

  it("closes on a press outside it, but not inside it, on the menu button, the search, or in a popover", () => {
    shell.setModalDrawer(true, false);
    const popover = document.createElement("div");
    popover.className = "mud-popover";
    document.body.append(popover);
    const search = document.createElement("div");
    search.className = "sk-search";
    search.append(document.getElementById("search"));
    document.querySelector("header").append(search);

    press(elements.row);
    press(elements.library);
    press(document.getElementById("search"));
    press(popover);
    expect(calls).toEqual([]);

    press(document.querySelector("main"));
    expect(calls).toEqual(["CloseModalDrawer"]);
  });

  it("closes on a press elsewhere on the app bar, such as Settings", () => {
    shell.setModalDrawer(true, false);
    const settings = document.createElement("a");
    settings.setAttribute("data-settings-toggle", "");
    document.querySelector("header").append(settings);

    press(settings);
    expect(calls).toEqual(["CloseModalDrawer"]);
  });

  it("stays open behind a dialog when Escape closes the dialog", () => {
    shell.setModalDrawer(true, false);
    const dialog = document.createElement("div");
    dialog.className = "mud-dialog-container";
    const cancel = document.createElement("button");
    dialog.append(cancel);
    document.body.append(dialog);
    cancel.focus();

    cancel.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    // The dialog's own handler may move focus to the body before the event reaches the shell.
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    expect(calls).toEqual([]);
  });

  it("closes on Escape and does not trap Tab", () => {
    shell.setModalDrawer(true, false);
    elements.row.focus();

    const tab = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    document.dispatchEvent(tab);
    expect(tab.defaultPrevented).toBe(false);

    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    expect(calls).toEqual(["CloseModalDrawer"]);
  });

  it("leaves outside presses alone while the phone's drawer is modal", () => {
    shell.setModalDrawer(true, true);
    press(document.querySelector("main"));
    expect(calls).toEqual([]);
  });
});

describe("AppShell.razor.js settings links", () => {
  let shellRef;

  function click(target, init = {}) {
    const event = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ...init });
    target.dispatchEvent(event);
    return event;
  }

  function at(path) {
    window.history.replaceState(null, "", path);
  }

  beforeEach(() => {
    document.body.innerHTML = `
      <div data-shell><header></header><nav id="sk-drawer"></nav><main></main></div>
      <a id="settings" href="/settings"><span id="icon">⚙</span></a>
      <a id="twitch" href="/settings#twitch">Manage</a>
      <a id="query" href="/settings?authError=failed">Retry</a>
      <a id="tab" href="/settings" target="_blank">New tab</a>
      <a id="home" href="/">Home</a>`;
    shellRef = { invokeMethodAsync: vi.fn(() => Promise.resolve()) };
    shell.init(shellRef);
    at("/r/abc");
  });

  afterEach(() => {
    shell.dispose();
    document.body.replaceChildren();
    at("/");
  });

  it("turns a click on a plain /settings link into the sheet's toggle", () => {
    const event = click(document.getElementById("icon"));

    expect(event.defaultPrevented).toBe(true);
    expect(shellRef.invokeMethodAsync).toHaveBeenCalledWith("ToggleSettingsAsync");
  });

  it("leaves deep links, other pages, new tabs and modified clicks to the browser", () => {
    for (const id of ["twitch", "query", "tab", "home"]) {
      expect(click(document.getElementById(id)).defaultPrevented).toBe(false);
    }
    expect(click(document.getElementById("settings"), { ctrlKey: true }).defaultPrevented).toBe(false);
    expect(click(document.getElementById("settings"), { button: 1 }).defaultPrevented).toBe(false);
    expect(shellRef.invokeMethodAsync).not.toHaveBeenCalled();
  });

  it("keeps the links plain on the settings page itself", () => {
    at("/settings");

    expect(click(document.getElementById("settings")).defaultPrevented).toBe(false);
    expect(shellRef.invokeMethodAsync).not.toHaveBeenCalled();
  });

  it("stops claiming links once disposed", () => {
    shell.dispose();

    expect(click(document.getElementById("settings")).defaultPrevented).toBe(false);
    shell.init(shellRef);
  });
});
