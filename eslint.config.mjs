import { createRequire } from "node:module";

// The dev-only dependencies live in tests/js so the runtime project never sees Node.
const require = createRequire(new URL("./tests/js/package.json", import.meta.url));
const js = require("@eslint/js");
const globals = require("globals");

export default [
  js.configs.recommended,
  {
    files: ["**/*.js"],
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: "module",
      globals: {
        ...globals.browser,
        ...globals.node,
      },
    },
    rules: {
      "no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" },
      ],
    },
  },
  {
    files: ["**/*.test.js"],
    languageOptions: {
      globals: {
        describe: "readonly",
        it: "readonly",
        expect: "readonly",
        beforeEach: "readonly",
        afterEach: "readonly",
        vi: "readonly",
      },
    },
  },
  {
    ignores: ["**/node_modules/**", "coverage/**"],
  },
];
