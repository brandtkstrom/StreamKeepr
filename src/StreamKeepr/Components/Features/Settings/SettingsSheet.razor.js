// The settings sheet (design system: SettingsSheet, Keyboard). SettingsSheet.razor draws it and
// the shell owns whether it is open; this module moves focus into the sheet and back, owns Esc
// while it is open, and keeps the phone's now-playing strip in step with the player.
//
// The strip listens for the player's sk:playhead events and plays or pauses by clicking the
// player's own Play button ([data-sk-player] [data-sk=play]), so the player stays the only code
// that drives the <video>.
import { isEditable } from "/js/shortcuts.js";
// Not the player module: Settings opens on every page, and the player loads only on recording
// pages (testing contract §9).
import { formatTimecode } from "/js/timecode.js";

// Overlays that own Escape. The phone sheet is a dialog itself, so its own container counts as
// the sheet; anything else open (a confirmation, a menu, a popover) is someone else's. It follows
// SettingsExit.razor.js's list, but skips hidden menus: the player keeps its speed menu in the DOM.
const OVERLAY = "dialog[open], [role='dialog'], [role='menu']:not([hidden]), .mud-overlay, .mud-popover-open:not(.mud-tooltip)";
const WAIT_MS = 2000;
const POLL_MS = 16;

let state = null;

function ownScope(sheet) {
  return sheet.closest(".mud-dialog-container") ?? sheet.closest("aside") ?? sheet;
}

/** Whether `event` should close the open `sheet`. */
export function shouldClose(event, sheet, doc = document) {
  if (event.key !== "Escape" || event.repeat || event.defaultPrevented || event.isComposing) return false;
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return false;

  const target = event.target;
  if (!(target instanceof Element) || isEditable(target)) return false;

  const own = ownScope(sheet);
  if (!own.contains(target)) return false;
  return Array.from(doc.querySelectorAll(OVERLAY)).every((overlay) => own.contains(overlay));
}

function visible(element) {
  return element instanceof HTMLElement
    && element.isConnected
    && element.checkVisibility?.({ visibilityProperty: true }) !== false;
}

function waitFor(selector, doc) {
  return new Promise((resolve) => {
    const started = Date.now();
    const poll = () => {
      const element = doc.querySelector(selector);
      if (element?.querySelector("[data-settings-close]")) resolve(element);
      else if (Date.now() - started >= WAIT_MS) resolve(null);
      else setTimeout(poll, POLL_MS);
    };
    poll();
  });
}

function attachStrip(strip, doc) {
  const status = strip.querySelector("[data-now-playing-status]");
  const progress = strip.querySelector("[data-now-playing-progress]");
  const toggle = strip.querySelector("[data-now-playing-toggle]");

  const render = ({ time, duration, paused }) => {
    if (status) status.textContent = ` · ${formatTimecode(time)} · ${paused ? "paused" : "playing"}`;
    if (progress) progress.style.width = `${duration > 0 ? Math.min(100, (time / duration) * 100) : 0}%`;
    if (toggle) {
      toggle.setAttribute("aria-label", paused ? "Play" : "Pause");
      const icon = toggle.querySelector("span") ?? toggle;
      icon.textContent = paused ? "play_arrow" : "pause";
    }
  };

  const video = doc.querySelector("[data-sk-player] video");
  if (video) {
    render({
      time: video.currentTime,
      duration: Number.isFinite(video.duration) ? video.duration : 0,
      paused: video.paused,
    });
  }

  const onPlayhead = (event) => render(event.detail ?? {});
  const onToggle = () => doc.querySelector("[data-sk-player] [data-sk='play']")?.click();
  doc.addEventListener("sk:playhead", onPlayhead);
  toggle?.addEventListener("click", onToggle);

  return () => {
    doc.removeEventListener("sk:playhead", onPlayhead);
    toggle?.removeEventListener("click", onToggle);
  };
}

/**
 * The sheet opened: remember what had focus, wait for `selector` to render (the phone dialog
 * renders a moment later), focus its Close control, and listen for Esc and the playhead.
 */
export async function opened(component, selector, doc = document) {
  closed({ restore: false });
  const current = { component, doc, opener: doc.activeElement, sheet: null, release: [] };
  state = current;

  const sheet = await waitFor(selector, doc);
  if (state !== current || !sheet) return;
  current.sheet = sheet;

  sheet.querySelector("[data-settings-close]")?.focus();

  const onKeyDown = (event) => {
    if (!shouldClose(event, sheet, doc)) return;
    event.preventDefault();
    void component
      .invokeMethodAsync("CloseFromKeyboard")
      .catch((error) => console.warn("StreamKeepr: could not close the settings sheet.", error));
  };
  doc.addEventListener("keydown", onKeyDown);
  current.release.push(() => doc.removeEventListener("keydown", onKeyDown));

  const strip = sheet.querySelector("[data-now-playing]");
  if (strip) current.release.push(attachStrip(strip, doc));
}

/**
 * The sheet closed: stop listening and give focus back if it was lost with the sheet, to the
 * control that opened it, else the Settings toggle, else the player (a phone's watch page has
 * no visible toggle; the account menu item that opened it is gone). Focus the viewer moved elsewhere (the
 * desktop sheet leaves the player usable) stays where it is.
 */
export function closed({ restore = true } = {}) {
  if (state === null) return;
  const { doc, opener, sheet, release } = state;
  state = null;
  for (const undo of release) undo();
  if (!restore) return;

  const active = doc.activeElement;
  const lost = !active || active === doc.body || (sheet !== null && sheet.contains(active));
  if (!lost) return;

  const target = visible(opener) && opener !== doc.body
    ? opener
    : ["[data-settings-toggle]", "[data-sk-player]"]
      .map((selector) => Array.from(doc.querySelectorAll(selector)).find(visible))
      .find(Boolean);
  target?.focus();
}
