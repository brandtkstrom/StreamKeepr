import { afterEach, describe, expect, it, vi } from "vitest";
import { focusWhenPresent } from "../../src/StreamKeepr/Components/Features/Library/LibraryDrawer.razor.js";

describe("LibraryDrawer.razor.js", () => {
  afterEach(() => {
    document.body.replaceChildren();
    vi.useRealTimers();
  });

  // jsdom has no layout, so frames are driven by fake timers.
  function frames() {
    vi.useFakeTimers({ toFake: ["requestAnimationFrame"] });
  }

  it("focuses an element that is already there", async () => {
    document.body.innerHTML = `<div role="menuitem" tabindex="-1" data-sort-option="oldest"></div>`;

    await expect(focusWhenPresent("[data-sort-option='oldest']")).resolves.toBe(true);

    expect(document.activeElement?.dataset.sortOption).toBe("oldest");
  });

  it("waits for an element the popover renders a few frames later", async () => {
    frames();
    const focused = focusWhenPresent("[data-sort-option='az']");

    vi.advanceTimersToNextFrame();
    document.body.innerHTML = `<div role="menuitem" tabindex="-1" data-sort-option="az"></div>`;
    vi.advanceTimersToNextFrame();

    await expect(focused).resolves.toBe(true);
    expect(document.activeElement?.dataset.sortOption).toBe("az");
  });

  it("gives up without throwing when the element never appears", async () => {
    frames();
    const focused = focusWhenPresent("[data-sort-option='missing']", document, 3);

    for (let frame = 0; frame < 4; frame++) vi.advanceTimersToNextFrame();

    await expect(focused).resolves.toBe(false);
    expect(document.activeElement).toBe(document.body);
  });
});
