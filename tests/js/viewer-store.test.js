import { afterEach, describe, expect, it, vi } from "vitest";
import {
  publishViewers,
  resetViewerStore,
  subscribeViewers,
  viewersFor,
} from "/js/viewer-store.js";

const data = (recordingId) => ({ recordingId, durationSec: 60, samples: [{ at: 0, count: 5 }] });

describe("viewer-store.js", () => {
  afterEach(() => resetViewerStore());

  it("gives the latest publish for its recording to islands that start later", () => {
    publishViewers(data("a"));

    expect(viewersFor("a")).toEqual(data("a"));
    expect(viewersFor("b")).toBeNull();
  });

  it("keeps only the latest recording's samples", () => {
    publishViewers(data("a"));
    publishViewers(data("b"));

    expect(viewersFor("a")).toBeNull();
    expect(viewersFor("b")).toEqual(data("b"));
  });

  it("tells subscribers about every later publish until they unsubscribe", () => {
    const listener = vi.fn();
    const unsubscribe = subscribeViewers(listener);
    publishViewers(data("a"));
    unsubscribe();
    publishViewers(data("b"));

    expect(listener).toHaveBeenCalledTimes(1);
    expect(listener).toHaveBeenCalledWith(data("a"));
  });

  it("ignores a publish without a recording", () => {
    publishViewers({ samples: [] });

    expect(viewersFor(undefined)).toBeNull();
  });
});
