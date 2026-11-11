// Library keyboard tree (library contract §6). The drawer list is one tab stop with a roving
// tabindex over `[role=treeitem]`; arrows, Home and End move focus on the rendered DOM without a
// server round-trip. Collapsing and expanding activate the group head, which is the only call
// back to .NET. Rows are real links, so Enter on a row is the browser's own navigation.
const ITEM = '[role="treeitem"]';
// Same breakpoint as the modal drawer in AppShell.razor.js and components.css.
const MOBILE = "(max-width: 959.98px)";

const state = new WeakMap();

const keyOf = (element) => element?.getAttribute("data-tree-key") ?? null;
const isHead = (element) => element?.getAttribute("aria-level") === "1";
const isExpanded = (element) => element?.getAttribute("aria-expanded") === "true";

function inRail(list) {
  const drawer = list.closest(".sk-drawer");
  if (!drawer?.classList.contains("rail")) return false;
  // Below 960px the rail class is ignored and the drawer opens as a full modal sheet.
  return !(typeof window.matchMedia === "function" && window.matchMedia(MOBILE).matches);
}

function visibleItems(list) {
  const rail = inRail(list);
  return [...list.querySelectorAll(ITEM)].filter(
    (element) => !element.closest("[inert]") && !(rail && !isHead(element)),
  );
}

function findByKey(list, key) {
  return [...list.querySelectorAll(ITEM)].find((element) => keyOf(element) === key) ?? null;
}

function firstChild(head) {
  return head.closest(".sk-group")?.querySelector(`${ITEM}[aria-level="2"]`) ?? null;
}

function headOf(row) {
  return row.closest(".sk-group")?.querySelector(`${ITEM}[aria-level="1"]`) ?? null;
}

function setActive(list, element) {
  const entry = state.get(list);
  if (entry) entry.activeKey = keyOf(element);
  syncTabStops(list);
}

function syncTabStops(list) {
  const entry = state.get(list);
  const items = visibleItems(list);
  if (items.length === 0) return;

  const byKey = entry?.activeKey
    ? items.find((element) => keyOf(element) === entry.activeKey)
    : undefined;
  const active =
    byKey ?? items.find((element) => element.getAttribute("aria-current") === "true") ?? items[0];

  for (const element of list.querySelectorAll(ITEM)) {
    const tabIndex = element === active ? 0 : -1;
    if (element.tabIndex !== tabIndex || !element.hasAttribute("tabindex")) {
      element.tabIndex = tabIndex;
    }
  }
  if (entry) entry.activeKey = keyOf(active);
}

function focus(list, element) {
  if (!element) return;
  setActive(list, element);
  element.focus();
}

function onKeyDown(list, event) {
  if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey) return;

  const current = event.target instanceof Element ? event.target.closest(ITEM) : null;
  if (!current || !list.contains(current)) return;

  const items = visibleItems(list);
  const index = items.indexOf(current);
  const rail = inRail(list);
  let handled = true;

  switch (event.key) {
    case "ArrowDown":
      focus(list, items[Math.min(index + 1, items.length - 1)]);
      break;
    case "ArrowUp":
      focus(list, items[Math.max(index - 1, 0)]);
      break;
    case "Home":
      focus(list, items[0]);
      break;
    case "End":
      focus(list, items[items.length - 1]);
      break;
    case "ArrowRight":
      if (rail || !isHead(current)) break;
      if (isExpanded(current)) focus(list, firstChild(current));
      else current.click();
      break;
    case "ArrowLeft":
      if (rail) break;
      if (isHead(current)) {
        if (isExpanded(current)) current.click();
      } else {
        focus(list, headOf(current));
      }
      break;
    case "Enter":
    case " ":
      if (isHead(current)) current.click();
      else handled = false;
      break;
    default:
      handled = false;
  }

  if (handled) event.preventDefault();
}

export function init(list) {
  if (!list || state.has(list)) return;

  const entry = { activeKey: null, focusedKey: null };

  entry.onKeyDown = (event) => onKeyDown(list, event);
  entry.onFocusIn = (event) => {
    const item = event.target instanceof Element ? event.target.closest(ITEM) : null;
    if (!item) return;
    entry.focusedKey = keyOf(item);
    setActive(list, item);
  };
  entry.onFocusOut = (event) => {
    const left = event.target;
    if (event.relatedTarget instanceof Node && list.contains(event.relatedTarget)) return;
    // A node that Blazor removed or replaced loses focus while disconnected; keep its key so the
    // observer can restore focus. Any other blur is the user leaving the tree.
    queueMicrotask(() => {
      if (left instanceof Node && left.isConnected) entry.focusedKey = null;
    });
  };

  // Blazor re-renders replace or reorder nodes; keep one tab stop and keep focus on the same
  // logical item when the focused node was swapped out.
  entry.observer = new MutationObserver(() => {
    syncTabStops(list);
    const active = document.activeElement;
    const lost = !active || active === document.body || !active.isConnected;
    if (entry.focusedKey && lost) {
      const again = findByKey(list, entry.focusedKey);
      if (again && !again.closest("[inert]")) again.focus();
    }
  });

  list.addEventListener("keydown", entry.onKeyDown);
  list.addEventListener("focusin", entry.onFocusIn);
  list.addEventListener("focusout", entry.onFocusOut);
  entry.observer.observe(list, {
    childList: true,
    subtree: true,
    attributes: true,
    attributeFilter: ["aria-expanded", "aria-current", "inert", "class"],
  });
  state.set(list, entry);
  syncTabStops(list);
}

export function dispose(list) {
  const entry = list && state.get(list);
  if (!entry) return;

  entry.observer.disconnect();
  list.removeEventListener("keydown", entry.onKeyDown);
  list.removeEventListener("focusin", entry.onFocusIn);
  list.removeEventListener("focusout", entry.onFocusOut);
  state.delete(list);
}

// True when the drawer is showing the compact avatar rail (not the mobile sheet).
export function isRail(list) {
  return Boolean(list) && inRail(list);
}

export function scrollToKey(list, key) {
  const element = list ? findByKey(list, key) : null;
  if (!element) return;
  setActive(list, element);
  element.scrollIntoView?.({ block: "start" });
}

export function scrollToTop(list) {
  if (list) list.scrollTop = 0;
}

// On load, bring the selected recording into view without moving focus.
export function revealCurrent(list) {
  list?.querySelector('[aria-current="true"]')?.scrollIntoView?.({ block: "nearest" });
}
