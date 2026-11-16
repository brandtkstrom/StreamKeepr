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

  // Each test imports a fresh module, which adds document listeners; remove them afterwards
  // so earlier modules do not answer later tests' clicks.
  let added;

  beforeEach(async () => {
    vi.resetModules();
    added = [];
    const addEventListener = document.addEventListener.bind(document);
    vi.spyOn(document, "addEventListener").mockImplementation((type, listener, options) => {
      added.push([type, listener, options]);
      addEventListener(type, listener, options);
    });
    modal = makeModal();
    await import("../../src/StreamKeepr/wwwroot/js/reconnect.js");
  });

  afterEach(() => {
    vi.mocked(document.addEventListener).mockRestore();
    for (const [type, listener, options] of added) {
      document.removeEventListener(type, listener, options);
    }
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

  it.each(["failed", "paused", "resume-failed"])(
    "names the modal by the title of the state it shows (%s)",
    (state) => {
      emit(modal, state);

      expect(modal.getAttribute("aria-labelledby")).toBe(`sk-reconnect-title-${state}`);
    },
  );

  describe("buttons served without the circuit", () => {
    let reload;

    beforeEach(() => {
      reload = vi.fn();
      vi.stubGlobal("location", { reload });
    });

    afterEach(() => {
      vi.unstubAllGlobals();
      delete window.Blazor;
    });

    function button(hook) {
      const element = document.createElement("button");
      element.setAttribute(hook, "");
      element.innerHTML = "<span>label</span>";
      document.body.append(element);
      return element;
    }

    it("retries from any [data-reconnect-retry], even when a component stops propagation", async () => {
      window.Blazor = { reconnect: vi.fn().mockResolvedValue(true), resumeCircuit: vi.fn() };
      const retry = button("data-reconnect-retry");
      retry.addEventListener("click", (event) => event.stopPropagation());

      retry.querySelector("span").click();
      await Promise.resolve();

      expect(window.Blazor.reconnect).toHaveBeenCalledOnce();
      expect(reload).not.toHaveBeenCalled();
    });

    it("reloads when the server is reachable but the circuit cannot be resumed", async () => {
      window.Blazor = {
        reconnect: vi.fn().mockResolvedValue(false),
        resumeCircuit: vi.fn().mockResolvedValue(false),
      };

      button("data-reconnect-retry").click();
      await vi.waitFor(() => expect(reload).toHaveBeenCalledOnce());
      expect(window.Blazor.resumeCircuit).toHaveBeenCalledOnce();
    });

    it("resumes from [data-reconnect-resume] and closes the modal", async () => {
      window.Blazor = { reconnect: vi.fn(), resumeCircuit: vi.fn().mockResolvedValue(true) };
      emit(modal, "paused");

      button("data-reconnect-resume").click();

      await vi.waitFor(() => expect(modal.close).toHaveBeenCalled());
      expect(reload).not.toHaveBeenCalled();
    });

    it("shows resume-failed, named by its title, when resuming throws", async () => {
      window.Blazor = { reconnect: vi.fn(), resumeCircuit: vi.fn().mockRejectedValue(new Error("down")) };
      modal.classList.add("components-reconnect-paused");
      emit(modal, "paused");

      button("data-reconnect-resume").click();

      await vi.waitFor(() =>
        expect(modal.classList.contains("components-reconnect-resume-failed")).toBe(true),
      );
      expect(modal.getAttribute("aria-labelledby")).toBe("sk-reconnect-title-resume-failed");
      expect(document.body.dataset.skReconnect).toBe("resume-failed");
    });

    it("reloads from [data-reconnect-reload]", () => {
      button("data-reconnect-reload").click();

      expect(reload).toHaveBeenCalledOnce();
    });
  });
});
