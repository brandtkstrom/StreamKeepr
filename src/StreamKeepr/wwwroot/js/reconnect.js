// Reconnect dialog behaviour from the Blazor Web App template, adapted to the
// StreamKeepr shell. The framework owns the modal; this module only mirrors
// its state and retries/resumes the circuit.
const reconnectModal = document.getElementById("components-reconnect-modal");

if (reconnectModal) {
  reconnectModal.addEventListener(
    "components-reconnect-state-changed",
    handleReconnectStateChanged,
  );

  document
    .getElementById("components-reconnect-button")
    ?.addEventListener("click", retry);
  document
    .getElementById("components-resume-button")
    ?.addEventListener("click", resume);
}

function setState(state) {
  if (document.body) document.body.dataset.skReconnect = state;
}

function handleReconnectStateChanged(event) {
  const state = event.detail?.state;
  setState(state ?? "hide");

  if (state === "show" || state === "paused") {
    if (!reconnectModal.open) reconnectModal.showModal();
  } else if (state === "hide") {
    reconnectModal.close();
  } else if (state === "failed") {
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
      else reconnectModal.close();
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
    else reconnectModal.close();
  } catch {
    reconnectModal.classList.replace(
      "components-reconnect-paused",
      "components-reconnect-resume-failed",
    );
  }
}

async function retryWhenDocumentBecomesVisible() {
  if (document.visibilityState === "visible") await retry();
}
