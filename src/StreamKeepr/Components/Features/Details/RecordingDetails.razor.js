// Copy is intentionally clipboard-API-only: the contract forbids a legacy fallback.
export async function copyText(text) {
  if (!navigator.clipboard || typeof navigator.clipboard.writeText !== "function") {
    return false;
  }

  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

// Returns focus to the More actions button after its dialog closes (the menu item that opened
// the dialog no longer exists). A missing root is a no-op: signed out there is no menu.
export function focusButton(root) {
  root?.querySelector("button")?.focus();
}

// MudMenu activates the item at its own focused index, which can drift from the element the
// keyboard is actually on when the menu re-renders: Enter then runs a different item (the E2E
// keyboard journey caught Delete copying the playlist). Activate the focused item instead, and
// consume the key so MudMenu cannot activate a second one.
const MENU_ITEM =
  "[data-copy-playlist],[data-export],[data-stop-recording],[data-delete]";

let menuActivationInstalled = false;

export function installMenuActivation() {
  if (menuActivationInstalled) return;
  menuActivationInstalled = true;
  document.addEventListener("keydown", activateFocusedMenuItem, true);
}

export function activateFocusedMenuItem(event) {
  if (event.key !== "Enter" && event.key !== " " && event.key !== "Spacebar") return;

  const item = document.activeElement;
  if (!item || typeof item.matches !== "function" || !item.matches(MENU_ITEM)) return;
  if (item.getAttribute("aria-disabled") === "true") return;

  event.preventDefault();
  event.stopPropagation();
  item.click();
}

// A title that wraps to two lines takes the first type step; three or more lines take the
// second. The thresholds are line counts at the title's base size, not character counts: the
// same title fits one line on desktop and wraps on a phone or when the chat column opens.
export function titleStepForLines(lines) {
  if (lines >= 3) return 2;
  if (lines === 2) return 1;
  return 0;
}

// Count the title's natural line boxes at the base size against the column width. The clone is
// never inserted into the visible layout: it is absolutely positioned, hidden, and removed
// before returning, so the measured title is not reflowed.
function titleLineCount(title, availableWidth) {
  const clone = title.cloneNode(true);
  clone.removeAttribute("data-details-title");
  clone.removeAttribute("data-title-step");
  clone.removeAttribute("tabindex");
  clone.setAttribute("aria-hidden", "true");
  Object.assign(clone.style, {
    position: "absolute",
    visibility: "hidden",
    pointerEvents: "none",
    width: availableWidth + "px",
    maxWidth: availableWidth + "px",
    display: "block",
    whiteSpace: "normal",
    overflow: "visible",
    margin: "0",
  });
  // The CSS sets both the prefixed and standard properties; clear both on the clone so a
  // browser that honours the standard one does not clamp the measurement to two lines.
  clone.style.setProperty("-webkit-line-clamp", "unset");
  clone.style.setProperty("line-clamp", "unset");
  const host = title.parentElement ?? document.body;
  host.appendChild(clone);
  try {
    const range = document.createRange();
    range.selectNodeContents(clone);
    const tops = new Set(
      [...range.getClientRects()].map((rect) => Math.round(rect.top)),
    );
    return Math.max(1, tops.size);
  } finally {
    clone.remove();
  }
}

export function updateTitleStep(root) {
  const title = root?.querySelector?.("[data-details-title]");
  const head = root?.querySelector?.(".sk-details-head");
  if (!title || !head) return;

  const availableWidth = head.clientWidth;
  if (availableWidth < 1) return;

  const step = titleStepForLines(titleLineCount(title, availableWidth));
  if (step === 0) title.removeAttribute("data-title-step");
  else title.setAttribute("data-title-step", String(step));
}

// One observer per summary root. A WeakMap means a disposed component's element can still be
// collected, and two summaries never steal each other's observer.
const titleObservers = new WeakMap();

export function watchTitle(root) {
  if (!root || typeof root !== "object") return;
  titleObservers.get(root)?.disconnect();
  titleObservers.delete(root);

  const head = root.querySelector?.(".sk-details-head");
  if (!head) return;

  let lastWidth = -1;
  const update = () => {
    if (!head.isConnected) return;
    updateTitleStep(root);
  };

  lastWidth = head.clientWidth;
  update();
  document.fonts?.ready.then(update);

  if (typeof ResizeObserver !== "function") return;

  // Only a width change needs a new measurement. The step itself changes the title's height,
  // which would otherwise make the observer re-measure on every step.
  const observer = new ResizeObserver((entries) => {
    const width = entries[0]?.contentRect.width ?? head.clientWidth;
    if (Math.abs(width - lastWidth) < 0.5) return;
    lastWidth = width;
    update();
  });
  titleObservers.set(root, observer);
  observer.observe(head);
}

export function disposeTitle(root) {
  if (!root || typeof root !== "object") return;
  titleObservers.get(root)?.disconnect();
  titleObservers.delete(root);
}
