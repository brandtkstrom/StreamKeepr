import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: {
    // Browser modules import shared helpers from the app's static root, /js/*. Map that
    // root onto the source wwwroot so Vitest resolves the same imports without a bundler.
    alias: {
      "/js/stats.js": fileURLToPath(
        new URL("../../src/StreamKeepr/wwwroot/js/stats.js", import.meta.url)),
      "/js/sparkline.js": fileURLToPath(
        new URL("../../src/StreamKeepr/wwwroot/js/sparkline.js", import.meta.url)),
    },
  },
  server: {
    fs: {
      allow: [fileURLToPath(new URL("../..", import.meta.url))],
    },
  },
  test: {
    environment: "jsdom",
    include: ["**/*.test.js"],
    restoreMocks: true,
  },
});
