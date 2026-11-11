import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createProgress, progressUrl } from "../../src/StreamKeepr/wwwroot/js/player-progress.js";

describe("player-progress.js", () => {
  let save;
  let clear;
  let fetch;
  let media;
  let progress;

  beforeEach(() => {
    vi.useFakeTimers();
    save = vi.fn();
    clear = vi.fn();
    fetch = vi.fn(() => Promise.resolve({ status: 204 }));
    media = { position: 100, duration: 600, live: false };
    progress = createProgress({ save, clear, fetch, now: () => Date.now() });
    progress.track({
      recordingId: "r 1",
      position: () => media.position,
      duration: () => media.duration,
      live: () => media.live,
    });
  });

  afterEach(() => {
    progress.dispose();
    vi.useRealTimers();
  });

  it("saves every 15 s while playing and once on pause", () => {
    progress.playing();
    vi.advanceTimersByTime(14_999);
    expect(save).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(save).toHaveBeenLastCalledWith("r 1", 100);

    media.position = 130;
    vi.advanceTimersByTime(15_000);
    expect(save).toHaveBeenLastCalledWith("r 1", 130);

    media.position = 140;
    progress.paused();
    expect(save).toHaveBeenLastCalledWith("r 1", 140);
    vi.advanceTimersByTime(60_000);
    expect(save).toHaveBeenCalledTimes(3);
  });

  it("clears instead of saving near the end", () => {
    media.position = 571;
    progress.paused();

    expect(clear).toHaveBeenCalledExactlyOnceWith("r 1");
    expect(save).not.toHaveBeenCalled();
  });

  it("writes nothing while the duration is unknown, so a pause during loading keeps the stored position", () => {
    media.duration = NaN;
    progress.paused();
    progress.playing();
    vi.advanceTimersByTime(15_000);

    expect(save).not.toHaveBeenCalled();
    expect(clear).not.toHaveBeenCalled();
  });

  it("never writes while live", () => {
    media.live = true;
    progress.playing();
    vi.advanceTimersByTime(60_000);
    progress.paused();
    progress.flush();
    progress.restarted();

    expect(save).not.toHaveBeenCalled();
    expect(clear).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it("stops the cadence when playback goes live", () => {
    progress.playing();
    progress.live();
    vi.advanceTimersByTime(60_000);

    expect(save).not.toHaveBeenCalled();
  });

  it("clears on ended and on replay", () => {
    progress.ended();
    progress.restarted();

    expect(clear).toHaveBeenCalledTimes(2);
  });

  it("flushes over HTTP with keepalive and skips a flush within 2 s of a write", () => {
    progress.flush();

    expect(fetch).toHaveBeenCalledOnce();
    const [url, init] = fetch.mock.calls[0];
    expect(url).toBe(progressUrl("r 1"));
    expect(url).toMatch(/\/api\/recordings\/r%201\/progress$/);
    expect(init).toMatchObject({
      method: "PUT",
      keepalive: true,
      headers: { "Content-Type": "application/json" },
    });
    expect(JSON.parse(init.body)).toEqual({ positionSec: 100 });

    vi.advanceTimersByTime(1_999);
    progress.flush();
    expect(fetch).toHaveBeenCalledOnce();

    progress.paused();
    vi.advanceTimersByTime(1_000);
    progress.flush();
    expect(fetch).toHaveBeenCalledOnce();

    vi.advanceTimersByTime(1_000);
    progress.flush();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("does not flush once disposed or without a known duration", () => {
    media.duration = NaN;
    progress.flush();
    media.duration = 600;
    progress.dispose();
    progress.flush();

    expect(fetch).not.toHaveBeenCalled();
  });
});
