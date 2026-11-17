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
