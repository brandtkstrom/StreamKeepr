import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  createPlayer,
  formatTimecode,
  HLS_CONFIG,
} from "../../src/StreamKeepr/Components/Features/Player/VideoPlayer.razor.js";

// Mirrors the data-sk hooks VideoPlayer.razor renders (VideoPlayerTests guards the markup side).
const MARKUP = `
  <div class="sk-player" data-sk-player aria-busy="true">
    <video data-sk="video"></video>
    <div class="sk-player-top"><div class="t"><b data-sk="title"></b><span data-sk="streamer"></span></div><span data-sk="chip"></span></div>
    <button data-sk="center" aria-label="Play"><span class="sk-icon">play_arrow</span></button>
    <div data-sk="spinner" hidden><span data-sk="spinner-text">Loading video</span></div>
    <div data-sk="overlay" role="alert" hidden><p data-sk="overlay-message"></p><button data-sk="retry">Retry</button><a href="/">Back</a></div>
    <div class="sk-seek" data-sk="seek" role="slider" tabindex="0" aria-disabled="true">
      <div class="track"><div data-sk="buffered"></div><div data-sk="played"></div></div>
      <div data-sk="ticks"></div><div data-sk="knob"></div><span data-sk="hover-time"></span>
    </div>
    <button data-sk="play" aria-label="Play"><span class="sk-icon">play_arrow</span></button>
    <button data-sk="back" disabled></button>
    <button data-sk="forward" disabled></button>
    <button data-sk="mute" aria-label="Mute"><span class="sk-icon">volume_up</span></button>
    <div data-sk="volume" role="slider" tabindex="0" aria-valuenow="100"><i data-sk="volume-level"></i></div>
    <span data-sk="current"></span><span data-sk="duration"></span>
    <button data-sk="speed" aria-expanded="false">1×</button>
    <div data-sk="speed-menu" role="menu" hidden>
      <button role="menuitemradio" data-rate="0.5" aria-checked="false"><span class="sk-icon"></span></button>
      <button role="menuitemradio" data-rate="1" aria-checked="true"><span class="sk-icon">check</span></button>
      <button role="menuitemradio" data-rate="1.5" aria-checked="false"><span class="sk-icon"></span></button>
      <button role="menuitemradio" data-rate="2" aria-checked="false"><span class="sk-icon"></span></button>
    </div>
  </div>`;

const hook = (name) => document.querySelector(`[data-sk="${name}"]`);

function fakeMedia(video) {
  const media = {
    paused: true,
    ended: false,
    currentTime: 0,
    duration: NaN,
    readyState: 0,
    ranges: [],
    seeks: [],
    canPlay: "",
  };

  Object.defineProperties(video, {
    paused: { configurable: true, get: () => media.paused },
    ended: { configurable: true, get: () => media.ended },
    duration: { configurable: true, get: () => media.duration },
    readyState: { configurable: true, get: () => media.readyState },
    currentTime: {
      configurable: true,
      get: () => media.currentTime,
      set: (value) => {
        media.currentTime = value;
        media.seeks.push(value);
      },
    },
    buffered: {
      configurable: true,
      get: () => ({
        length: media.ranges.length,
        start: (i) => media.ranges[i][0],
        end: (i) => media.ranges[i][1],
      }),
    },
  });
  video.volume = 1;
  video.muted = false;
  video.playbackRate = 1;
  video.play = vi.fn(() => {
    media.paused = false;
    video.dispatchEvent(new Event("play"));
    return Promise.resolve();
  });
  video.pause = vi.fn(() => {
    media.paused = true;
    video.dispatchEvent(new Event("pause"));
  });
  video.load = vi.fn();
  video.removeAttribute = vi.fn(HTMLElement.prototype.removeAttribute.bind(video));
  video.canPlayType = vi.fn(() => media.canPlay);

  media.metadata = (duration) => {
    media.duration = duration;
    media.readyState = 1;
    video.dispatchEvent(new Event("loadedmetadata"));
    video.dispatchEvent(new Event("durationchange"));
  };
  media.time = (seconds) => {
    media.currentTime = seconds;
    video.dispatchEvent(new Event("timeupdate"));
  };
  return media;
}

