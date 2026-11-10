# Part 1 includes a minimal Blazor shell

## Status

Accepted

## Context

Part 1's `Program.cs` depends on `StreamKeepr.Components.App` and `AddRazorComponents`, which are Part 2 work. Without a shell, Part 1 is not independently buildable or runnable, and the migration route's first checkpoint cannot be verified.

## Decision

The backend foundation includes a minimal Blazor Web App shell: `App.razor`, `Routes.razor`, and a placeholder home page sufficient to build, run, and test the host. The full UI remains Part 2. Part 1 is then buildable and runnable on its own; the first migration checkpoint uses this shell.

## Consequences

- A small amount of UI scaffolding lands earlier than Part 2.
- The full component tree, design system, and features remain Part 2 tasks.
- The migration route's first checkpoint can prove the Web SDK, host, health endpoint, and test seam.
