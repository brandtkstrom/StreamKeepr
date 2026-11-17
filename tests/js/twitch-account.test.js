import { beforeAll, describe, expect, it } from "vitest";
import {
  init,
  resetConnecting,
  submitForm,
} from "../../src/StreamKeepr/Components/Features/Auth/TwitchAccount.razor.js";

const LINK = `
  <a class="sk-twitch" href="#login" data-twitch-connect>
    <svg class="mud-icon-root" aria-hidden="true" data-twitch-icon><path d=""/></svg><span class="lbl">Connect with Twitch</span>
  </a>`;

describe("TwitchAccount.razor.js", () => {
  beforeAll(() => init(document));

  function click(link, init = {}) {
    // A hash href keeps jsdom from attempting a real navigation.
    link.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ...init }));
  }

  it("shows the connecting state on a plain click without blocking the navigation", () => {
    document.body.innerHTML = LINK;
    const link = document.querySelector("a");
    let prevented = null;
    document.addEventListener("click", (event) => { prevented = event.defaultPrevented; }, { once: true });

    click(link);

    expect(link.getAttribute("aria-busy")).toBe("true");
    expect(link.querySelector(".sk-spin")).not.toBeNull();
    expect(link.querySelector("[data-spinner]")).not.toBeNull();
    expect(link.querySelector("[data-twitch-icon]")).toBeNull();
    expect(link.querySelector(".lbl").textContent).toBe("Waiting for Twitch…");
    expect(prevented).toBe(false);
  });

  it("ignores modified clicks that open a new tab", () => {
    document.body.innerHTML = LINK;
    const link = document.querySelector("a");

    click(link, { metaKey: true });
    click(link, { ctrlKey: true });

    expect(link.hasAttribute("aria-busy")).toBe(false);
  });

  it("resets to Connect with Twitch when the page comes back", () => {
    document.body.innerHTML = LINK;
    const link = document.querySelector("a");
    click(link);

    resetConnecting(link);

    expect(link.hasAttribute("aria-busy")).toBe(false);
    expect(link.querySelector("[data-spinner]")).toBeNull();
    expect(link.querySelector("svg[data-twitch-icon]")).not.toBeNull();
    expect(link.querySelector(".lbl").textContent).toBe("Connect with Twitch");
  });

  it("restores the label each link had, such as Reconnect Twitch", () => {
    document.body.innerHTML = LINK.replace("Connect with Twitch", "Reconnect Twitch");
    const link = document.querySelector("a");
    click(link);
    expect(link.querySelector(".lbl").textContent).toBe("Waiting for Twitch…");

    resetConnecting(link);

    expect(link.querySelector(".lbl").textContent).toBe("Reconnect Twitch");
  });

  it("submits the named antiforgery form for Disconnect, as its submit button would", () => {
    document.body.innerHTML = `<form id="sk-logout" method="post" action="/api/auth/logout" hidden></form>`;
    const form = document.getElementById("sk-logout");
    let submitted = 0;
    form.addEventListener("submit", (event) => { submitted++; event.preventDefault(); });

    submitForm("sk-logout");
    submitForm("missing");

    expect(submitted).toBe(1);
  });
});