function fakeHlsClass() {
  const instances = [];

  class FakeHls {
    static Events = {
      MANIFEST_PARSED: "hlsManifestParsed",
      FRAG_LOADED: "hlsFragLoaded",
      ERROR: "hlsError",
    };
    static ErrorTypes = {
      NETWORK_ERROR: "networkError",
      MEDIA_ERROR: "mediaError",
      OTHER_ERROR: "otherError",
    };
    static isSupported = vi.fn(() => true);

    constructor(config) {
      this.config = config;
      this.handlers = new Map();
      this.attachMedia = vi.fn();
      this.loadSource = vi.fn();
      this.startLoad = vi.fn();
      this.recoverMediaError = vi.fn();
      this.swapAudioCodec = vi.fn();
      this.destroy = vi.fn();
      this.levels = [{}];
      instances.push(this);
    }

    on(event, handler) {
      this.handlers.set(event, handler);
    }

    off(event) {
      this.handlers.delete(event);
    }

    emit(event, data = {}) {
      this.handlers.get(event)?.(event, data);
    }
  }

  return { FakeHls, instances };
}

function view(overrides = {}) {
  return {
    recordingId: "r1",
    playlistUrl: "/media/ann/r1/index.m3u8",
    title: "Night chef special",
    streamerLogin: "ann",
    thumbnailUrl: null,
    state: "saved",
    durationSec: 120,
    resumeSec: null,
    chapters: [
      { startSeconds: 0, title: "Intro", game: "Food" },
      { startSeconds: 60, title: "Main course", game: "Food" },
      { startSeconds: 500, title: "Out of range", game: "Food" },
    ],
    ...overrides,
  };
}

function pointer(target, type, clientX) {
  target.dispatchEvent(new MouseEvent(type, { bubbles: true, clientX, button: 0 }));
}

function key(target, name) {
  const event = new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
}

