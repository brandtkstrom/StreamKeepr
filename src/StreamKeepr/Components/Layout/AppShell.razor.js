// Collocated shell module: app-bar scroll state, focus handling for the temporary library
// drawer below 960px, and the non-modal paused-updates/retry bridge. AppShell.razor owns the
// drawer's open state; this module only moves focus and reports Escape.
let dotNetRef = null;
let shell = null;
let main = null;
let appBar = null;
let drawer = null;
let modalOpen = false;
let lastFocus = null;

function onScroll() {
  appBar?.classList.toggle("scrolled", (main?.scrollTop ?? 0) > 0);
}

// The drawer's tab stops: library rows are one roving stop (tabindex="-1" on the rest), and
// rows in a collapsed group are not rendered visible, so neither counts as a trap end.
function focusableElements() {
  if (!drawer) return [];
  return Array.from(
    drawer.querySelectorAll(
      'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]',
    ),
  ).filter(
    (element) =>
      element.getAttribute("tabindex") !== "-1" &&
      element.checkVisibility?.({ visibilityProperty: true }) !== false,
  );
}

// Called by AppShell after it renders the temporary drawer open or closed.
function setModalDrawer(open) {
  if (open === modalOpen) return;
  modalOpen = open;

  if (open) {
    lastFocus =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    focusableElements()[0]?.focus();
  } else {
    if (lastFocus instanceof HTMLElement && lastFocus.isConnected) lastFocus.focus();
    lastFocus = null;
  }
}

function onKeyDown(event) {
  if (!modalOpen) return;

  if (event.key === "Escape") {
    event.preventDefault();
    void dotNetRef?.invokeMethodAsync("CloseModalDrawer").catch(() => {});
    return;
  }

  if (event.key !== "Tab") return;

  const items = focusableElements();
  if (items.length === 0) return;

  const first = items[0];
  const last = items[items.length - 1];
  const inside = drawer?.contains(document.activeElement);
  if (event.shiftKey && (document.activeElement === first || !inside)) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && (document.activeElement === last || !inside)) {
    event.preventDefault();
    first.focus();
  }
}

function onReconnectState(event) {
  const state = event.detail?.state;
  // Mirrors HARD_LOSS in reconnect.js: only a hard circuit loss opens the template modal.
  const modalVisible =
    state === "paused" || state === "failed" || state === "resume-failed";
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
  shell = document.querySelector("[data-shell]");
  main = shell?.querySelector("main");
  appBar = shell?.querySelector("header");
  drawer = shell?.querySelector("#sk-drawer");

  main?.addEventListener("scroll", onScroll, { passive: true });
  onScroll();
  document.addEventListener("keydown", onKeyDown);

  document
    .getElementById("components-reconnect-modal")
    ?.addEventListener("components-reconnect-state-changed", onReconnectState);

  // F8-T4/library code can dispatch `sk:updates-paused` to show the note.
  window.addEventListener("sk:updates-paused", onUpdatesPaused);
}

export function dispose() {
  main?.removeEventListener("scroll", onScroll);
  document.removeEventListener("keydown", onKeyDown);
  document
    .getElementById("components-reconnect-modal")
    ?.removeEventListener("components-reconnect-state-changed", onReconnectState);
  window.removeEventListener("sk:updates-paused", onUpdatesPaused);

  modalOpen = false;
  lastFocus = null;
  dotNetRef = null;
  shell = null;
  main = null;
  appBar = null;
  drawer = null;
}

export { setModalDrawer, retryConnection };
