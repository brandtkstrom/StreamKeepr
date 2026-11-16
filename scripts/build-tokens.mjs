#!/usr/bin/env node
// Dev-only: generates src/StreamKeepr/wwwroot/css/tokens.css from design-system/tokens.json
// (ADR 0012). Node never enters the runtime image; the generated CSS is committed, and
// tests/js/tokens.test.js fails when it no longer matches this script's output.
//
//   node scripts/build-tokens.mjs        (or `npm run build:tokens` in tests/js)

import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const GROUPS = ["spacing", "radius", "shadow", "size", "opacity"];
const REFERENCE = /^\{([a-z0-9-]+)\}$/;

/**
 * Builds tokens.css: one :root block of custom properties.
 * - colour tokens use their dark value; a `{name}` reference becomes `var(--name)`;
 * - spacing, radius, shadow, size and opacity tokens are copied as they are;
 * - each type style becomes a `font` shorthand, `--text-<name>`, plus
 *   `--text-<name>-letter-spacing`, which the shorthand cannot carry.
 * Throws when two tokens would write the same custom property (a colour `text-primary` and a
 * type style `primary`, say), since the later one would silently win.
 * @param {object} tokens the parsed tokens.json
 * @returns {string}
 */
export function buildTokensCss(tokens) {
  const names = new Set();
  const declaration = (name, value, usage) => {
    if (names.has(name)) throw new Error(`tokens.json defines --${name} more than once`);
    names.add(name);
    const note = usage ? ` /* ${usage.replaceAll("*/", "* /")} */` : "";
    return `  --${name}: ${value};${note}`;
  };
  const lines = [
    "/* Generated from design-system/tokens.json by scripts/build-tokens.mjs. Do not edit. */",
    ":root {",
  ];

  for (const token of tokens.color?.tokens ?? []) {
    const value = token.value.dark;
    const reference = REFERENCE.exec(value);
    lines.push(declaration(token.name, reference ? `var(--${reference[1]})` : value, token.usage));
  }

  for (const group of GROUPS) {
    for (const token of tokens[group]?.tokens ?? []) lines.push(declaration(token.name, token.value));
  }

  const type = tokens.type;
  if (type?.families?.sans) lines.push(declaration("font-sans", type.families.sans));
  for (const group of type?.groups ?? []) {
    for (const style of group.styles) {
      const family = `var(--font-${group.family ?? "sans"})`;
      lines.push(
        declaration(
          `text-${style.name}`,
          `${style.fontStyle} ${style.fontWeight} ${style.fontSize}/${style.lineHeight} ${family}`,
        ),
      );
      lines.push(declaration(`text-${style.name}-letter-spacing`, style.letterSpacing));
    }
  }

  lines.push("}", "");
  return lines.join("\n");
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = new URL("../", import.meta.url);
  const tokens = JSON.parse(readFileSync(new URL("design-system/tokens.json", root), "utf8"));
  writeFileSync(new URL("src/StreamKeepr/wwwroot/css/tokens.css", root), buildTokensCss(tokens));
}