describe("VideoPlayer.razor.js", () => {
  let media;
  let hls;
  let loadHls;
  let player;

  beforeEach(() => {
    vi.useFakeTimers();
    document.body.innerHTML = MARKUP;
    media = fakeMedia(hook("video"));
    hook("seek").getBoundingClientRect = () => ({ left: 0, width: 100, top: 0, height: 20 });
    hls = fakeHlsClass();
    loadHls = vi.fn(async () => hls.FakeHls);
    vi.stubGlobal("MediaSource", function MediaSource() {});
  });

  afterEach(() => {
    player?.dispose();
    player = null;
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.replaceChildren();
  });

  async function start(initial = view()) {
    player = createPlayer(document.querySelector("[data-sk-player]"), { loadHls });
    await player.load(initial);
    return player;
  }

  describe("engine and source", () => {
    it("lazily loads hls.js once with the contract config and never autoplays", async () => {
      await start();

      expect(loadHls).toHaveBeenCalledOnce();
      const [instance] = hls.instances;
      expect(instance.config).toMatchObject({
        enableWorker: true,
        lowLatencyMode: false,
        backBufferLength: 90,
        liveSyncDurationCount: 1,
        liveMaxLatencyDurationCount: Infinity,
        maxLiveSyncPlaybackRate: 1,
      });
      expect(HLS_CONFIG.liveSyncDurationCount).toBe(1);
      expect(instance.attachMedia).toHaveBeenCalledWith(hook("video"));
      expect(instance.loadSource).toHaveBeenCalledWith("/media/ann/r1/index.m3u8");
      expect(hook("video").play).not.toHaveBeenCalled();
      expect(hook("title").textContent).toBe("Night chef special");
      expect(hook("streamer").textContent).toBe("ann");
    });

    it("falls back to native HLS without importing hls.js", async () => {
      vi.unstubAllGlobals();
      media.canPlay = "maybe";

      await start();

      expect(loadHls).not.toHaveBeenCalled();
      expect(hook("video").getAttribute("src")).toBe("/media/ann/r1/index.m3u8");
      expect(hook("overlay").hidden).toBe(true);
    });

    it("explains an unsupported browser without offering Retry", async () => {
      vi.unstubAllGlobals();

      await start();

      expect(hook("overlay").hidden).toBe(false);
      expect(hook("overlay-message").textContent).toMatch(/can.t play/i);
      expect(hook("retry").hidden).toBe(true);
    });

    it("swaps the source for a new recording and releases the old engine", async () => {
      await start();
      const [first] = hls.instances;

      await player.load(view({ recordingId: "r2", playlistUrl: "/media/ann/r2/index.m3u8", title: "Second" }));

      expect(first.destroy).toHaveBeenCalledOnce();
      expect(hls.instances).toHaveLength(2);
      expect(hls.instances[1].loadSource).toHaveBeenCalledWith("/media/ann/r2/index.m3u8");
      expect(hook("title").textContent).toBe("Second");
      expect(loadHls).toHaveBeenCalledOnce();
    });

    it("updates the same recording without resetting playback", async () => {
      await start();
      media.metadata(120);
      media.time(42);

      player.update(view({ title: "Renamed", state: "partial" }));

      expect(hook("title").textContent).toBe("Renamed");
      expect(hls.instances).toHaveLength(1);
      expect(hls.instances[0].destroy).not.toHaveBeenCalled();
      expect(media.seeks).toEqual([]);
      expect(hook("chip").textContent).toContain("Partial");
    });

    it("starts finished recordings at their resume position", async () => {
      await start(view({ resumeSec: 30 }));
      media.metadata(120);

      expect(media.seeks).toEqual([30]);
    });

    it("releases the engine and its listeners on dispose", async () => {
      await start();
      const [instance] = hls.instances;

      player.dispose();
      player = null;
      hook("play").click();

      expect(instance.destroy).toHaveBeenCalledOnce();
      expect(hook("video").play).not.toHaveBeenCalled();
    });

    it("ignores a load that finishes after a newer one", async () => {
      let release;
      loadHls = vi.fn(() => new Promise((resolve) => (release = () => resolve(hls.FakeHls))));
      player = createPlayer(document.querySelector("[data-sk-player]"), { loadHls });

      const first = player.load(view());
      const second = player.load(view({ recordingId: "r2", playlistUrl: "/two.m3u8" }));
      release();
      await Promise.all([first, second]);

      expect(hls.instances.map((i) => i.loadSource.mock.calls[0][0])).toEqual(["/two.m3u8"]);
    });
  });

  describe("controls", () => {
    it("plays and pauses from the control bar and the centre button", async () => {
      await start();

      hook("center").click();
      expect(hook("video").play).toHaveBeenCalledOnce();
      expect(hook("play").getAttribute("aria-label")).toBe("Pause");
      expect(hook("center").hidden).toBe(true);
      expect(document.activeElement).toBe(hook("play"));

      hook("play").click();
      expect(hook("video").pause).toHaveBeenCalledOnce();
      expect(hook("play").getAttribute("aria-label")).toBe("Play");
      expect(hook("center").hidden).toBe(false);
    });

    it("shows replay when ended and restarts from zero", async () => {
      await start();
      media.metadata(120);
      media.ended = true;
      media.currentTime = 120;
      hook("video").dispatchEvent(new Event("ended"));

      expect(hook("center").querySelector(".sk-icon").textContent).toBe("replay");
      hook("center").click();

      expect(media.seeks).toContain(0);
      expect(hook("video").play).toHaveBeenCalled();
    });

    it("skips 10 seconds within bounds once metadata exists", async () => {
      await start();
      expect(hook("back").disabled).toBe(true);

      media.metadata(120);
      media.time(5);
      expect(hook("back").disabled).toBe(false);

      hook("back").click();
      expect(media.currentTime).toBe(0);
      media.currentTime = 115;
      hook("forward").click();
      expect(media.currentTime).toBe(120);
    });

    it("mutes and changes volume with the slider", async () => {
      await start();
      const slider = hook("volume");

      hook("mute").click();
      expect(hook("video").muted).toBe(true);
      expect(hook("mute").getAttribute("aria-label")).toBe("Unmute");

      // Lowering the volume keeps it muted; only raising it above 0 unmutes.
      key(slider, "ArrowLeft");
      expect(hook("video").volume).toBeCloseTo(0.95);
      expect(hook("video").muted).toBe(true);
      expect(slider.getAttribute("aria-valuenow")).toBe("95");
      key(slider, "ArrowRight");
      expect(hook("video").muted).toBe(false);

      for (let i = 0; i < 25; i++) key(slider, "ArrowLeft");
      expect(hook("video").volume).toBe(0);
      expect(slider.getAttribute("aria-valuenow")).toBe("0");

      key(slider, "ArrowRight");
      expect(hook("video").volume).toBeCloseTo(0.05);
      expect(hook("video").muted).toBe(false);
    });

    it("shows the current and total timecode", async () => {
      await start();
      media.metadata(3733);
      media.time(62);

      expect(hook("current").textContent).toBe("1:02");
      expect(hook("duration").textContent).toBe("1:02:13");
    });

    it("chooses a playback speed from the menu", async () => {
      await start();

      hook("speed").click();
      expect(hook("speed-menu").hidden).toBe(false);
      expect(hook("speed").getAttribute("aria-expanded")).toBe("true");

      hook("speed-menu").querySelector('[data-rate="1.5"]').click();

      expect(hook("video").playbackRate).toBe(1.5);
      expect(hook("speed").textContent).toBe("1.5×");
      expect(hook("speed-menu").hidden).toBe(true);
      expect(hook("speed-menu").querySelector('[data-rate="1.5"]').getAttribute("aria-checked")).toBe("true");
      expect(hook("speed-menu").querySelector('[data-rate="1"]').getAttribute("aria-checked")).toBe("false");
    });

    it("closes the speed menu with Escape and returns focus", async () => {
      await start();
      hook("speed").click();

      key(hook("speed-menu").querySelector('[data-rate="2"]'), "Escape");

      expect(hook("speed-menu").hidden).toBe(true);
      expect(document.activeElement).toBe(hook("speed"));
    });
  });

  describe("seek bar", () => {
    it("is a disabled slider until metadata, then exposes value semantics", async () => {
      await start();
      expect(hook("seek").getAttribute("aria-disabled")).toBe("true");

      media.metadata(3733);
      media.time(62);

      expect(hook("seek").getAttribute("aria-disabled")).toBe("false");
      expect(hook("seek").getAttribute("aria-valuemax")).toBe("3733");
      expect(hook("seek").getAttribute("aria-valuenow")).toBe("62");
      expect(hook("seek").getAttribute("aria-valuetext")).toBe("1:02 of 1:02:13");
    });

    it("previews while dragging and seeks once on release", async () => {
      await start();
      media.metadata(100);

      pointer(hook("seek"), "pointerdown", 20);
      pointer(hook("seek"), "pointermove", 40);
      pointer(hook("seek"), "pointermove", 60);
      expect(media.seeks).toEqual([]);
      expect(hook("played").style.width).toBe("60%");

      pointer(hook("seek"), "pointerup", 60);
      expect(media.seeks).toEqual([60]);
    });

    it("moves 5 seconds with the arrows and jumps with Home and End", async () => {
      await start();
      media.metadata(100);
      media.time(50);

      expect(key(hook("seek"), "ArrowRight").defaultPrevented).toBe(true);
      expect(media.currentTime).toBe(55);
      key(hook("seek"), "ArrowLeft");
      key(hook("seek"), "ArrowLeft");
      expect(media.currentTime).toBe(45);
      key(hook("seek"), "End");
      expect(media.currentTime).toBe(100);
      key(hook("seek"), "Home");
      expect(media.currentTime).toBe(0);
    });

    it("draws valid chapter ticks and names the chapter under the pointer", async () => {
      await start();
      media.metadata(120);

      const ticks = hook("ticks").querySelectorAll(".tick");
      expect([...ticks].map((t) => t.style.left)).toEqual(["50%"]);

      pointer(hook("seek"), "pointermove", 75);
      expect(hook("hover-time").textContent).toBe("1:30 · Main course");
      pointer(hook("seek"), "pointermove", 25);
      expect(hook("hover-time").textContent).toBe("0:30 · Intro");
    });

    it("shows the buffered range around the playhead", async () => {
      await start();
      media.metadata(100);
      media.ranges = [[0, 10], [40, 70]];
      media.time(50);
      hook("video").dispatchEvent(new Event("progress"));

      expect(hook("buffered").style.width).toBe("70%");
      expect(hook("played").style.width).toBe("50%");
    });
  });

  describe("errors", () => {
    const fatal = (type, details = "x") => ({ fatal: true, type, details });

    it("retries fatal network errors after 1, 2 and 4 seconds, then shows the alert", async () => {
      await start();
      const [instance] = hls.instances;
      const { NETWORK_ERROR } = hls.FakeHls.ErrorTypes;

      for (const delay of [1000, 2000, 4000]) {
        instance.emit("hlsError", fatal(NETWORK_ERROR));
        vi.advanceTimersByTime(delay - 1);
        const before = instance.startLoad.mock.calls.length;
        vi.advanceTimersByTime(1);
        expect(instance.startLoad.mock.calls.length).toBe(before + 1);
      }
      expect(hook("overlay").hidden).toBe(true);

      instance.emit("hlsError", fatal(NETWORK_ERROR));
      expect(hook("overlay").hidden).toBe(false);
      expect(hook("retry").hidden).toBe(false);
      expect(document.activeElement).toBe(hook("retry"));
    });

    it("requests the playlist again when it never loaded", async () => {
      await start();
      const [instance] = hls.instances;
      instance.levels = [];

      instance.emit("hlsError", fatal(hls.FakeHls.ErrorTypes.NETWORK_ERROR, "manifestLoadError"));
      vi.advanceTimersByTime(1000);

      expect(instance.startLoad).not.toHaveBeenCalled();
      expect(instance.loadSource).toHaveBeenCalledTimes(2);
    });

    it("a loaded fragment resets the attempt count", async () => {
      await start();
      const [instance] = hls.instances;
      const { NETWORK_ERROR } = hls.FakeHls.ErrorTypes;

      for (let round = 0; round < 3; round++) {
        instance.emit("hlsError", fatal(NETWORK_ERROR));
        instance.emit("hlsError", fatal(NETWORK_ERROR));
        vi.runOnlyPendingTimers();
        instance.emit("hlsFragLoaded");
      }

      expect(hook("overlay").hidden).toBe(true);
    });

    it("recovers media errors twice, then swaps the audio codec, then shows the alert", async () => {
      await start();
      const [instance] = hls.instances;
      const { MEDIA_ERROR } = hls.FakeHls.ErrorTypes;

      instance.emit("hlsError", fatal(MEDIA_ERROR));
      instance.emit("hlsError", fatal(MEDIA_ERROR));
      expect(instance.recoverMediaError).toHaveBeenCalledTimes(2);
      expect(instance.swapAudioCodec).not.toHaveBeenCalled();

      instance.emit("hlsError", fatal(MEDIA_ERROR));
      expect(instance.swapAudioCodec).toHaveBeenCalledOnce();
      expect(hook("overlay").hidden).toBe(true);

      instance.emit("hlsError", fatal(MEDIA_ERROR));
      expect(hook("overlay").hidden).toBe(false);
    });

    it("Retry before playback starts keeps the resume position", async () => {
      await start(view({ resumeSec: 30 }));
      hls.instances[0].emit("hlsError", fatal(hls.FakeHls.ErrorTypes.OTHER_ERROR));

      hook("retry").click();
      await vi.runAllTimersAsync();
      media.metadata(100);

      expect(media.seeks.at(-1)).toBe(30);
    });

    it("clears busy state and the pending spinner when the alert shows", async () => {
      await start();
      hls.instances[0].emit("hlsError", fatal(hls.FakeHls.ErrorTypes.OTHER_ERROR));
      vi.advanceTimersByTime(1000);

      expect(document.querySelector("[data-sk-player]").hasAttribute("aria-busy")).toBe(false);
      expect(hook("spinner").hidden).toBe(true);
    });

    it("a failed hls.js import is reported as a retryable failure and retried", async () => {
      const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
      loadHls = vi.fn().mockRejectedValueOnce(new Error("offline")).mockResolvedValue(hls.FakeHls);

      await start();
      expect(hook("overlay").hidden).toBe(false);
      expect(hook("retry").hidden).toBe(false);
      expect(warn).toHaveBeenCalled();

      hook("retry").click();
      await vi.runAllTimersAsync();

      expect(loadHls).toHaveBeenCalledTimes(2);
      expect(hls.instances).toHaveLength(1);
      expect(hook("overlay").hidden).toBe(true);
    });

    it("ignores non-fatal errors", async () => {
      await start();
      hls.instances[0].emit("hlsError", { fatal: false, type: "networkError" });

      expect(hook("overlay").hidden).toBe(true);
    });

    it("Retry reloads at the last position and resets attempts", async () => {
      await start();
      media.metadata(100);
      media.time(37);
      const { OTHER_ERROR } = hls.FakeHls.ErrorTypes;
      hls.instances[0].emit("hlsError", fatal(OTHER_ERROR));
      expect(hook("overlay").hidden).toBe(false);

      hook("retry").click();
      await vi.runAllTimersAsync();

      expect(hls.instances).toHaveLength(2);
      expect(hls.instances[0].destroy).toHaveBeenCalled();
      expect(hook("overlay").hidden).toBe(true);
      media.metadata(100);
      expect(media.seeks.at(-1)).toBe(37);
    });

    it("shows the spinner while waiting and after 500 ms of loading", async () => {
      await start();
      expect(hook("spinner").hidden).toBe(true);
      vi.advanceTimersByTime(500);
      expect(hook("spinner").hidden).toBe(false);

      hook("video").dispatchEvent(new Event("canplay"));
      expect(hook("spinner").hidden).toBe(true);
      hook("video").dispatchEvent(new Event("waiting"));
      expect(hook("spinner").hidden).toBe(false);
    });
  });

  it("formats timecodes like Format.Timecode", () => {
    expect(formatTimecode(0)).toBe("0:00");
    expect(formatTimecode(59.9)).toBe("0:59");
    expect(formatTimecode(62)).toBe("1:02");
    expect(formatTimecode(3733)).toBe("1:02:13");
    expect(formatTimecode(-4)).toBe("0:00");
    expect(formatTimecode(NaN)).toBe("0:00");
  });
});
