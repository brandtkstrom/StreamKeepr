import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as search from "../../src/StreamKeepr/Components/Features/Library/SearchField.razor.js";

function makeDotNetRef() {
  return { invokeMethodAsync: vi.fn().mockResolvedValue(undefined) };
}

function makeSearch() {
  const input = document.createElement("input");
  input.type = "search";
  input.setAttribute("data-search-input", "");
  document.body.append(input);
  return input;
}

function type(input, value) {
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

function press(target, key, init = {}) {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

describe("SearchField.razor.js", () => {
  beforeEach(() => vi.useFakeTimers());

  afterEach(() => {
    vi.useRealTimers();
    document.body.replaceChildren();
  });

  it("sends the trimmed query once, 250 ms after typing stops", () => {
    const input = makeSearch();
    const ref = makeDotNetRef();
    search.init(input, ref);

    type(input, "c");
    vi.advanceTimersByTime(200);
    type(input, "  chef ");
    vi.advanceTimersByTime(249);
    expect(ref.invokeMethodAsync).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(ref.invokeMethodAsync).toHaveBeenCalledOnce();
    expect(ref.invokeMethodAsync).toHaveBeenCalledWith("Search", "chef");
  });

  it("clears immediately on a clear request and cancels the pending search", () => {
    const input = makeSearch();
    const ref = makeDotNetRef();
    search.init(input, ref);

    type(input, "chef");
    input.dispatchEvent(new CustomEvent("sk:search-clear"));
    vi.advanceTimersByTime(1000);

    expect(input.value).toBe("");
    expect(ref.invokeMethodAsync.mock.calls).toEqual([["Search", ""]]);
  });

  it("does not overwrite the field when the URL already matches what is typed", () => {
    const input = makeSearch();
    search.init(input, makeDotNetRef());
    input.value = "chef ";

    search.setValue(input, "chef");
    expect(input.value).toBe("chef ");

    search.setValue(input, "art");
    expect(input.value).toBe("art");
  });

  it("stops listening after dispose", () => {
    const input = makeSearch();
    const ref = makeDotNetRef();
    search.init(input, ref);
    search.dispose(input);

    type(input, "chef");
    vi.advanceTimersByTime(1000);
    expect(ref.invokeMethodAsync).not.toHaveBeenCalled();
  });
});

describe("shortcuts.js", () => {
  let uninstall;

  beforeEach(async () => {
    vi.resetModules();
    const shortcuts = await import("../../src/StreamKeepr/wwwroot/js/shortcuts.js");
    window.skShortcuts?.uninstall();
    uninstall = shortcuts.install(document);
  });

  afterEach(() => {
    uninstall();
    document.body.replaceChildren();
  });

  it("focuses search on / from a non-editable target", () => {
    const input = makeSearch();
    const button = document.createElement("button");
    document.body.append(button);
    button.focus();

    const event = press(button, "/");

    expect(document.activeElement).toBe(input);
    expect(event.defaultPrevented).toBe(true);
  });

  it("ignores / while typing elsewhere or with modifiers", () => {
    const input = makeSearch();
    const other = document.createElement("textarea");
    const editable = document.createElement("div");
    editable.setAttribute("contenteditable", "true");
    document.body.append(other, editable);

    for (const target of [other, editable]) {
      target.focus();
      const event = press(target, "/");
      expect(event.defaultPrevented).toBe(false);
      expect(document.activeElement).not.toBe(input);
    }

    document.body.focus();
    expect(press(document.body, "/", { ctrlKey: true }).defaultPrevented).toBe(false);
  });

  it("Esc in the search field asks it to clear", () => {
    const input = makeSearch();
    const cleared = vi.fn();
    input.addEventListener("sk:search-clear", cleared);
    input.value = "chef";
    input.focus();

    const event = press(input, "Escape");

    expect(cleared).toHaveBeenCalledOnce();
    expect(event.defaultPrevented).toBe(true);
  });

  it("Esc ignores other editable targets, handled events, and an empty search", () => {
    const input = makeSearch();
    const cleared = vi.fn();
    input.addEventListener("sk:search-clear", cleared);
    const other = document.createElement("input");
    document.body.append(other);

    input.value = "chef";
    press(other, "Escape");

    const handled = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    handled.preventDefault();
    document.body.dispatchEvent(handled);

    input.value = "";
    press(document.body, "Escape");

    expect(cleared).not.toHaveBeenCalled();
  });

  it("Esc from a non-editable target clears a non-empty search", () => {
    const input = makeSearch();
    const cleared = vi.fn();
    input.addEventListener("sk:search-clear", cleared);
    input.value = "chef";

    press(document.body, "Escape");

    expect(cleared).toHaveBeenCalledOnce();
  });
});
