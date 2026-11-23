// JS→.NET calls for the interactive components. Every invokeMethodAsync goes through here.
//
// Blazor (10.0.5, beginInvokeDotNetFromJS) never handles the promise of its own send. While the
// circuit's connection is down that send rejects unhandled inside the framework ("Cannot send
// data if the connection is not in the 'Connected' State"), past any catch on the caller's
// promise, and the promise invokeMethodAsync returned never settles. So a call is made only
// while the circuit is up; otherwise it rejects with CircuitDownError, and the caller's own
// catch handles it like any other undelivered call.

// The framework's reconnect display reports every loss and recovery on this event, which
// reconnect.js also handles. "hide" is the only state in which the connection is up again.
const RECONNECT_EVENT = "components-reconnect-state-changed";

export class CircuitDownError extends Error {
  constructor(identifier) {
    super(`${identifier} was not sent: the circuit is not connected.`);
    this.name = "CircuitDownError";
  }
}

/** Tracks one page's circuit from the reconnect events that reach `target`. */
export function createCircuit(target) {
  // A component module loads through JS interop, so the circuit is up when this first runs.
  let up = true;
  const onReconnectState = (event) => {
    up = event.detail?.state === "hide";
  };
  // Capture, so the state is current before any listener on the modal itself calls .NET.
  target.addEventListener(RECONNECT_EVENT, onReconnectState, true);

  return {
    /**
     * Calls `identifier` on `reference`, or rejects with CircuitDownError while the circuit is
     * down instead of reaching the framework's unhandled send.
     */
    invoke(reference, identifier, ...args) {
      // No reference (a module not initialised yet, or disposed) is a no-op, as
      // `reference?.invokeMethodAsync(...)` was.
      if (!reference) return Promise.resolve(undefined);
      if (!up) return Promise.reject(new CircuitDownError(identifier));
      return reference.invokeMethodAsync(identifier, ...args);
    },
    dispose() {
      target.removeEventListener(RECONNECT_EVENT, onReconnectState, true);
    },
  };
}

const pageCircuit = createCircuit(document);

/** `reference.invokeMethodAsync(identifier, ...args)` that rejects while the circuit is down. */
export function callDotNet(reference, identifier, ...args) {
  return pageCircuit.invoke(reference, identifier, ...args);
}
