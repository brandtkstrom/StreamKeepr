// Esc on the Settings page exits it (design system: SettingsPage, Keyboard). Escape stays with
// whatever already owns it: a field being typed in, a dialog, a menu or another open popover.
// An open tooltip owns nothing, so it never blocks the exit.
import { isEditable } from "/js/shortcuts.js";

const OVERLAY = "dialog[open], [role='dialog'], [role='menu'], .mud-overlay, .mud-popover-open:not(.mud-tooltip)";

let listener = null;

/** Whether `event` should exit Settings, given what is open in `doc`. */
export function shouldExit(event, doc = document) {
  if (event.key !== "Escape" || event.defaultPrevented || event.isComposing) return false;
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return false;

  const target = event.target;
  if (isEditable(target)) return false;
  if (target instanceof Element && target.closest(OVERLAY)) return false;
  return doc.querySelector(OVERLAY) === null;
}

/**
 * Calls the page's `ExitAsync` when Escape should exit. One page at a time: installing again
 * replaces the previous listener.
 */
export function install(page, doc = document) {
  uninstall(doc);
  listener = (event) => {
    if (!shouldExit(event, doc)) return;
    event.preventDefault();
    page.invokeMethodAsync("ExitAsync");
  };
  doc.addEventListener("keydown", listener);
}

export function uninstall(doc = document) {
  if (listener === null) return;
  doc.removeEventListener("keydown", listener);
  listener = null;
}
