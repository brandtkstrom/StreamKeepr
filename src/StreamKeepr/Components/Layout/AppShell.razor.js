// Collocated shell module: app-bar scroll state, focus handling for the temporary library
// drawer below 960px, the settings links, and the non-modal paused-updates/retry bridge. AppShell.razor owns the
// drawer's open state; this module only moves focus and reports Escape. The selectors below
// mirror AppShell.razor: [data-shell], its <main> and <header>, and AppShell.DrawerId.
let dotNetRef = null;
let shell = null;
let main = null;
let appBar = null;
let drawer = null;
let modalOpen = false;
// Below 960px the open library is modal (focus is trapped); Md and up it lies over the page and
// a click outside it closes it instead.
let trapFocus = true;
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

// Called by AppShell after it renders the library open or closed.
function setModalDrawer(open, modal = true) {
  trapFocus = modal;
  if (open === modalOpen) return;
  modalOpen = open;

  if (open) {
    lastFocus =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    focusableElements()[0]?.focus();
  } else {
    // Give focus back only if it is still in the drawer or was lost with it. A drawer action
    // that closes the drawer may already have moved focus on (Clear search focuses the search
    // field); taking it back would depend on which of the two happened first.
    const active = document.activeElement;
    const stranded = !active || active === document.body || drawer?.contains(active);
    if (stranded && lastFocus instanceof HTMLElement && lastFocus.isConnected) lastFocus.focus();
    lastFocus = null;
  }
}

function onKeyDown(event) {
  if (!modalOpen) return;

  if (event.key === "Escape") {
    // An open menu, popover or dialog (a row's Delete) takes its own Escape first, and the
    // library stays open behind a dialog.
    const target = event.target instanceof Element ? event.target : null;
    if (
      event.defaultPrevented ||
      target?.closest("[role='menu'], .mud-popover, .mud-dialog-container") ||
      document.querySelector(".mud-dialog-container")
    )
      return;
    event.preventDefault();
    void dotNetRef
      ?.invokeMethodAsync("CloseModalDrawer")
      .catch((error) => console.warn("Stream Keepr: could not close the library drawer.", error));
    return;
  }

  if (event.key !== "Tab" || !trapFocus) return;

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

// Mirrors SettingsSheetUrl.PagePath.
const SETTINGS_PATH = "/settings";

/**
 * Whether a click follows a plain link to the Settings page from another page, which opens the
 * settings sheet instead (design system: SettingsSheet). Deep links into a section (a query or
 * fragment), new tabs, modified clicks and the page's own links stay links. Claiming the click
 * here, rather than in a .NET location-changing handler, leaves browser Back alone: Blazor
 * replays every Back through .NET while such a handler is registered.
 */
export function claimsSettingsLink(event, location = window.location) {
  if (event.defaultPrevented || event.button !== 0) return false;
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return false;

  const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
  if (!link || (link.target && link.target !== "_self") || link.hasAttribute("download")) return false;

  const url = new URL(link.href, location.href);
  return url.origin === location.origin
    && url.pathname.toLowerCase() === SETTINGS_PATH
    && url.search === ""
    && url.hash === ""
    && location.pathname.toLowerCase() !== SETTINGS_PATH;
}

// Capture phase, so it runs before Blazor's link interception, which skips prevented clicks.
// Only the default is prevented: the click still reaches other handlers (a menu item closes its menu).
function onClick(event) {
  if (!dotNetRef || !claimsSettingsLink(event)) return;
  event.preventDefault();
  void dotNetRef
    .invokeMethodAsync("ToggleSettingsAsync")
    .catch((error) => console.warn("Stream Keepr: could not open the settings sheet.", error));
}

// Md and up the open library closes on a press outside it, the rest of the app bar included.
// Not outside: the menu button, which toggles it itself; the app bar's search, which filters the
// open library live; and MudBlazor's popovers and dialogs, which live outside the drawer but
// belong to what is open.
const INSIDE_LIBRARY =
  ".sk-drawer-frame, [data-drawer-toggle], .sk-search, .sk-appbar-search, .mud-popover, .mud-dialog-container";

function onPointerDown(event) {
  if (!modalOpen || trapFocus || !dotNetRef) return;
  const target = event.target instanceof Element ? event.target : null;
  if (!target || target.closest(INSIDE_LIBRARY)) return;
  void dotNetRef
    .invokeMethodAsync("CloseModalDrawer")
    .catch((error) => console.warn("Stream Keepr: could not close the library.", error));
}

function onReconnectState(event) {
  const state = event.detail?.state;
  // Mirrors HARD_LOSS in reconnect.js: only a hard circuit loss opens the reconnect modal.
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

export function init(reference) {
  dotNetRef = reference;
  shell = document.querySelector("[data-shell]");
  main = shell?.querySelector("main");
  appBar = shell?.querySelector("header");
  drawer = shell?.querySelector("#sk-drawer");

  main?.addEventListener("scroll", onScroll, { passive: true });
  onScroll();
  document.addEventListener("keydown", onKeyDown);
  document.addEventListener("click", onClick, true);
  document.addEventListener("pointerdown", onPointerDown, true);

  document
    .getElementById("components-reconnect-modal")
    ?.addEventListener("components-reconnect-state-changed", onReconnectState);

  // F8-T4/library code can dispatch `sk:updates-paused` to show the note.
  window.addEventListener("sk:updates-paused", onUpdatesPaused);
}

export function dispose() {
  main?.removeEventListener("scroll", onScroll);
  document.removeEventListener("keydown", onKeyDown);
  document.removeEventListener("click", onClick, true);
  document.removeEventListener("pointerdown", onPointerDown, true);
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

export { setModalDrawer };
