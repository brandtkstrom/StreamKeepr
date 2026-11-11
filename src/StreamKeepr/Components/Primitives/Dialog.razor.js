// Collocated dialog module. showModal supplies the native focus trap; this
// module remembers the invoker, turns Esc into a .NET close request, and
// restores focus when the dialog actually closes.
const states = new WeakMap();

function focusable(dialog) {
  return Array.from(
    dialog.querySelectorAll(
      'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    ),
  );
}

function cleanup(dialog) {
  const state = states.get(dialog);
  if (!state) return null;

  dialog.removeEventListener("cancel", state.onCancel);
  states.delete(dialog);
  return state;
}

function restoreFocus(state) {
  const previous = state?.previous;
  if (previous instanceof HTMLElement && previous.isConnected) {
    previous.focus();
  }
}

export function open(dialog, dotNetRef) {
  if (!dialog || typeof dialog.showModal !== "function" || dialog.open) return;

  const previous =
    document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;

  const onCancel = (event) => {
    event.preventDefault();
    void dotNetRef?.invokeMethodAsync("OnDialogCancel").catch(() => {});
  };

  dialog.addEventListener("cancel", onCancel);
  states.set(dialog, { previous, onCancel });

  dialog.showModal();

  const target =
    dialog.querySelector("[autofocus]") ?? focusable(dialog)[0] ?? dialog;
  target.focus();
}

export function close(dialog) {
  if (!dialog) return;

  const state = states.get(dialog);
  if (dialog.open) dialog.close();
  cleanup(dialog);
  restoreFocus(state);
}

export function dispose(dialog) {
  if (!dialog) return;

  const state = states.get(dialog);
  cleanup(dialog);
  if (dialog.open) dialog.close();
  restoreFocus(state);
}
