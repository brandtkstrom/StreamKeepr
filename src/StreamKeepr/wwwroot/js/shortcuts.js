// Global library shortcuts: `/` opens the library and focuses its search, and Esc clears the
// query. Typing in any other field is never intercepted; player shortcuts belong to the player.
const SEARCH_SELECTOR = "[data-search-input]";
const SEARCH_TRIGGER_SELECTOR = "[data-library-search]";

export function isEditable(element) {
  if (!(element instanceof Element)) return false;
  if (element.closest("[contenteditable]:not([contenteditable='false'])")) return true;

  const tag = element.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

function insideOverlay(element) {
  return element instanceof Element && element.closest("dialog[open], [role='dialog'], [role='menu']") !== null;
}

function isVisible(element) {
  if (!(element instanceof HTMLElement)) return false;
  return typeof element.checkVisibility === "function"
    ? element.checkVisibility({ visibilityProperty: true })
    : element.isConnected;
}

// The field lives in the library drawer, which is not rendered while the desktop rail is
// collapsed. Click the app-bar trigger, then wait for the render that mounts the field.
function focusSearch(doc) {
  const search = doc.querySelector(SEARCH_SELECTOR);
  if (search && isVisible(search)) {
    search.focus();
    search.select?.();
    return;
  }

  const trigger = doc.querySelector(SEARCH_TRIGGER_SELECTOR);
  if (!(trigger instanceof HTMLElement)) return;
  trigger.click();

  let frame = 0;
  const attempt = () => {
    const input = doc.querySelector(SEARCH_SELECTOR);
    if (input && isVisible(input)) {
      input.focus();
      input.select?.();
      return;
    }

    if (frame++ >= 30) return;
    doc.defaultView?.requestAnimationFrame(attempt);
  };

  doc.defaultView?.requestAnimationFrame(attempt);
}

export function install(doc = document) {
  const onKeyDown = (event) => {
    if (event.defaultPrevented || event.isComposing) return;

    const target = event.target;

    if (event.key === "/") {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (isEditable(target) || insideOverlay(target)) return;
      const search = doc.querySelector(SEARCH_SELECTOR);
      if (!search && !doc.querySelector(SEARCH_TRIGGER_SELECTOR)) return;
      event.preventDefault();
      focusSearch(doc);
      return;
    }

    if (event.key !== "Escape") return;
    const search = doc.querySelector(SEARCH_SELECTOR);
    if (!search) return;
    if (target !== search && (isEditable(target) || insideOverlay(target))) return;
    if (search.value === "") return;

    event.preventDefault();
    search.dispatchEvent(new CustomEvent("sk:search-clear"));
  };

  doc.addEventListener("keydown", onKeyDown);
  return () => doc.removeEventListener("keydown", onKeyDown);
}

if (typeof window !== "undefined" && typeof document !== "undefined" && !window.skShortcuts) {
  window.skShortcuts = { uninstall: install(document) };
}
