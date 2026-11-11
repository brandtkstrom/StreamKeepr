// Connecting feedback for the top-bar "Connect with Twitch" link. The link is a full-page
// navigation to the OAuth endpoint and works without this module; a click only shows that
// Twitch is on its way. One delegated listener covers every render of the link.

const CONNECT = "[data-twitch-connect]";
let installed = false;

const IDLE_LABEL = "Connect with Twitch";
const BUSY_LABEL = "Waiting for Twitch…";

export function showConnecting(link) {
  if (link.getAttribute("aria-busy") === "true") return;
  link.setAttribute("aria-busy", "true");
  const spinner = document.createElement("span");
  spinner.className = "sk-spin";
  spinner.setAttribute("aria-hidden", "true");
  link.querySelector(".sk-icon")?.replaceWith(spinner);
  const label = link.querySelector(".lbl");
  if (label) label.textContent = BUSY_LABEL;
}

export function resetConnecting(link) {
  if (link.getAttribute("aria-busy") !== "true") return;
  link.removeAttribute("aria-busy");
  const icon = document.createElement("span");
  icon.className = "sk-icon";
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = "link";
  link.querySelector(".sk-spin")?.replaceWith(icon);
  const label = link.querySelector(".lbl");
  if (label) label.textContent = IDLE_LABEL;
}

function onClick(event) {
  if (event.defaultPrevented || event.button > 0 || event.metaKey || event.ctrlKey || event.shiftKey) return;
  const link = event.target instanceof Element ? event.target.closest(CONNECT) : null;
  if (link) showConnecting(link);
}

export function init(doc = document) {
  if (installed) return;
  installed = true;
  doc.addEventListener("click", onClick);
  doc.defaultView?.addEventListener("pageshow", onPageShow);
}

function onPageShow(event) {
  // Back from Twitch through the back/forward cache: the link must not stay "waiting".
  if (event.persisted) document.querySelectorAll(CONNECT).forEach(resetConnecting);
}
