# Adopt MudBlazor through an incremental, test-guarded migration

## Status

Accepted (2026-11-15). Shaped in a design grilling session; the visual design is the StreamKeepr design system (MudBlazor 9, colourful dark theme, Roboto) exported to `design-system/`.

## Context

The UI is about 30 hand-built Razor components styled by `components.css` and `sk-*` classes, plus three JS-owned islands (player, stats, chat) whose modules own their DOM after the first render. The new design replaces the hand-built primitives with MudBlazor components, re-themes the app from MudBlazor's palette and typography, and adds new behaviour: a responsive shell, a default Home view, a settings sheet that never interrupts playback, and more.

The existing tests are coupled to today's markup: 22 bUnit test files assert `sk-*` classes, and the Playwright journeys mix `data-sk` hooks with class selectors. `main` keeps moving while this work happens, and the binding contracts in `docs/*-contract.md` describe today's markup.

## Decision

- **Incremental migration.** MudBlazor reaches `main` in small PRs that each keep the app releasable. A foundation PR adds the package, `MudTheme`, providers and Roboto without changing visible markup, and re-points today's `sk-*` CSS variables at the new palette, so surfaces not yet migrated already look close to the new design. Surface PRs then follow one at a time: shell and app bar, library drawer, Home, Settings, Recording details, then the JS islands.
- **Pinned dependency.** `MudBlazor` is pinned to exactly `9.12.0`, the version the design system was checked against. Each upgrade is its own PR that re-runs the theme test, the UI tests and the E2E journeys.
- **One source of design values.** `design-system/tokens.json` is authoritative. `StreamKeeprTheme.cs` is hand-written from it and verified by a unit test that compares every `Palette` value and the typography override. `wwwroot/css/tokens.css` is generated from it by a dev-only script (Node is dev-only; the runtime image stays Node-free), and a drift test fails when the committed CSS no longer matches.
- **The repo's design system is authoritative.** After the export, design changes are PRs to `design-system/`. The claude.ai design-system artifact is re-synced from the repo for browsing and marked as synced from code, so it is never edited directly.
- **Layout behaviour ships with its surface; features ship alone.** Behaviour that cannot be separated from a surface's markup (responsive shell, bottom bar, rail on watch pages, full-width theater, avatar-only account, app-bar links, storage in the drawer footer, removing the schedule placeholder) lands in that surface's migration PR. Standalone features (Home rows, the settings sheet, the chat sync slider, empty-state actions, the viewer-count chart) get their own PRs after their surface is migrated.
- **Tests target roles, then `data-*` hooks, never styling classes.** Neither `sk-*` nor `mud-*` classes are asserted. A test-hardening PR moves every class-based selector to roles or hooks before the foundation PR, without changing the UI, so each migration PR that keeps behaviour proves parity against unchanged tests.
- **Contracts stay true at every merge.** The PR that changes a surface amends the contract that describes it (`testing-contract.md` in the hardening PR, `frontend-foundation-contract.md` in the foundation PR, then library, player and details-auth-settings with their surfaces).

## Considered options

- **One long-lived branch, merged once:** no mixed-look period, but an unreviewable diff that diverges from an active `main`.
- **Feature flag with both UIs:** the safest rollback, but every feature would be built twice for the duration.
- **C# theme as the only source**, with CSS reading `--mud-palette-*`: fewer files, but the StreamKeepr-only tokens (`rec`, `streamer-*`, `player-*`) would be maintained by hand in a second place, free to drift from the design system.
- **Build-time generation** of both the theme and the CSS: fully single-source, but a heavier build step to own for about 80 values.
- **Parity first, features later:** clean diffs, but the shell would be built twice (desktop parity, then the responsive rework).
- **Asserting MudBlazor classes in tests:** quick, but couples every test to MudBlazor's internal markup and breaks on upgrades.

## Consequences

- For a few weeks, migrated and unmigrated surfaces coexist. The palette re-point keeps them visually close; layout and component shapes still differ until each surface lands.
- `components.css`, the `Components/Primitives/` components and the Material Symbols webfont stay until the last surface that uses them is migrated, then are deleted.
- Changing a design value means editing `tokens.json`, regenerating `tokens.css` and updating `StreamKeeprTheme.cs`; CI fails if any of the three disagree.
- Tests can no longer find elements by how they look. New UI needs an accessible role and name, or a `data-*` hook.
