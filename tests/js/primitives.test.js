import { afterEach, describe, expect, it, vi } from "vitest";
import * as dialogModule from "../../src/StreamKeepr/Components/Primitives/Dialog.razor.js";

function makeDotNetRef() {
  return { invokeMethodAsync: vi.fn().mockResolvedValue(undefined) };
}

describe("Dialog.razor.js", () => {
  afterEach(() => {
    document.body.replaceChildren();
  });

  function makeDialog() {
    const trigger = document.createElement("button");
    trigger.id = "trigger";
    const dialog = document.createElement("dialog");
    dialog.innerHTML = '<button id="cancel" autofocus>Cancel</button>';
    document.body.append(trigger, dialog);
    trigger.focus();

    dialog.showModal = vi.fn(function () {
      this.setAttribute("open", "");
    });
    dialog.close = vi.fn(function () {
      this.removeAttribute("open");
      this.dispatchEvent(new Event("close"));
    });
    Object.defineProperty(dialog, "open", {
      configurable: true,
      get() {
        return this.hasAttribute("open");
      },
    });

    return { dialog, trigger };
  }

  it("opens modally, focuses the autofocus target, and restores the invoker", () => {
    const { dialog, trigger } = makeDialog();

    dialogModule.open(dialog, makeDotNetRef());

    expect(dialog.showModal).toHaveBeenCalledOnce();
    expect(document.activeElement.id).toBe("cancel");

    dialogModule.close(dialog);

    expect(dialog.close).toHaveBeenCalledOnce();
    expect(document.activeElement).toBe(trigger);
  });

  it("turns Esc into a .NET close request", () => {
    const { dialog } = makeDialog();
    const dotNetRef = makeDotNetRef();
    dialogModule.open(dialog, dotNetRef);

    const cancel = new Event("cancel", { cancelable: true });
    dialog.dispatchEvent(cancel);

    expect(cancel.defaultPrevented).toBe(true);
    expect(dotNetRef.invokeMethodAsync).toHaveBeenCalledWith("OnDialogCancel");
  });
});
