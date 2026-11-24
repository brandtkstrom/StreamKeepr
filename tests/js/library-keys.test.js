import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as keys from "../../src/StreamKeepr/wwwroot/js/library-keys.js";

// The library keyboard (design system: LibraryDrawer, Keyboard). Replaces tree-nav.test.js.
const MARKUP = `
  <nav class="sk-drawer">
    <div data-drawer-list>
      <div role="listbox" data-channel-list>
        <button role="option" data-library-channel="alpha" aria-selected="false" tabindex="0">Alpha</button>
        <button role="option" data-library-channel="beta" aria-selected="false" tabindex="-1">Beta</button>
        <button role="option" data-library-channel="gamma" aria-selected="false" tabindex="-1">Gamma</button>
      </div>
    </div>
  </nav>`;

const COLUMN = `
  <section data-recordings-column="beta">
    <button data-column-close>Close</button>
    <div role="list">
      <div role="listitem" data-row-item><a class="sk-row" href="/r/b1" data-recording-id="b1">B1</a><button data-row-delete>Delete</button></div>
      <div role="listitem" data-row-item><a class="sk-row" href="/r/b2" data-recording-id="b2">B2</a><button data-row-delete aria-disabled="true">Delete</button></div>
    </div>
  </section>`;

const press = (target, key) => {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
};
const option = (login) => document.querySelector(`[data-library-channel="${login}"]`);
const row = (id) => document.querySelector(`[data-recording-id="${id}"]`);
const list = () => document.querySelector("[data-drawer-list]");
const openColumn = () => {
  option("beta").setAttribute("aria-selected", "true");
  document.querySelector(".sk-drawer").insertAdjacentHTML("beforeend", COLUMN);
};

