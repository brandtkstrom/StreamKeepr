// The viewer samples ViewersPanel loads, shared with the Statistics overlay's Stream row and its
// copy action (design system: Viewers, StatsPanel). One module instance per page, so both islands
// see the same data without a second fetch. A subscriber gets every later publish; viewersFor
// gives the latest one for a recording, for an island that starts after it was published. Only
// the latest publish is kept: the page watches one recording, so moving to another lets the
// last one's samples go (going back loads them again).

let latest = null;
const listeners = new Set();

/** @param {{ recordingId: string, durationSec: number, samples: { at: number, count: number }[] }} data */
export function publishViewers(data) {
  if (!data?.recordingId) return;
  latest = data;
  for (const listener of [...listeners]) listener(data);
}

export function viewersFor(recordingId) {
  return latest?.recordingId === recordingId ? latest : null;
}

/** @returns {() => void} unsubscribe */
export function subscribeViewers(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Tests only: forget everything published. */
export function resetViewerStore() {
  latest = null;
  listeners.clear();
}
