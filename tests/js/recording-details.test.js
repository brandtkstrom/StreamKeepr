import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as details from "../../src/StreamKeepr/Components/Features/Details/RecordingDetails.razor.js";

// The More actions menu (design system: RecordingDetails). MudMenu's own Enter handling follows
// its internal index; these tests pin the keyboard to the element the user is on instead.
const MENU = `
  <div role="menu" data-testid="menu-wrapper">
    <div role="menuitem" tabindex="0" data-copy-playlist="true">Copy playlist URL</div>
    <div role="menuitem" tabindex="0" data-delete="true">Delete recording</div>
    <div role="menuitem" tabindex="-1" aria-disabled="true" data-export="true">Export MP4</div>
  </div>
  <button data-elsewhere>Elsewhere</button>`;

const find = (hook) => document.querySelector(`[${hook}]`);

function press(target, key) {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
}

describe("RecordingDetails.razor.js menu activation", () => {
  beforeEach(() => {
    document.body.innerHTML = MENU;
    details.installMenuActivation();
  });

  afterEach(() => {
    document.body.replaceChildren();
  });

  it("clicks the focused item and consumes Enter before MudMenu sees it", () => {
    const deleteItem = find("data-delete");
    const clicked = vi.fn();
    const bubbled = vi.fn();
    deleteItem.addEventListener("click", clicked);
    document.addEventListener("keydown", bubbled);

    deleteItem.focus();
    const event = press(deleteItem, "Enter");

    expect(clicked).toHaveBeenCalledTimes(1);
    expect(bubbled).not.toHaveBeenCalled();
    expect(event.defaultPrevented).toBe(true);

    document.removeEventListener("keydown", bubbled);
  });

  it("clicks the focused item on Space", () => {
    const copyItem = find("data-copy-playlist");
    const clicked = vi.fn();
    copyItem.addEventListener("click", clicked);

    copyItem.focus();
    const event = press(copyItem, " ");

    expect(clicked).toHaveBeenCalledTimes(1);
    expect(event.defaultPrevented).toBe(true);
  });

  it("leaves arrow keys to the menu", () => {
    const deleteItem = find("data-delete");
    const clicked = vi.fn();
    deleteItem.addEventListener("click", clicked);

    deleteItem.focus();
    const event = press(deleteItem, "ArrowDown");

    expect(clicked).not.toHaveBeenCalled();
    expect(event.defaultPrevented).toBe(false);
  });

  it("ignores disabled items and elements outside the menu", () => {
    const disabled = find("data-export");
    const disabledClicked = vi.fn();
    disabled.addEventListener("click", disabledClicked);
    disabled.focus();
    const disabledEvent = press(disabled, "Enter");

    expect(disabledClicked).not.toHaveBeenCalled();
    expect(disabledEvent.defaultPrevented).toBe(false);

    const elsewhere = find("data-elsewhere");
    const elsewhereClicked = vi.fn();
    elsewhere.addEventListener("click", elsewhereClicked);
    elsewhere.focus();
    const elsewhereEvent = press(elsewhere, "Enter");

    expect(elsewhereClicked).not.toHaveBeenCalled();
    expect(elsewhereEvent.defaultPrevented).toBe(false);
  });

  it("installs a single listener when called again", () => {
    details.installMenuActivation();
    const item = find("data-copy-playlist");
    const clicked = vi.fn();
    item.addEventListener("click", clicked);

    item.focus();
    press(item, "Enter");

    expect(clicked).toHaveBeenCalledTimes(1);
  });
});

describe("RecordingDetails.razor.js title steps", () => {
  it("keeps a one-line title at the base size", () => {
    expect(details.titleStepForLines(1)).toBe(0);
  });

  it("takes the first step at two lines", () => {
    expect(details.titleStepForLines(2)).toBe(1);
  });

  it("takes the second step at three or more lines", () => {
    expect(details.titleStepForLines(3)).toBe(2);
    expect(details.titleStepForLines(9)).toBe(2);
  });
});

describe("RecordingDetails.razor.js title observer", () => {
  it("measures through the font load and a ResizeObserver, then releases it on dispose", async () => {
    const root = document.createElement("div");
    root.innerHTML = '<div class="sk-details-head"><h2 data-details-title>Title</h2></div>';
    const head = root.querySelector(".sk-details-head");
    const title = root.querySelector("[data-details-title]");
    Object.defineProperty(head, "clientWidth", { configurable: true, value: 300 });
    document.body.appendChild(root);

    const originalRects = Range.prototype.getClientRects;
    Range.prototype.getClientRects = () => [{ top: 0 }, { top: 20 }, { top: 40 }];

    const originalFonts = Object.getOwnPropertyDescriptor(document, "fonts");
    let resolveFonts;
    const fontsReady = new Promise((resolve) => {
      resolveFonts = resolve;
    });
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: { ready: fontsReady },
    });

    const originalObserver = globalThis.ResizeObserver;
    const observers = [];
    globalThis.ResizeObserver = class {
      constructor(callback) {
        this.callback = callback;
        observers.push(this);
      }

      observe(target) {
        this.target = target;
      }

      disconnect() {
        this.disconnected = true;
      }
    };

    try {
      details.watchTitle(root);

      expect(title.getAttribute("data-title-step")).toBe("2");
      expect(observers).toHaveLength(1);
      expect(observers[0].target).toBe(head);

      // A font load re-runs the measurement; one line now clears the step.
      Range.prototype.getClientRects = () => [{ top: 0 }];
      resolveFonts();
      await fontsReady;
      expect(title.hasAttribute("data-title-step")).toBe(false);

      // A width change re-runs it again; three lines take the second step.
      Range.prototype.getClientRects = () => [{ top: 0 }, { top: 20 }, { top: 40 }];
      observers[0].callback([{ contentRect: { width: 299 } }]);
      expect(title.getAttribute("data-title-step")).toBe("2");

      Range.prototype.getClientRects = () => [{ top: 0 }];
      observers[0].callback([{ contentRect: { width: 298 } }]);
      expect(title.hasAttribute("data-title-step")).toBe(false);

      details.disposeTitle(root);
      expect(observers[0].disconnected).toBe(true);
    } finally {
      details.disposeTitle(root);
      Range.prototype.getClientRects = originalRects;
      if (originalFonts) Object.defineProperty(document, "fonts", originalFonts);
      else delete document.fonts;
      if (originalObserver) globalThis.ResizeObserver = originalObserver;
      else delete globalThis.ResizeObserver;
      root.remove();
    }
  });
});
