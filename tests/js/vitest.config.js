import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: {
    // Browser modules import shared helpers from the app's static root, /js/*. Map that
    // root onto the source wwwroot so Vitest resolves the same imports without a bundler.
    alias: [
      {
        find: /^\/js\/(.*)$/,
        replacement: `${fileURLToPath(new URL("../../src/StreamKeepr/wwwroot/js/", import.meta.url))}$1`,
      },
    ],
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
