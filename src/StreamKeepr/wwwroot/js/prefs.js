const ONE_YEAR_SECONDS = 31_536_000;
const MAX_COOKIE_BYTES = 4096;

function readCookie(name) {
  const prefix = `${name}=`;
  const cookies = document.cookie ? document.cookie.split("; ") : [];
  for (const cookie of cookies) {
    if (!cookie.startsWith(prefix)) continue;
    const value = cookie.slice(prefix.length);
    try {
      return decodeURIComponent(value);
    } catch {
      return value;
    }
  }
  return null;
}

function writeCookie(name, value) {
  const secure = window.location.protocol === "https:" ? "; Secure" : "";
  document.cookie =
    `${name}=${encodeURIComponent(value)}; Path=/; SameSite=Lax; ` +
    `Max-Age=${ONE_YEAR_SECONDS}${secure}`;
}

function getTimeZone() {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
}

function setTimeZone(timeZone) {
  if (typeof timeZone !== "string" || timeZone.trim() === "") return false;
  writeCookie("sk-tz", timeZone.trim());
  return true;
}

function getUi() {
  const raw = readCookie("sk-ui");
  if (raw === null) return null;
  try {
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === "object" ? parsed : null;
  } catch {
    return null;
  }
}

function writeUi(state) {
  if (!state || typeof state !== "object") return false;
  const json = JSON.stringify(state);
  // Cookie limits apply to the encoded header value, not the raw JSON.
  const encoded = encodeURIComponent(json);
  if (new TextEncoder().encode(encoded).length > MAX_COOKIE_BYTES) return false;
  writeCookie("sk-ui", json);
  return true;
}

if (readCookie("sk-tz") === null) {
  setTimeZone(getTimeZone());
}

window.skPrefs = {
  readCookie,
  writeCookie,
  getUi,
  writeUi,
  getTimeZone,
  setTimeZone,
};
