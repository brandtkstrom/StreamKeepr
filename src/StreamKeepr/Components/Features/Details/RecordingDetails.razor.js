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
