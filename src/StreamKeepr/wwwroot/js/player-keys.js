// Player keyboard shortcuts (player contract §7). This module only decides which action a
// keydown means; the player runs it. `/` and Esc belong to shortcuts.js and are never claimed.

import { isEditable } from "/js/shortcuts.js";

const LETTERS = {
  k: "toggle-play",
  j: "back-10",
  l: "forward-10",
  m: "mute",
  f: "fullscreen",
  t: "theater",
  i: "stats",
};

function overlayOpen(doc, target) {
  if (target instanceof Element && target.closest("dialog[open], [role='menu'], [role='dialog']")) return true;
  return doc.querySelector("dialog[open], [role='menu']:not([hidden])") !== null;
}

/**
 * @param {KeyboardEvent} event
 * @param {Document} [doc]
 * @returns {string | null} the action, or null when the player must leave the key alone
 */
export function playerAction(event, doc = document) {
  if (event.defaultPrevented || event.isComposing) return null;
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return null;
  if (event.key === "/" || event.key === "Escape") return null;

  const target = event.target;
  if (isEditable(target) || overlayOpen(doc, target)) return null;

  const element = target instanceof Element ? target : null;
  if (element?.closest("[data-sk-chat]")) return null;

  if (event.key === " " || event.key.toLowerCase() === "k") {
    // Native activation wins on buttons and links.
    if (element?.closest("button, a[href], [role='button'], [role='switch']")) return null;
    return "toggle-play";
  }

  if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
    // Sliders handle their own arrows; the drawer's tree keys win inside the library.
    if (element?.closest("[role='slider'], [role='tree'], #sk-drawer")) return null;
    return event.key === "ArrowLeft" ? "back-5" : "forward-5";
  }

  return LETTERS[event.key.toLowerCase()] ?? null;
}
