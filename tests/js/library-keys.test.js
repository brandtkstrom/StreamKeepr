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
      <div role="listitem"><a class="sk-row" href="/r/b1" data-recording-id="b1">B1</a><button data-row-delete>Delete</button></div>
      <div role="listitem"><a class="sk-row" href="/r/b2" data-recording-id="b2">B2</a><button data-row-delete aria-disabled="true">Delete</button></div>
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

  it("ignores modified keys", () => {
    option("alpha").focus();
    option("alpha").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", shiftKey: true, bubbles: true }));
    expect(document.activeElement).toBe(option("alpha"));
  });
});
