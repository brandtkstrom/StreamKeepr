// App-bar search: debounces typing so the circuit sees one query per pause, never one per
// keystroke. shortcuts.js asks for an immediate clear with an `sk:search-clear` event.
export const DEBOUNCE_MS = 250;

const state = new WeakMap();

export function init(input, dotNetRef, delay = DEBOUNCE_MS) {
  if (!input || state.has(input)) return;

  const entry = { timer: 0, dotNetRef };

  const send = (value) => {
    clearTimeout(entry.timer);
    entry.timer = 0;
    void entry.dotNetRef?.invokeMethodAsync("Search", value).catch(() => {});
  };

  entry.onInput = () => {
    clearTimeout(entry.timer);
    entry.timer = setTimeout(() => send(input.value.trim()), delay);
  };

  entry.onKeyDown = (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      send(input.value.trim());
    }
  };

  entry.onClear = () => {
    input.value = "";
    send("");
  };

  input.addEventListener("input", entry.onInput);
  input.addEventListener("keydown", entry.onKeyDown);
  input.addEventListener("sk:search-clear", entry.onClear);
  state.set(input, entry);
  input.dataset.skReady = "";
}

// Mirrors the URL into the field (back/forward, chips) without fighting the user's typing.
export function setValue(input, value) {
  if (!input) return;
  if (input.value.trim() === (value ?? "")) return;
  input.value = value ?? "";
}

export function dispose(input) {
  const entry = input && state.get(input);
  if (!entry) return;

  clearTimeout(entry.timer);
  input.removeEventListener("input", entry.onInput);
  input.removeEventListener("keydown", entry.onKeyDown);
  input.removeEventListener("sk:search-clear", entry.onClear);
  entry.dotNetRef = null;
  state.delete(input);
  delete input.dataset.skReady;
}
