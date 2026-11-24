import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CircuitDownError, createCircuit } from "/js/circuit.js";

function makeModal() {
  const modal = document.createElement("dialog");
  modal.id = "components-reconnect-modal";
  document.body.append(modal);
  return modal;
}

function report(modal, state) {
  modal.dispatchEvent(
    new CustomEvent("components-reconnect-state-changed", { detail: { state } }),
  );
}

describe("circuit.js", () => {
  let modal;
  let circuit;
  let reference;

  beforeEach(() => {
    modal = makeModal();
    circuit = createCircuit(document);
    reference = { invokeMethodAsync: vi.fn(() => Promise.resolve("done")) };
  });

  afterEach(() => {
    circuit.dispose();
    document.body.replaceChildren();
  });

  it("calls .NET while the circuit is up", async () => {
    await expect(circuit.invoke(reference, "Save", "rec-1", 12)).resolves.toBe("done");
    expect(reference.invokeMethodAsync).toHaveBeenCalledWith("Save", "rec-1", 12);
  });

  it.each(["show", "retrying", "failed", "paused", "resume-failed", "rejected"])(
    "rejects without calling .NET once the framework reports %s",
    async (state) => {
      report(modal, state);

      await expect(circuit.invoke(reference, "Save")).rejects.toBeInstanceOf(CircuitDownError);
      expect(reference.invokeMethodAsync).not.toHaveBeenCalled();
    },
  );

  it("calls .NET again once the framework hides the reconnect display", async () => {
    report(modal, "retrying");
    report(modal, "hide");

    await expect(circuit.invoke(reference, "Save")).resolves.toBe("done");
  });

  it("is current before a listener on the modal itself runs", async () => {
    // AppShell listens on the modal and calls .NET from there; the circuit must already know.
    const seen = [];
    modal.addEventListener("components-reconnect-state-changed", () => {
      seen.push(circuit.invoke(reference, "SetReconnectModalVisible", false).then(
        () => "sent",
        (error) => error.name,
      ));
    });

    report(modal, "show");
    report(modal, "hide");

    expect(await Promise.all(seen)).toEqual(["CircuitDownError", "sent"]);
  });

  it("treats a missing reference as a no-op, up or down", async () => {
    await expect(circuit.invoke(null, "Save")).resolves.toBeUndefined();
    report(modal, "show");
    await expect(circuit.invoke(undefined, "Save")).resolves.toBeUndefined();
  });

  it("names the call it did not send", async () => {
    report(modal, "show");

    await expect(circuit.invoke(reference, "SaveProgress")).rejects.toThrow(/SaveProgress/);
  });
});
