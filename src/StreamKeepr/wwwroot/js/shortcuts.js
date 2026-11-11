// Global library shortcuts: `/` focuses search and Esc clears it. Typing in any other field is
// never intercepted; player shortcuts belong to the player module.
const SEARCH_SELECTOR = "[data-search-input]";

export function isEditable(element) {
  if (!(element instanceof Element)) return false;
  if (element.closest("[contenteditable]:not([contenteditable='false'])")) return true;

  const tag = element.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

function insideOverlay(element) {
  return element instanceof Element && element.closest("dialog[open], [role='menu']") !== null;
}

export function install(doc = document) {
  const onKeyDown = (event) => {
    if (event.defaultPrevented || event.isComposing) return;
    const search = doc.querySelector(SEARCH_SELECTOR);
    if (!search) return;

    const target = event.target;

    if (event.key === "/") {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (isEditable(target) || insideOverlay(target)) return;
      event.preventDefault();
      search.focus();
      search.select?.();
      return;
    }

    if (event.key !== "Escape") return;
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
