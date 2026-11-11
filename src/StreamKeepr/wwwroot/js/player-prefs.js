// Playback preferences (player contract §9): one versioned JSON object in localStorage under
// sk-player. Separate from sk-ui and sk-tz, never a cookie, never sent to the server. Any
// storage problem falls back to defaults in memory and never blocks playback.

export const PLAYER_PREFS_KEY = "sk-player";
export const PLAYER_PREFS_VERSION = 1;
export const SPEEDS = Object.freeze([0.5, 1, 1.5, 2]);
export const DEFAULT_PREFS = Object.freeze({ volume: 1, muted: false, speed: 1 });

/** Parses a stored value; invalid fields fall back one by one, unknown fields are ignored. */
export function parsePlayerPrefs(text) {
  let raw = null;
  try {
    raw = typeof text === "string" ? JSON.parse(text) : null;
  } catch {
    raw = null;
  }
  if (!raw || typeof raw !== "object" || raw.v !== PLAYER_PREFS_VERSION) return { ...DEFAULT_PREFS };

  return {
    volume: Number.isFinite(raw.volume) ? Math.min(Math.max(raw.volume, 0), 1) : DEFAULT_PREFS.volume,
    muted: typeof raw.muted === "boolean" ? raw.muted : DEFAULT_PREFS.muted,
    speed: SPEEDS.includes(raw.speed) ? raw.speed : DEFAULT_PREFS.speed,
  };
}

export function serializePlayerPrefs(prefs) {
  const { volume, muted, speed } = parsePlayerPrefs(
    JSON.stringify({ v: PLAYER_PREFS_VERSION, ...prefs }),
  );
  return JSON.stringify({ v: PLAYER_PREFS_VERSION, volume, muted, speed });
}

/** localStorage can be absent, disabled, or throw on access (private mode, sandboxed frames). */
export function defaultStorage() {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}

export function readPlayerPrefs(storage = defaultStorage()) {
  try {
    return parsePlayerPrefs(storage?.getItem(PLAYER_PREFS_KEY) ?? null);
  } catch {
    return { ...DEFAULT_PREFS };
  }
}

export function writePlayerPrefs(prefs, storage = defaultStorage()) {
  try {
    storage?.setItem(PLAYER_PREFS_KEY, serializePlayerPrefs(prefs));
  } catch {
    // Quota or disabled storage: the in-memory value still applies for this page.
  }
}
