import { describe, expect, it, vi } from "vitest";
import { scrollRow } from "../../src/StreamKeepr/Components/Features/Home/HomeRows.razor.js";

function row(clientWidth, reducedMotion = false) {
  return {
    clientWidth,
    scrollBy: vi.fn(),
    ownerDocument: {
      defaultView: { matchMedia: (query) => ({ matches: reducedMotion && query.includes("reduce") }) },
    },
  };
}

describe("HomeRows.razor.js", () => {
  it("scrolls a row by its visible width in either direction", () => {
    const element = row(800);

    scrollRow(element, 1);
    scrollRow(element, -1);

    expect(element.scrollBy.mock.calls).toEqual([
      [{ left: 800, behavior: "smooth" }],
      [{ left: -800, behavior: "smooth" }],
    ]);
  });

  it("jumps instead of gliding under reduced motion", () => {
    const element = row(500, true);

    scrollRow(element, 1);

    expect(element.scrollBy).toHaveBeenCalledWith({ left: 500, behavior: "auto" });
  });

  it("ignores a missing row", () => {
    expect(() => scrollRow(null, 1)).not.toThrow();
  });
});
