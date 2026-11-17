// The Settings page's exit (design system: SettingsPage, Keyboard). Esc exits unless something
// else already owns it: a field being typed in, a dialog, a menu or another open popover. An
// open tooltip owns nothing, so it never blocks the exit. A held key repeats; only the first
// press counts, so history never skips a page.
import { isEditable } from "/js/shortcuts.js";

const OVERLAY = "dialog[open], [role='dialog'], [role='menu'], .mud-overlay, .mud-popover-open:not(.mud-tooltip)";

let installed = null;

/** Whether `event` should exit Settings, given what is open in `doc`. */
export function shouldExit(event, doc = document) {
  if (event.key !== "Escape" || event.repeat || event.defaultPrevented || event.isComposing) return false;
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return false;

  const target = event.target;
  if (isEditable(target)) return false;
  if (target instanceof Element && target.closest(OVERLAY)) return false;
  return doc.querySelector(OVERLAY) === null;
}

/**
 * Calls the component's `ExitAsync` when Escape should exit. One Settings page at a time:
 * installing again replaces the previous listener.
 */
export function install(component, doc = document) {
  uninstall();
  const listener = (event) => {
    if (!shouldExit(event, doc)) return;
    event.preventDefault();
    void component
      .invokeMethodAsync("ExitAsync")
      .catch((error) => console.warn("StreamKeepr: could not exit Settings.", error));
  };
  doc.addEventListener("keydown", listener);
  installed = { doc, listener };
}

/** Removes the listener from the document it was installed on. */
export function uninstall() {
  if (installed === null) return;
  installed.doc.removeEventListener("keydown", installed.listener);
  installed = null;
}

/** Goes back one entry in the document's browser history. */
export function back(doc = document) {
  doc.defaultView.history.back();
}