describe("library-keys.js", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    document.body.innerHTML = MARKUP;
    keys.init(list());
  });

  afterEach(() => {
    keys.dispose(list());
    vi.useRealTimers();
    document.body.replaceChildren();
  });

  it("moves through the channels with a roving tab stop, Home and End", () => {
    option("alpha").focus();

    expect(press(option("alpha"), "ArrowDown").defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(option("beta"));
    expect([...document.querySelectorAll("[role=option]")].map((o) => o.tabIndex)).toEqual([-1, 0, -1]);

    press(option("beta"), "End");
    expect(document.activeElement).toBe(option("gamma"));
    press(option("gamma"), "ArrowDown");
    expect(document.activeElement).toBe(option("gamma"));
    press(option("gamma"), "Home");
    expect(document.activeElement).toBe(option("alpha"));
    press(option("alpha"), "ArrowUp");
    expect(document.activeElement).toBe(option("alpha"));
  });

  it.each(["Enter", " ", "ArrowRight"])("%s selects the channel and moves focus into its column once it renders", (key) => {
    const click = vi.fn(openColumn);
    option("beta").addEventListener("click", click);
    option("beta").focus();

    press(option("beta"), key);

    expect(click).toHaveBeenCalledOnce();
    vi.advanceTimersByTime(50);
    expect(document.activeElement).toBe(row("b1"));
  });

  // The column renders after a round trip to the server, which can take seconds on a slow link.
  const selectBetaRenderingLater = (ms) => {
    option("beta").addEventListener("click", () => setTimeout(openColumn, ms));
    option("beta").focus();
    press(option("beta"), "ArrowRight");
  };

  it("still moves focus into the column when it renders seconds later", () => {
    selectBetaRenderingLater(2_000);

    vi.advanceTimersByTime(2_100);
    expect(document.activeElement).toBe(row("b1"));
  });

  it("moves focus into the column even after a re-render moved it elsewhere in the drawer", () => {
    // A re-render can move focus within the drawer (the drawer's own head); that is still ours.
    document.querySelector(".sk-drawer").insertAdjacentHTML("afterbegin", "<button id='head'>Options</button>");
    selectBetaRenderingLater(1_000);
    document.getElementById("head").focus();

    vi.advanceTimersByTime(1_100);
    expect(document.activeElement).toBe(row("b1"));
  });

  it("does not take focus back once the viewer moved outside the drawer", () => {
    document.body.insertAdjacentHTML("beforeend", "<button id='outside'>Elsewhere</button>");
    selectBetaRenderingLater(1_000);
    document.getElementById("outside").focus();

    vi.advanceTimersByTime(1_100);
    expect(document.activeElement).toBe(document.getElementById("outside"));
  });

  it("gives up when the column never renders", () => {
    selectBetaRenderingLater(60_000);

    vi.advanceTimersByTime(60_100);
    expect(document.activeElement).toBe(option("beta"));
  });

  it("moves through the recordings and goes back to the channel with ← or Escape", () => {
    openColumn();
    row("b1").focus();

    press(row("b1"), "ArrowDown");
    expect(document.activeElement).toBe(row("b2"));
    press(row("b2"), "ArrowLeft");
    expect(document.activeElement).toBe(option("beta"));

    row("b1").focus();
    const outside = vi.fn();
    document.addEventListener("keydown", outside);
    press(row("b1"), "Escape");
    expect(document.activeElement).toBe(option("beta"));
    // Escape here goes back to the channels; it must not reach the shell and close the drawer.
    expect(outside).not.toHaveBeenCalled();
    document.removeEventListener("keydown", outside);
  });

  it("leaves Escape on the channels to the shell", () => {
    option("alpha").focus();
    expect(press(option("alpha"), "Escape").defaultPrevented).toBe(false);
  });

  it("Delete opens the focused row's delete unless it is blocked", () => {
    openColumn();
    const [open, blocked] = document.querySelectorAll("[data-row-delete]");
    const openClick = vi.fn();
    const blockedClick = vi.fn();
    open.addEventListener("click", openClick);
    blocked.addEventListener("click", blockedClick);

    press(row("b1"), "Delete");
    press(row("b2"), "Delete");

    expect(openClick).toHaveBeenCalledOnce();
    expect(blockedClick).not.toHaveBeenCalled();
  });

  it("after a delete, waits for the row to leave, then focuses the one in its place, the one before, or Close", () => {
    openColumn();

    keys.focusAfterDelete(list(), 0, "b1");
    vi.advanceTimersByTime(50);
    // b1 is still there: nothing moves yet.
    expect(document.activeElement).toBe(document.body);

    row("b1").parentElement.remove();
    vi.advanceTimersByTime(50);
    expect(document.activeElement).toBe(row("b2"));

    row("b2").parentElement.remove();
    keys.focusAfterDelete(list(), 0, "b2");
    vi.advanceTimersByTime(50);
    expect(document.activeElement).toBe(document.querySelector("[data-column-close]"));
  });

  it("scrolls the current recording's row into view", () => {
    openColumn();
    row("b2").setAttribute("aria-current", "true");
    const scrolled = vi.fn();
    row("b2").scrollIntoView = scrolled;

    keys.scrollCurrentIntoView(list());

    expect(scrolled).toHaveBeenCalledWith({ block: "nearest" });
  });

  describe("a deleted row", () => {
    let finish;
    let animate;

    beforeEach(() => {
      openColumn();
      animate = vi.fn(() => ({ finished: new Promise((resolve) => { finish = resolve; }) }));
      Element.prototype.animate = animate;
      window.matchMedia = vi.fn(() => ({ matches: false }));
    });

    afterEach(() => {
      delete Element.prototype.animate;
      delete window.matchMedia;
    });

    // What Blazor does once the library drops the recording.
    const blazorRemoves = (id) => row(id).closest("[role=listitem]").remove();

    it("collapses out where it was, out of reach, once the library removes it", async () => {
      keys.armRowCollapse(list(), "b1");
      blazorRemoves("b1");
      await Promise.resolve();

      const ghost = document.querySelector("[data-collapsing]");
      expect(ghost).not.toBeNull();
      expect(ghost.nextElementSibling).toBe(row("b2").closest("[role=listitem]"));
      expect(ghost.inert).toBe(true);
      expect(ghost.getAttribute("aria-hidden")).toBe("true");
      expect(ghost.hasAttribute("role")).toBe(false);
      expect(row("b1")).toBeNull();
      expect(animate).toHaveBeenCalledWith(
        expect.arrayContaining([expect.objectContaining({ height: "0px", opacity: 0 })]),
        expect.objectContaining({ duration: 150 }),
      );

      // Focus after the delete skips it.
      keys.focusAfterDelete(list(), 0, "b1");
      expect(document.activeElement).toBe(row("b2"));

      finish();
      await Promise.resolve();
      await Promise.resolve();
      expect(document.querySelector("[data-collapsing]")).toBeNull();
    });

    it("stays gone when Delete was cancelled first", async () => {
      keys.armRowCollapse(list(), "b1");
      keys.disarmRowCollapse(list());
      blazorRemoves("b1");
      await Promise.resolve();

      expect(document.querySelector("[data-collapsing]")).toBeNull();
    });

    it("does not move under reduced motion", async () => {
      window.matchMedia = vi.fn(() => ({ matches: true }));
      keys.armRowCollapse(list(), "b1");
      blazorRemoves("b1");
      await Promise.resolve();

      expect(document.querySelector("[data-collapsing]")).toBeNull();
      expect(animate).not.toHaveBeenCalled();
    });
  });

  it("ignores modified keys", () => {
    option("alpha").focus();
    option("alpha").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", shiftKey: true, bubbles: true }));
    expect(document.activeElement).toBe(option("alpha"));
  });
});
