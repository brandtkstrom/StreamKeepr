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
