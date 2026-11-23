// Library keyboard (design system: LibraryDrawer, Keyboard; replaces tree-nav.js). The channels
// are a listbox with a roving tab stop: ↑ ↓ Home End move between them; Enter, Space or → select
// one, which opens its recordings column, and focus moves to its first recording. In the column
// ↑ ↓ move between recordings, ← or Escape go back to the selected channel, and Delete opens the
// focused row's "Delete recording?" unless its Delete is blocked. Selecting goes through the
// option's own click, the only call back to .NET; everything else is focus on the rendered DOM.
// The module also owns the drawer's other DOM-only effects: the selected row scrolled into view,
// the deleted row's collapse, and where focus goes after a delete.

const OPTION = '[role="option"]';
const ROW = "a.sk-row";
const COLUMN = "[data-recordings-column]";

const drawers = new WeakMap();
// The row watched while its "Delete recording?" is open, per drawer.
const collapses = new WeakMap();
const COLLAPSE_MS = 150;

function drawerOf(list) {
  return list?.closest(".sk-drawer") ?? list?.parentElement ?? null;
}

function options(drawer) {
  return [...drawer.querySelectorAll(`[data-channel-list] ${OPTION}`)];
}

// A collapsing row is already deleted; it is never a focus target.
function rows(drawer) {
  return [...drawer.querySelectorAll(`${COLUMN} ${ROW}`)].filter((row) => !row.closest("[data-collapsing]"));
}

/** Moves the roving tab stop to one option and focuses it. */
function focusOption(drawer, option) {
  if (!option) return;
  for (const element of options(drawer)) element.tabIndex = element === option ? 0 : -1;
  option.focus();
}

function selectedOption(drawer) {
  return drawer.querySelector(`[data-channel-list] ${OPTION}[aria-selected="true"]`);
}

// How long a selected channel's column may take to render before its focus move is dropped.
const COLUMN_WAIT_MS = 5_000;

/**
 * Focuses the first row of `login`'s column once Blazor renders it. The render waits on a round
 * trip to the server, which can take seconds on a slow link, so this waits by time rather than
 * frames. It gives up once focus has left the drawer, so it never pulls the viewer back.
 */
function focusColumnWhenOpen(drawer, login, deadline = Date.now() + COLUMN_WAIT_MS) {
  const doc = drawer.ownerDocument;
  const active = doc.activeElement;
  if (active && active !== doc.body && !drawer.contains(active)) return;
  const row = withData(drawer.querySelectorAll(COLUMN), "data-recordings-column", login)?.querySelector(ROW);
  if (row) {
    row.focus();
    return;
  }
  if (Date.now() < deadline) requestAnimationFrame(() => focusColumnWhenOpen(drawer, login, deadline));
}

// Matches a data attribute by value without building a selector from it.
function withData(elements, name, value) {
  return [...elements].find((element) => element.getAttribute(name) === value) ?? null;
}

function step(list, current, delta) {
  const index = list.indexOf(current);
  if (index < 0) return list[0];
  return list[Math.min(list.length - 1, Math.max(0, index + delta))];
}

function onOptionKey(drawer, option, event) {
  const all = options(drawer);
  switch (event.key) {
    case "ArrowDown":
      focusOption(drawer, step(all, option, 1));
      break;
    case "ArrowUp":
      focusOption(drawer, step(all, option, -1));
      break;
    case "Home":
      focusOption(drawer, all[0]);
      break;
    case "End":
      focusOption(drawer, all.at(-1));
      break;
    case "Enter":
    case " ":
    case "ArrowRight": {
      option.click();
      const login = option.getAttribute("data-library-channel");
      if (login) focusColumnWhenOpen(drawer, login);
      break;
    }
    default:
      return;
  }
  event.preventDefault();
}

function onRowKey(drawer, row, event) {
  const all = rows(drawer);
  switch (event.key) {
    case "ArrowDown":
      step(all, row, 1)?.focus();
      break;
    case "ArrowUp":
      step(all, row, -1)?.focus();
      break;
    case "Home":
      all[0]?.focus();
      break;
    case "End":
      all.at(-1)?.focus();
      break;
    case "ArrowLeft":
    case "Escape": {
      const option = selectedOption(drawer);
      if (!option) return;
      focusOption(drawer, option);
      // Escape here means "back to the channels", not "close the drawer".
      event.stopPropagation();
      break;
    }
    case "Delete": {
      const button = row.parentElement?.querySelector("[data-row-delete]");
      if (button && button.getAttribute("aria-disabled") !== "true") button.click();
      break;
    }
    default:
      return;
  }
  event.preventDefault();
}

export function init(list) {
  const drawer = drawerOf(list);
  if (!drawer || drawers.has(drawer)) return;

  const listeners = new AbortController();
  drawer.addEventListener("keydown", (event) => {
    if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const target = event.target instanceof Element ? event.target : null;
    const option = target?.closest(`[data-channel-list] ${OPTION}`);
    if (option) {
      onOptionKey(drawer, option, event);
      return;
    }
    const row = target?.closest(`${COLUMN} ${ROW}`);
    if (row) onRowKey(drawer, row, event);
  }, { signal: listeners.signal });

  drawers.set(drawer, listeners);
}

