// Collocated menu module: outside-click dismissal, Esc handoff, and suppression
// of the browser's default arrow-key scrolling while the menu is open.
const states = new WeakMap();

function isOpen(trigger) {
  return trigger?.getAttribute("aria-expanded") === "true";
}

export function init(root, dotNetRef) {
  if (!root || states.has(root)) return;

  const trigger = root.querySelector("[data-menu-trigger]");
  const menu = root.querySelector('[role="menu"]');
  if (!trigger || !menu) return;

  const onTriggerKeyDown = (event) => {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
    }
  };

  const onDocumentPointerDown = (event) => {
    if (!isOpen(trigger) || root.contains(event.target)) return;
    void dotNetRef
      .invokeMethodAsync("CloseFromJs", false)
      .catch(() => {});
  };

  const onDocumentKeyDown = (event) => {
    if (!isOpen(trigger)) return;

    if (
      event.key === "ArrowDown" ||
      event.key === "ArrowUp" ||
      event.key === "Home" ||
      event.key === "End" ||
      event.key === "Escape"
    ) {
      event.preventDefault();
    }

    if (event.key === "Escape") {
      event.stopPropagation();
      void dotNetRef
        .invokeMethodAsync("CloseFromJs", true)
        .catch(() => {});
    }
  };

  trigger.addEventListener("keydown", onTriggerKeyDown);
  document.addEventListener("pointerdown", onDocumentPointerDown, true);
  document.addEventListener("keydown", onDocumentKeyDown, true);

  states.set(root, {
    trigger,
    onTriggerKeyDown,
    onDocumentPointerDown,
    onDocumentKeyDown,
  });
}

export function dispose(root) {
  const state = states.get(root);
  if (!state) return;

  state.trigger.removeEventListener("keydown", state.onTriggerKeyDown);
  document.removeEventListener("pointerdown", state.onDocumentPointerDown, true);
  document.removeEventListener("keydown", state.onDocumentKeyDown, true);
  states.delete(root);
}
