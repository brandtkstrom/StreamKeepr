// Collocated shell module: app-bar scroll state, the modal drawer below 960px,
// and the non-modal paused-updates/retry bridge.
let dotNetRef = null;
let shell = null;
let main = null;
let appBar = null;
let drawer = null;
let scrim = null;
let menuButton = null;
let mobileOpen = false;
let lastFocus = null;
let media = null;

function onScroll() {
  appBar?.classList.toggle("scrolled", (main?.scrollTop ?? 0) > 0);
}

function isMobile() {
  return window.matchMedia("(max-width: 959.98px)").matches;
}

function focusableElements() {
  if (!drawer) return [];
  return Array.from(
    drawer.querySelectorAll(
      'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    ),
  );
}

function applyMobileDrawer() {
  shell?.classList.toggle("drawer-modal-open", mobileOpen);
  menuButton?.setAttribute("aria-expanded", mobileOpen ? "true" : "false");

  if (mobileOpen) {
    lastFocus =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    focusableElements()[0]?.focus();
  } else {
    if (lastFocus instanceof HTMLElement && lastFocus.isConnected) lastFocus.focus();
    lastFocus = null;
  }
}

function toggleMobileDrawer() {
  if (!isMobile() || shell?.classList.contains("theater")) return;
  mobileOpen = !mobileOpen;
  applyMobileDrawer();
}

function closeMobileDrawer() {
  if (!mobileOpen) return;
  mobileOpen = false;
  applyMobileDrawer();
}

function onKeyDown(event) {
  if (!mobileOpen) return;

  if (event.key === "Escape") {
    event.preventDefault();
    closeMobileDrawer();
    return;
  }

  if (event.key !== "Tab") return;

  const items = focusableElements();
  if (items.length === 0) return;

  const first = items[0];
  const last = items[items.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}

function onMediaChange() {
  if (isMobile()) return;

  closeMobileDrawer();
  const expanded =
    drawer !== null &&
    !drawer.classList.contains("rail") &&
    !drawer.classList.contains("hidden") &&
    !shell?.classList.contains("theater");
  menuButton?.setAttribute("aria-expanded", expanded ? "true" : "false");
}

function onReconnectState(event) {
  const state = event.detail?.state;
  // Mirrors reconnect.js: only a hard circuit loss opens the template modal.
  const modalVisible =
    state === "paused" ||
    state === "failed" ||
    state === "rejected" ||
    state === "resume-failed";
  void dotNetRef
    ?.invokeMethodAsync("SetReconnectModalVisible", modalVisible)
    .catch(() => {});
}

function onUpdatesPaused(event) {
  void dotNetRef
    ?.invokeMethodAsync("SetUpdatesPaused", Boolean(event.detail))
    .catch(() => {});
}

async function retryConnection() {
  if (!window.Blazor) {
    location.reload();
    return;
  }

  try {
    const successful = await window.Blazor.reconnect();
    if (successful === false) {
      const resumed = await window.Blazor.resumeCircuit();
      if (!resumed) location.reload();
    }
  } catch {
    location.reload();
  }
}

export function init(reference) {
  dotNetRef = reference;
  shell = document.querySelector(".sk-shell");
  main = shell?.querySelector(".sk-main");
  appBar = shell?.querySelector(".sk-appbar");
  drawer = shell?.querySelector(".sk-drawer");
  scrim = shell?.querySelector(".sk-scrim");
  menuButton = shell?.querySelector("[data-drawer-toggle]");

  main?.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  scrim?.addEventListener("click", closeMobileDrawer);
  media = window.matchMedia("(max-width: 959.98px)");
  media.addEventListener("change", onMediaChange);
  document.addEventListener("keydown", onKeyDown);

  document
    .getElementById("components-reconnect-modal")
    ?.addEventListener("components-reconnect-state-changed", onReconnectState);

  // F8-T4/library code can dispatch `sk:updates-paused` to show the note.
  window.addEventListener("sk:updates-paused", onUpdatesPaused);
}

export function dispose() {
  main?.removeEventListener("scroll", onScroll);
  scrim?.removeEventListener("click", closeMobileDrawer);
  media?.removeEventListener("change", onMediaChange);
  document.removeEventListener("keydown", onKeyDown);
  document
    .getElementById("components-reconnect-modal")
    ?.removeEventListener("components-reconnect-state-changed", onReconnectState);
  window.removeEventListener("sk:updates-paused", onUpdatesPaused);

  closeMobileDrawer();
  dotNetRef = null;
  shell = null;
  main = null;
  appBar = null;
  drawer = null;
  scrim = null;
  menuButton = null;
  media = null;
}

export { isMobile, toggleMobileDrawer, closeMobileDrawer, retryConnection };