/**
 * After a deletion, focus the row that took the deleted one's place, else the one before it,
 * else the column's Close (or All channels) button, else the channel list.
 * @param {Element} list the drawer list
 * @param {number} index the deleted row's index in its column
 * @param {string|null} recordingId the deleted recording, whose row may not be gone yet
 */
export function focusAfterDelete(list, index, recordingId = null, frames = 120) {
  const drawer = drawerOf(list);
  if (!drawer) return;
  // Wait until the library has dropped the deleted row and MudDialog has finished closing (it
  // hands focus back when it does); move focus only after both.
  const pending = drawer.ownerDocument.querySelector(".mud-dialog-container")
    || (recordingId && withData(drawer.querySelectorAll(`${COLUMN} [data-recording-id]`), "data-recording-id", recordingId));
  if (frames > 0 && pending) {
    requestAnimationFrame(() => focusAfterDelete(list, index, recordingId, frames - 1));
    return;
  }
  const all = rows(drawer);
  const target = all[index] ?? all[index - 1] ?? drawer.querySelector(`${COLUMN} [data-column-close]`)
    ?? selectedOption(drawer) ?? options(drawer)[0];
  if (!target) return;
  target.focus();
  // The dialog's focus return lands a little later, on the deleted row it remembered, which is
  // gone, so focus falls to the body; take it back for a few frames when that happens.
  let checks = 20;
  const keep = () => {
    const active = drawer.ownerDocument.activeElement;
    if (active === null || active === drawer.ownerDocument.body) target.focus();
    if (--checks > 0) requestAnimationFrame(keep);
  };
  requestAnimationFrame(keep);
}

export function scrollToTop(list) {
  if (list) list.scrollTop = 0;
}

/** Scrolls the current recording's row (aria-current) into view in its column, if it shows. */
export function scrollCurrentIntoView(list) {
  const drawer = drawerOf(list);
  drawer?.querySelector(`${COLUMN} ${ROW}[aria-current="true"]`)?.scrollIntoView?.({ block: "nearest" });
}

/**
 * Watches a row while its "Delete recording?" is open. The library removes the row as soon as
 * the recording is deleted, before .NET hears back from the dialog, so the row is put back where
 * it was, inert and stripped of its hooks, and collapses out over 150 ms (design system:
 * LibraryDrawer). Nothing moves under reduced motion. Cancelling disarms it.
 * @param {Element} list the drawer list
 * @param {string} recordingId the recording the dialog would delete
 */
export function armRowCollapse(list, recordingId) {
  const drawer = drawerOf(list);
  if (!drawer) return;
  disarmRowCollapse(list);
  const link = withData(drawer.querySelectorAll(`${COLUMN} [data-recording-id]`), "data-recording-id", recordingId);
  const item = link?.closest("[data-row-item]");
  const parent = item?.parentNode;
  if (!item || !parent) return;

  const height = item.getBoundingClientRect().height;
  const observer = new MutationObserver((records) => {
    const removal = records.find((record) => [...record.removedNodes].includes(item));
    if (!removal) return;
    disarmRowCollapse(list);
    collapseOut(item, parent, removal.nextSibling, height);
  });
  observer.observe(parent, { childList: true });
  collapses.set(drawer, observer);
}

export function disarmRowCollapse(list) {
  const drawer = drawerOf(list);
  if (!drawer) return;
  collapses.get(drawer)?.disconnect();
  collapses.delete(drawer);
}

function collapseOut(item, parent, next, height) {
  const view = parent.ownerDocument?.defaultView;
  const reduced = view?.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
  // A closed column (the channel's last recording) leaves nothing to collapse in.
  if (reduced || !parent.isConnected || typeof item.animate !== "function") return;

  // Blazor has let go of this element, so putting it back does not disturb its bookkeeping.
  item.setAttribute("data-collapsing", "");
  item.setAttribute("aria-hidden", "true");
  item.removeAttribute("role");
  item.removeAttribute("data-row-item");
  item.inert = true;
  for (const element of item.querySelectorAll("[data-recording-id]")) element.removeAttribute("data-recording-id");
  parent.insertBefore(item, next?.parentNode === parent ? next : null);

  const animation = item.animate(
    [
      { height: `${height}px`, opacity: 1 },
      { height: "0px", marginBottom: "0px", opacity: 0 },
    ],
    { duration: COLLAPSE_MS, easing: "cubic-bezier(0.4, 0, 0.2, 1)" },
  );
  const remove = () => item.remove();
  animation.finished.then(remove, remove);
}

export function dispose(list) {
  const drawer = drawerOf(list);
  if (!drawer) return;
  disarmRowCollapse(list);
  drawers.get(drawer)?.abort();
  drawers.delete(drawer);
}
