// Watch-progress cadence for the recording player (player contract §8). The player tells this
// module what happens; it decides when a position is saved through the circuit, cleared, or
// flushed over HTTP with keepalive. Nothing high-frequency crosses the circuit.

export const SAVE_INTERVAL_MS = 15_000;
export const FLUSH_SKIP_MS = 2_000;
// Mirrors ProgressStore.EndMargin (the source of truth, which enforces the same rule).
export const END_MARGIN_SEC = 30;

// Mirrors ProgressEndpoint.Route (the source of truth): PUT /api/recordings/{id}/progress.
export function progressUrl(recordingId) {
  return new URL(`api/recordings/${encodeURIComponent(recordingId)}/progress`, document.baseURI).href;
}

/**
 * @param {object} options
 * @param {(recordingId: string, seconds: number) => unknown} options.save circuit save (SaveProgress)
 * @param {(recordingId: string) => unknown} options.clear circuit clear (ClearProgress)
 * @param {(url: string, init: RequestInit) => unknown} [options.fetch] keepalive transport
 * @param {() => number} [options.now]
 */
export function createProgress({ save, clear, fetch = globalThis.fetch, now = () => Date.now() }) {
  let source = null; // { recordingId, position(), duration(), live() }
  let lastWrite = -Infinity;
  let timer = 0;

  const eligible = () => source && !source.live();

  function decide() {
    const position = source.position();
    const duration = source.duration();
    // Unknown duration (still loading, or a pause fired while switching recordings): skip.
    // Clearing here would wipe a stored resume position the viewer never moved past.
    if (!Number.isFinite(position) || !Number.isFinite(duration) || duration <= 0) return null;
    return position >= duration - END_MARGIN_SEC ? "clear" : Math.max(0, position);
  }

  function write() {
    if (!eligible()) return;
    const decision = decide();
    if (decision === null) return;
    lastWrite = now();
    if (decision === "clear") void clear(source.recordingId);
    else void save(source.recordingId, decision);
  }

  function stopTimer() {
    clearInterval(timer);
    timer = 0;
  }

  return {
    /** Starts tracking a recording; the previous one should be flushed first. */
    track(next) {
      stopTimer();
      source = next;
      lastWrite = -Infinity;
    },

    playing() {
      if (!eligible() || timer) return;
      timer = setInterval(write, SAVE_INTERVAL_MS);
    },

    paused() {
      stopTimer();
      write();
    },

    /** Called when playback becomes live: no writes until it is finite again. */
    live() {
      stopTimer();
    },

    ended() {
      stopTimer();
      if (!source) return;
      lastWrite = now();
      void clear(source.recordingId);
    },

    /** Replay from the start drops the stored position. */
    restarted() {
      if (!eligible()) return;
      lastWrite = now();
      void clear(source.recordingId);
    },

    /**
     * Keepalive flush for page hide, route change, and dispose. The server applies the
     * same clamp and end rule, so a position near the end clears there too.
     */
    flush() {
      if (!eligible() || now() - lastWrite < FLUSH_SKIP_MS || typeof fetch !== "function") return;
      const position = source.position();
      const duration = source.duration();
      if (!Number.isFinite(position) || !Number.isFinite(duration) || duration <= 0) return;
      lastWrite = now();
      try {
        const sent = fetch(progressUrl(source.recordingId), {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ positionSec: Math.max(0, position) }),
          keepalive: true,
        });
        // A deleted recording answers 404 and the page may be going away; the next save
        // covers a lost flush, so a failure is only worth a debug line.
        sent?.catch?.((error) => console.debug("Stream Keepr: progress flush failed.", error));
      } catch (error) {
        // fetch can throw synchronously while the page unloads.
        console.debug("Stream Keepr: progress flush failed.", error);
      }
    },

    dispose() {
      stopTimer();
      source = null;
    },
  };
}
