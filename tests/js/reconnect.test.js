import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

function makeModal() {
  const modal = document.createElement("dialog");
  modal.id = "components-reconnect-modal";
  modal.showModal = vi.fn(function () {
    this.setAttribute("open", "");
  });
  modal.close = vi.fn(function () {
    this.removeAttribute("open");
  });
  Object.defineProperty(modal, "open", {
    configurable: true,
    get() {
      return this.hasAttribute("open");
    },
  });
  document.body.append(modal);
  return modal;
}

function emit(modal, state) {
  modal.dispatchEvent(
    new CustomEvent("components-reconnect-state-changed", { detail: { state } }),
  );
}

describe("reconnect.js", () => {
  let modal;

  beforeEach(async () => {
    vi.resetModules();
    modal = makeModal();
    await import("../../src/StreamKeepr/wwwroot/js/reconnect.js");
  });

  afterEach(() => {
    document.body.replaceChildren();
    delete document.body.dataset.skReconnect;
  });

  it("keeps a transient reconnect quiet so the drawer note shows and playback continues", () => {
    emit(modal, "show");
    expect(document.body.dataset.skReconnect).toBe("show");
    expect(modal.showModal).not.toHaveBeenCalled();

    emit(modal, "retrying");
    expect(document.body.dataset.skReconnect).toBe("retrying");
    expect(modal.showModal).not.toHaveBeenCalled();

    emit(modal, "hide");
    expect(document.body.dataset.skReconnect).toBe("hide");
  });

  it.each(["failed", "paused", "resume-failed"])("opens the modal for a hard loss (%s)", (state) => {
    emit(modal, "show");
    emit(modal, state);

    expect(modal.showModal).toHaveBeenCalledOnce();
    expect(document.body.dataset.skReconnect).toBe(state);

    emit(modal, "hide");
    expect(modal.close).toHaveBeenCalled();
  });
});
