import { describe, expect, it } from "vitest";
import { isLiveState } from "/js/recording-state.js";

describe("isLiveState", () => {
  it("is true while a recording is captured or reconnecting", () => {
    expect(isLiveState("recording")).toBe(true);
    expect(isLiveState("reconnecting")).toBe(true);
  });

  it("is false for finished, failed or missing states", () => {
    for (const state of ["saved", "partial", "failed", "", undefined, null, "Recording"]) {
      expect(isLiveState(state)).toBe(false);
    }
  });
});
