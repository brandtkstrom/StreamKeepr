// The Home rows' chevrons (design system: Home, Rules): mouse users page a row sideways by its
// visible width. Keyboard users never need them: focusing a card scrolls it into view.

/**
 * Scrolls `row` by its visible width, gliding unless the viewer asked for reduced motion.
 * @param {HTMLElement | null} row the row's list element.
 * @param {number} direction 1 for forward, -1 for back.
 */
export function scrollRow(row, direction) {
  if (!row) return;

  const reduced = row.ownerDocument.defaultView.matchMedia("(prefers-reduced-motion: reduce)").matches;
  row.scrollBy({ left: direction * row.clientWidth, behavior: reduced ? "auto" : "smooth" });
}
