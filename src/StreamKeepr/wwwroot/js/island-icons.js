// Inline SVG glyphs the island modules (and the settings sheet's now-playing toggle) draw or swap, copied from MudBlazor 9.12.0
// Icons.Material.Rounded.* so the island draws MudBlazor's icons without MudBlazor components.
// Razor renders every island's first glyph from the MudBlazor constant (StreamKeepr.Ui.IslandIcon);
// this copy holds only the glyphs a module sets itself, so add one here when a module needs it. IslandIconTests checks every entry against the constant of the
// same name, so an upgrade that changes a glyph fails there. Keep one entry per line.
export const ICONS = Object.freeze({
  PlayArrow: '<path d="M8 6.82v10.36c0 .79.87 1.27 1.54.84l8.14-5.18c.62-.39.62-1.29 0-1.69L9.54 5.98C8.87 5.55 8 6.03 8 6.82z"/>',
  Pause: '<path d="M8 19c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2s-2 .9-2 2v10c0 1.1.9 2 2 2zm6-12v10c0 1.1.9 2 2 2s2-.9 2-2V7c0-1.1-.9-2-2-2s-2 .9-2 2z"/>',
  Replay: '<path d="M12 5V2.21c0-.45-.54-.67-.85-.35l-3.8 3.79c-.2.2-.2.51 0 .71l3.79 3.79c.32.31.86.09.86-.36V7c3.73 0 6.68 3.42 5.86 7.29-.47 2.27-2.31 4.1-4.57 4.57-3.57.75-6.75-1.7-7.23-5.01-.07-.48-.49-.85-.98-.85-.6 0-1.08.53-1 1.13.62 4.39 4.8 7.64 9.53 6.72 3.12-.61 5.63-3.12 6.24-6.24C20.84 9.48 16.94 5 12 5z"/>',
  VolumeUp: '<path d="M3 10v4c0 .55.45 1 1 1h3l3.29 3.29c.63.63 1.71.18 1.71-.71V6.41c0-.89-1.08-1.34-1.71-.71L7 9H4c-.55 0-1 .45-1 1zm13.5 2c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 4.45v.2c0 .38.25.71.6.85C17.18 6.53 19 9.06 19 12s-1.82 5.47-4.4 6.5c-.36.14-.6.47-.6.85v.2c0 .63.63 1.07 1.21.85C18.6 19.11 21 15.84 21 12s-2.4-7.11-5.79-8.4c-.58-.23-1.21.22-1.21.85z"/>',
  VolumeOff: '<path d="M3.63 3.63c-.39.39-.39 1.02 0 1.41L7.29 8.7 7 9H4c-.55 0-1 .45-1 1v4c0 .55.45 1 1 1h3l3.29 3.29c.63.63 1.71.18 1.71-.71v-4.17l4.18 4.18c-.49.37-1.02.68-1.6.91-.36.15-.58.53-.58.92 0 .72.73 1.18 1.39.91.8-.33 1.55-.77 2.22-1.31l1.34 1.34c.39.39 1.02.39 1.41 0 .39-.39.39-1.02 0-1.41L5.05 3.63c-.39-.39-1.02-.39-1.42 0zM19 12c0 .82-.15 1.61-.41 2.34l1.53 1.53c.56-1.17.88-2.48.88-3.87 0-3.83-2.4-7.11-5.78-8.4-.59-.23-1.22.23-1.22.86v.19c0 .38.25.71.61.85C17.18 6.54 19 9.06 19 12zm-8.71-6.29l-.17.17L12 7.76V6.41c0-.89-1.08-1.33-1.71-.7zM16.5 12c0-1.77-1.02-3.29-2.5-4.03v1.79l2.48 2.48c.01-.08.02-.16.02-.24z"/>',
  Fullscreen: '<path d="M0 0h24v24H0V0z" fill="none"/><path d="M6 14c-.55 0-1 .45-1 1v3c0 .55.45 1 1 1h3c.55 0 1-.45 1-1s-.45-1-1-1H7v-2c0-.55-.45-1-1-1zm0-4c.55 0 1-.45 1-1V7h2c.55 0 1-.45 1-1s-.45-1-1-1H6c-.55 0-1 .45-1 1v3c0 .55.45 1 1 1zm11 7h-2c-.55 0-1 .45-1 1s.45 1 1 1h3c.55 0 1-.45 1-1v-3c0-.55-.45-1-1-1s-1 .45-1 1v2zM14 6c0 .55.45 1 1 1h2v2c0 .55.45 1 1 1s1-.45 1-1V6c0-.55-.45-1-1-1h-3c-.55 0-1 .45-1 1z"/>',
  FullscreenExit: '<path d="M0 0h24v24H0V0z" fill="none"/><path d="M6 16h2v2c0 .55.45 1 1 1s1-.45 1-1v-3c0-.55-.45-1-1-1H6c-.55 0-1 .45-1 1s.45 1 1 1zm2-8H6c-.55 0-1 .45-1 1s.45 1 1 1h3c.55 0 1-.45 1-1V6c0-.55-.45-1-1-1s-1 .45-1 1v2zm7 11c.55 0 1-.45 1-1v-2h2c.55 0 1-.45 1-1s-.45-1-1-1h-3c-.55 0-1 .45-1 1v3c0 .55.45 1 1 1zm1-11V6c0-.55-.45-1-1-1s-1 .45-1 1v3c0 .55.45 1 1 1h3c.55 0 1-.45 1-1s-.45-1-1-1h-2z"/>',
  PictureInPictureAlt: '<path d="M0 0h24v24H0V0z" fill="none"/><path d="M18 11h-6c-.55 0-1 .45-1 1v4c0 .55.45 1 1 1h6c.55 0 1-.45 1-1v-4c0-.55-.45-1-1-1zm5 8V4.98C23 3.88 22.1 3 21 3H3c-1.1 0-2 .88-2 1.98V19c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2zm-3 .02H4c-.55 0-1-.45-1-1V5.97c0-.55.45-1 1-1h16c.55 0 1 .45 1 1v12.05c0 .55-.45 1-1 1z"/>',
  PictureInPicture: '<path d="M0 0h24v24H0V0z" fill="none"/><path d="M18 7h-6c-.55 0-1 .45-1 1v4c0 .55.45 1 1 1h6c.55 0 1-.45 1-1V8c0-.55-.45-1-1-1zm3-4H3c-1.1 0-2 .9-2 2v14c0 1.1.9 1.98 2 1.98h18c1.1 0 2-.88 2-1.98V5c0-1.1-.9-2-2-2zm-1 16.01H4c-.55 0-1-.45-1-1V5.98c0-.55.45-1 1-1h16c.55 0 1 .45 1 1v12.03c0 .55-.45 1-1 1z"/>',
  Sync: '<path d="M.01 0h24v24h-24V0z" fill="none"/><path d="M12 4V2.21c0-.45-.54-.67-.85-.35l-2.8 2.79c-.2.2-.2.51 0 .71l2.79 2.79c.32.31.86.09.86-.36V6c3.31 0 6 2.69 6 6 0 .79-.15 1.56-.44 2.25-.15.36-.04.77.23 1.04.51.51 1.37.33 1.64-.34.37-.91.57-1.91.57-2.95 0-4.42-3.58-8-8-8zm0 14c-3.31 0-6-2.69-6-6 0-.79.15-1.56.44-2.25.15-.36.04-.77-.23-1.04-.51-.51-1.37-.33-1.64.34C4.2 9.96 4 10.96 4 12c0 4.42 3.58 8 8 8v1.79c0 .45.54.67.85.35l2.79-2.79c.2-.2.2-.51 0-.71l-2.79-2.79c-.31-.31-.85-.09-.85.36V18z"/>',
  Warning: '<path d="M4.47 21h15.06c1.54 0 2.5-1.67 1.73-3L13.73 4.99c-.77-1.33-2.69-1.33-3.46 0L2.74 18c-.77 1.33.19 3 1.73 3zM12 14c-.55 0-1-.45-1-1v-2c0-.55.45-1 1-1s1 .45 1 1v2c0 .55-.45 1-1 1zm1 4h-2v-2h2v2z"/>',
});

const SVG_ATTRIBUTES = 'viewBox="0 0 24 24" aria-hidden="true" focusable="false"';

function glyph(name) {
  const value = ICONS[name];
  if (value === undefined) throw new Error(`Unknown island icon: ${name}`);
  return value;
}

/** The decorative SVG for `name`, as markup for module-built DOM (the same shape Razor renders). */
export function iconMarkup(name) {
  return `<svg ${SVG_ATTRIBUTES} data-icon="${name}">${glyph(name)}</svg>`;
}

/**
 * Swaps the glyph of the icon inside `host` (or `host` itself when it is the icon). The glyphs
 * are constants from this module, never page data, so setting innerHTML is safe.
 */
export function setIcon(host, name) {
  const svg = host?.matches?.("svg[data-icon]") ? host : host?.querySelector("svg[data-icon]");
  if (!svg || svg.dataset.icon === name) return;
  svg.innerHTML = glyph(name);
  svg.dataset.icon = name;
}
