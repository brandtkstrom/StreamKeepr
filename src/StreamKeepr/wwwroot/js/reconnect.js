// Reconnect behaviour from the Blazor Web App template, adapted to the StreamKeepr shell
// (design system Reconnect card). The framework owns the modal and its state classes; this
// module mirrors the state onto <body>, opens the modal only for a hard loss, and retries,
// resumes or reloads. Every button it serves works while the circuit is down, so they are
// found by data hook through one document-level listener rather than Blazor events:
//   [data-reconnect-retry]   the modal's Retry and the "Live updates paused" alerts' Retry
//   [data-reconnect-resume]  the modal's Resume
//   [data-reconnect-reload]  the modal's Reload
const reconnectModal = document.getElementById("components-reconnect-modal");

// Mirrored in AppShell.razor.js (onReconnectState) and ReconnectModal.razor (one section each).
const HARD_LOSS = new Set(["failed", "paused", "resume-failed"]);

reconnectModal?.addEventListener(
  "components-reconnect-state-changed",
  handleReconnectStateChanged,
);
// Capture, so a component that stops propagation (MudButton does) cannot hide the click.
document.addEventListener("click", onClick, true);

function onClick(event) {
  const target = event.target instanceof Element ? event.target : null;
  if (target?.closest("[data-reconnect-retry]")) void retry();
  else if (target?.closest("[data-reconnect-resume]")) void resume();
  else if (target?.closest("[data-reconnect-reload]")) location.reload();
}

function setState(state) {
  if (document.body) document.body.dataset.skReconnect = state;
}

// Shows the hard loss's section (components.css keys on data-reconnect-shown) and names the
// dialog by that section's title (ReconnectModal.razor TitleId).
function showLoss(state) {
  if (!reconnectModal) return;
  reconnectModal.dataset.reconnectShown = state;
  reconnectModal.setAttribute("aria-labelledby", `sk-reconnect-title-${state}`);
}

function handleReconnectStateChanged(event) {
  const state = event.detail?.state;
  setState(state ?? "hide");

  // A transient reconnect ("show", "retrying") stays quiet: the drawer shows "Live updates
  // paused" from body[data-sk-reconnect] and playback keeps going. Only a hard circuit loss
  // raises the modal, so the alert and the modal are never both prominent.
  if (HARD_LOSS.has(state)) {
    showLoss(state);
    if (!reconnectModal.open) reconnectModal.showModal();
  } else if (state === "hide") {
    reconnectModal.close();
  }

  if (state === "failed") {
    document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
  } else if (state === "rejected") {
    location.reload();
  }
}

async function retry() {
  document.removeEventListener(
    "visibilitychange",
    retryWhenDocumentBecomesVisible,
  );

  if (!window.Blazor) {
    location.reload();
    return;
  }

  try {
    const successful = await window.Blazor.reconnect();
    if (successful === false) {
      // The server is reachable but the circuit is gone.
      const resumed = await window.Blazor.resumeCircuit();
      if (!resumed) location.reload();
      else reconnectModal?.close();
    }
  } catch {
    document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
  }
}

async function resume() {
  if (!window.Blazor) {
    location.reload();
    return;
  }

  try {
    const successful = await window.Blazor.resumeCircuit();
    if (!successful) location.reload();
    else reconnectModal?.close();
  } catch {
    reconnectModal?.classList.replace(
      "components-reconnect-paused",
      "components-reconnect-resume-failed",
    );
    setState("resume-failed");
    showLoss("resume-failed");
  }
}

async function retryWhenDocumentBecomesVisible() {
  if (document.visibilityState === "visible") await retry();
}
