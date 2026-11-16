// @vitest-environment node
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { buildTokensCss } from "../../scripts/build-tokens.mjs";

const root = new URL("../../", import.meta.url);
const read = (path) => readFileSync(new URL(path, root), "utf8");

describe("build-tokens.mjs", () => {
  it("keeps the committed tokens.css in step with tokens.json", () => {
    const tokens = JSON.parse(read("design-system/tokens.json"));

    // On failure, run `npm run build:tokens` in tests/js and commit the result.
    expect(read("src/StreamKeepr/wwwroot/css/tokens.css")).toBe(buildTokensCss(tokens));
  });

  it("writes colours, references, groups and type styles as :root variables", () => {
    const css = buildTokensCss({
      color: {
        tokens: [
          { name: "purple-accent-1", value: { dark: "#ea80fc" }, usage: "Colors.Purple.Accent1." },
          { name: "primary", value: { dark: "{purple-accent-1}" }, usage: "Key actions." },
        ],
      },
      spacing: { tokens: [{ name: "space-1", value: "4px" }] },
      radius: { tokens: [{ name: "radius-md", value: "8px" }] },
      shadow: { tokens: [{ name: "elevation-4", value: "0px 2px 4px -1px rgba(0,0,0,0.2)" }] },
      size: { tokens: [{ name: "appbar-height", value: "64px" }] },
      opacity: { tokens: [{ name: "hover-opacity", value: "0.08" }] },
      type: {
        families: { sans: "Roboto, Helvetica, Arial, sans-serif" },
        groups: [
          {
            name: "MudBlazor text",
            styles: [
              {
                name: "button",
                fontSize: "0.875rem",
                lineHeight: "1.75",
                fontWeight: 500,
                letterSpacing: "0.02857em",
                fontStyle: "normal",
                textTransform: "none",
              },
            ],
          },
        ],
      },
    });

    expect(css).toBe(
      [
        "/* Generated from design-system/tokens.json by scripts/build-tokens.mjs. Do not edit. */",
        ":root {",
        "  --purple-accent-1: #ea80fc; /* Colors.Purple.Accent1. */",
        "  --primary: var(--purple-accent-1); /* Key actions. */",
        "  --space-1: 4px;",
        "  --radius-md: 8px;",
        "  --elevation-4: 0px 2px 4px -1px rgba(0,0,0,0.2);",
        "  --appbar-height: 64px;",
        "  --hover-opacity: 0.08;",
        "  --font-sans: Roboto, Helvetica, Arial, sans-serif;",
        "  --text-button: normal 500 0.875rem/1.75 var(--font-sans);",
        "  --text-button-letter-spacing: 0.02857em;",
        "}",
        "",
      ].join("\n"),
    );
  });

  it("keeps a usage note from closing its CSS comment early", () => {
    const css = buildTokensCss({
      color: { tokens: [{ name: "rec", value: { dark: "#ff5252" }, usage: "REC */ dot" }] },
    });

    expect(css).toContain("  --rec: #ff5252; /* REC * / dot */");
  });
});
