// Focus for the drawer's sort menu. MudMenu renders its items in the popover provider a frame or
// two after it opens, and leaves focus on its trigger after a pointer open, so the first Escape
// would only move focus into the list. Focusing the current choice on open follows the
// menu-button pattern: one Escape closes the menu and returns focus to the trigger.

/**
 * Focuses the first element matching `selector` once it exists.
 * @returns {Promise<boolean>} whether it was focused within `maxFrames` animation frames.
 */
export function focusWhenPresent(selector, doc = document, maxFrames = 30) {
  return new Promise((resolve) => {
    let frame = 0;
    const attempt = () => {
      const element = doc.querySelector(selector);
      if (element) {
        element.focus();
        resolve(true);
        return;
      }

      if (frame++ >= maxFrames) {
        resolve(false);
        return;
      }

      doc.defaultView.requestAnimationFrame(attempt);
    };

    attempt();
  });
}
