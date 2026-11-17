import { afterEach, beforeEach, describe, expect, it } from "vitest";
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
