import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as tree from "../../src/StreamKeepr/wwwroot/js/tree-nav.js";

function group(login, expanded, rows) {
  return `
    <section class="sk-group" role="none">
      <div class="sk-group-head" role="treeitem" aria-level="1" aria-expanded="${expanded}"
           data-tree-key="g:${login}">${login}</div>
      <div class="sk-group-body" role="group" ${expanded ? "" : "inert"}>
        <div role="none">
          <div class="sk-day" role="none">Today</div>
          ${rows
            .map(
              (id) => `<a class="sk-row" role="treeitem" aria-level="2" href="/r/${id}"
                         data-tree-key="r:${id}" ${id === "a2" ? 'aria-current="true"' : ""}>${id}</a>`,
            )
            .join("")}
        </div>
      </div>
    </section>`;
}

function makeDrawer() {
  document.body.innerHTML = `
    <nav class="sk-drawer">
      <div class="sk-drawer-list">
        <div role="tree" aria-label="Recordings">
          ${group("a", true, ["a1", "a2"])}
          ${group("b", false, ["b1"])}
          ${group("c", true, ["c1"])}
        </div>
      </div>
    </nav>`;
  return document.querySelector(".sk-drawer-list");
}

const key = (el) => el?.getAttribute("data-tree-key");
const item = (k) => document.querySelector(`[data-tree-key="${k}"]`);

function press(keyName, init = {}) {
  const event = new KeyboardEvent("keydown", {
    key: keyName,
    bubbles: true,
    cancelable: true,
    ...init,
  });
  document.activeElement.dispatchEvent(event);
  return event;
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("tree-nav.js", () => {
  let list;

  beforeEach(() => {
    list = makeDrawer();
    tree.init(list);
  });

  afterEach(() => {
    tree.dispose(list);
    document.body.replaceChildren();
  });

  it("makes the list one tab stop, starting at the current row", () => {
    const stops = [...list.querySelectorAll('[role="treeitem"]')].filter(
      (el) => el.tabIndex === 0,
    );

    expect(stops.map(key)).toEqual(["r:a2"]);
    expect(item("g:a").tabIndex).toBe(-1);
  });

  it("moves with up and down, skipping collapsed rows and day headers", () => {
    item("g:a").focus();

    const keys = [];
    for (let i = 0; i < 5; i++) {
      expect(press("ArrowDown").defaultPrevented).toBe(true);
      keys.push(key(document.activeElement));
    }

    expect(keys).toEqual(["r:a1", "r:a2", "g:b", "g:c", "r:c1"]);

    press("ArrowUp");
    expect(key(document.activeElement)).toBe("g:c");
    expect(document.activeElement.tabIndex).toBe(0);
    expect(item("r:a2").tabIndex).toBe(-1);
  });

  it("jumps to the first and last items with Home and End", () => {
    item("r:a1").focus();

    press("End");
    expect(key(document.activeElement)).toBe("r:c1");

    press("Home");
    expect(key(document.activeElement)).toBe("g:a");
  });

  it("collapses and expands groups with left and right by activating the head", () => {
    const clickA = vi.fn();
    const clickB = vi.fn();
    item("g:a").addEventListener("click", clickA);
    item("g:b").addEventListener("click", clickB);

    item("g:a").focus();
    press("ArrowLeft");
    expect(clickA).toHaveBeenCalledOnce();

    item("g:b").focus();
    press("ArrowRight");
    expect(clickB).toHaveBeenCalledOnce();

    // Already collapsed: left does nothing.
    press("ArrowLeft");
    expect(clickB).toHaveBeenCalledOnce();
  });

  it("right on an expanded head enters the group and left on a row returns to it", () => {
    item("g:a").focus();

    press("ArrowRight");
    expect(key(document.activeElement)).toBe("r:a1");

    press("ArrowLeft");
    expect(key(document.activeElement)).toBe("g:a");
  });

  it("Enter and Space on a group head toggle it; rows keep their native link behaviour", () => {
    const click = vi.fn();
    item("g:a").addEventListener("click", click);
    item("g:a").focus();

    press("Enter");
    press(" ");
    expect(click).toHaveBeenCalledTimes(2);

    item("r:a1").focus();
    expect(press("Enter").defaultPrevented).toBe(false);
  });

  it("leaves modified keys and keys outside the tree alone", () => {
    item("g:a").focus();
    expect(press("ArrowDown", { altKey: true }).defaultPrevented).toBe(false);
    expect(key(document.activeElement)).toBe("g:a");
  });

  it("only moves between group heads in the rail", () => {
    list.closest(".sk-drawer").classList.add("rail");
    item("g:a").focus();

    press("ArrowDown");
    expect(key(document.activeElement)).toBe("g:b");
    press("ArrowDown");
    expect(key(document.activeElement)).toBe("g:c");

    const click = vi.fn();
    item("g:c").addEventListener("click", click);
    press("ArrowLeft");
    expect(click).not.toHaveBeenCalled();
  });

  it("keeps focus on the same logical item when it is re-rendered", async () => {
    item("r:a1").focus();
    press("ArrowDown");
    expect(key(document.activeElement)).toBe("r:a2");

    const old = item("r:a2");
    const fresh = old.cloneNode(true);
    fresh.removeAttribute("tabindex");
    old.replaceWith(fresh);
    await flush();

    expect(document.activeElement).toBe(fresh);
    expect(fresh.tabIndex).toBe(0);
  });

  it("does not pull focus back after the user leaves the tree", async () => {
    const outside = document.createElement("button");
    document.body.append(outside);
    item("r:a1").focus();
    outside.focus();
    outside.remove();
    await flush();

    item("r:a2").setAttribute("aria-current", "true");
    await flush();

    expect(list.contains(document.activeElement)).toBe(false);
  });

  it("gives new trees a tab stop and falls back to the first item", async () => {
    list.innerHTML = `<div role="tree">${group("z", true, ["z1"])}</div>`;
    await flush();

    expect(item("g:z").tabIndex).toBe(0);
    expect(item("r:z1").tabIndex).toBe(-1);
  });

  it("scrolls to a group and back to the top", () => {
    const scrolled = vi.fn();
    item("g:c").scrollIntoView = scrolled;
    list.scrollTop = 40;

    tree.scrollToKey(list, "g:c");
    expect(scrolled).toHaveBeenCalledOnce();
    expect(item("g:c").tabIndex).toBe(0);

    tree.scrollToTop(list);
    expect(list.scrollTop).toBe(0);
  });
});
