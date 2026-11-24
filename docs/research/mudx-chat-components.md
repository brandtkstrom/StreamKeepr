# Research memo: MudX Chat components for the chat replay pane

Written against .NET 10 and the `aspnet:10.0.14` image. Not re-verified against .NET 11 after the 2026-11-23 upgrade; framework behaviour cited here may have changed.

Status: research memo, 2026-11-20.
Issue: none at research time. The request was "Modify the chat component to use the 'Chat' components from the MudX package. Full Blazor rewrite to include MudX. Chat messages should scroll smoothly."
Binding: no. This memo informs a decision and changes nothing. Adopting MudX means adding a dependency. That is a high-impact decision (`AGENTS.md`, Definitions) and needs an ADR or decision-digest entry. A "full Blazor rewrite" of the pane would also amend `docs/frontend-foundation-contract.md` §11, `docs/player-contract.md` (the `sk:chat`/`sk:playhead` bridge) and `design-system/components/ChatPane/README.md`.

**Method.** Package facts come from the NuGet v3 registration and search APIs and from the `.nuspec`, XML docs and static web assets inside the downloaded `.nupkg` files. Component facts come from the MudXtra/MudX GitHub source at the commit that the 9.14.1 nuspec names. MudBlazor facts come from its GitHub source, issues, PRs and v9 migration guide. Blazor facts come from Microsoft Learn (the .NET 10 view). Web-platform facts come from MDN, MDN browser-compat-data and CSSWG drafts. Everything was retrieved on **2026-11-20**, and the links are in §12. I also built two disposable probe projects in a scratch directory outside the repo to check restore compatibility (§5.2). Nothing was added to the repo except this memo. Repository citations are `path:line` at HEAD `a7300c9`. I read Serena memories `core` and `frontend/core` first. `ChatPane.razor` and `ChatPane.razor.js` were read with native `read` rather than Serena symbol tools, which is a fallback reported per `AGENTS.md`. No code was edited.

Facts are stated plainly with a citation. Inferences are marked **(inference)**.

## 1. Summary and recommendation

**Verdict:** MudX Chat *can* be installed alongside MudBlazor 9.12.0, but only at MudX **9.11.0**. It does not deliver the "scrolls smoothly" goal, and a full Blazor rewrite of the pane would conflict with binding repo decisions.

- "MudX" is the NuGet package **`MudX.MudBlazor.Extension`** (singular), from **MudXtra/MudX**, MIT, by "Versile and Contributors". The latest version is 9.14.1, published 2026-11-20. Its Chat components (`MudXChat`, `MudXChatBubble`, `MudXChatHeader`, `MudXChatFooter`) are MudBlazor's former `MudChat` family. MudBlazor removed them in v9 and moved them to MudX (§3).
- **Compatibility.** MudX 9.14.x requires `MudBlazor >= 9.14.0`, so restore fails against the repo's exact `[9.12.0]` pin with NU1605, which I observed. MudX **9.11.0** requires `MudBlazor >= 9.11.0` and builds against `[9.12.0]` on `net10.0`, which I also observed. Its Chat source and CSS match 9.14.1 (§5).
- **The components are thin and presentational.** Each one is a single `<div>` with CSS classes, three cascading values, and click and right-click callbacks. They have **no** message list, virtualization, auto-scroll, smooth scroll, scroll-position tracking or JS for chat (§4).
- **Smooth scrolling is a separate problem, and it lives in the JS module today.** The current pane rebuilds the whole log with `replaceChildren()` on every visible-set change, then jumps `scrollTop` with no animation (`ChatPane.razor.js:738,762`). MudX changes neither. Blazor's `<Virtualize>` on .NET 10 requires identical item heights and has no end-anchoring. `AnchorMode.End` exists only in the .NET 11 docs (§6).
- **A Blazor-rendered log conflicts with binding decisions.** Under Interactive Server, every revealed message would cross the circuit. The foundation contract says anything that changes more than once a second stays in JS (`docs/frontend-foundation-contract.md:163`), and the pane is a JS-owned island by contract and design system (`:159`; `design-system/components/ChatPane/README.md:5`).

**Recommendation (inference):** do not do a full Blazor rewrite onto MudX. If the goal is smooth scrolling, fix it in `ChatPane.razor.js` without a new dependency: append and prune rows instead of rebuilding them, and animate the follow scroll (§9, Option C). If the goal is MudX's bubble look, Option B is lower risk: keep the JS island and emit MudX's CSS classes, or the repo's own equivalent rules. Both options need a decision from you (§10).

## 2. What the repository already fixes

| Fact | Evidence |
|---|---|
| Blazor renders the pane once. JS owns the log, polling, sync controls and auto-scroll, and the pane uses no MudBlazor components. | `src/StreamKeepr/Components/Features/Chat/ChatPane.razor:11-15`, `:108` (`ShouldRender() => !_ready`) |
| The contract binds this ownership. The module listens to `sk:playhead` and "never crosses the circuit for time updates". Anything changing more than once a second stays in JS. | `docs/frontend-foundation-contract.md:159,163` |
| The design system says: "Keep `ChatPane.razor.js` owning the log, polling, auto-scroll and the toggle." | `design-system/components/ChatPane/README.md:5` |
| ADR 0012 pins MudBlazor to exactly 9.12.0, and each upgrade is its own PR. The JS islands migrate last. | `docs/adr/0012-mudblazor-incremental-migration.md:15-16`; `src/StreamKeepr/StreamKeepr.csproj:20` (`Version="[9.12.0]"`) |
| The target framework is `net10.0`, and the app renders `Routes` as `InteractiveServer`. | `StreamKeepr.csproj:4`; `src/StreamKeepr/Components/App.razor:24` |
| The player dispatches `sk:playhead` at most every 250 ms. | `src/StreamKeepr/Components/Features/Player/VideoPlayer.razor.js:936-938` |
| Live panes poll every 500 ms. The log keeps the newest 1000 rows, and the endpoint's default and max limits are 300 and 1000. | `ChatPane.razor.js:10,22`; `src/StreamKeepr/Hosting/ChatEndpoint.cs:13-14` |
| `render()` filters messages to `mt <= currentTime + 0.5`, sorts them, builds a key and returns early if nothing changed. Otherwise it calls **`log.replaceChildren()`**, rebuilds every row and gap, and sets `scrollTop = scrollHeight` while following. | `ChatPane.razor.js:716-764` (rebuild `:738`, jump `:762`) |
| Pause on scroll-up: following turns off once the log is 24 px or more from the bottom. While not following, new messages count up on "Jump to current time (N)", and jumping sets `scrollTop` instantly. | `ChatPane.razor.js:563-566,691-714` |
| Each row is a flat `div.sk-chat-row` holding an optional reply quote, badge chips (text labels, not images), the name coloured by channel colour or a contrast-checked Twitch colour, a colon, and text with native and third-party emotes. | `ChatPane.razor.js:775-824`, emotes `:826-901`, contrast `:99-104` |
| The log is a flex column, and `margin-top: auto` on the first child anchors short chat to the bottom. It sets no `scroll-behavior` and no `overflow-anchor`. Emote images have a fixed 28 px height. | `src/StreamKeepr/wwwroot/css/components.css:555-563,583` |
| An earlier memo rejected `<Virtualize>` for chat because of the circuit round-trip per reveal and variable-height rows. The library also rejected `<Virtualize>`. | `docs/research/twitch-chat-replay.md:381`; `docs/library-contract.md:87` |
| Accessibility today: the log is `role="log"` with `aria-live="off"`, and status is announced separately. | `ChatPane.razor:39-44`; `docs/research/twitch-chat-replay.md:426` |
| Tests coupled to the module: 47 Vitest cases in `tests/js/chat-pane.test.js` (1241 lines), plus bUnit `tests/StreamKeepr.Tests/Ui/ChatPaneTests.cs`. | `wc`/`grep -c` at HEAD |

## 3. What "MudX" is

### 3.1 Identity

| Field | Value | Source |
|---|---|---|
| NuGet id | **`MudX.MudBlazor.Extension`** (singular) | nuspec `<id>`; NuGet search |
| Title / authors | "MudX Extensions for MudBlazor" / "Versile and Contributors" | nuspec |
| Repository | `https://github.com/MudXtra/MudX`. 9.14.1 is built from commit `06e027e`, 9.11.0 from `292a53b`. | nuspec `<repository>` |
| Docs site | `https://mudx.org/` (the Chat page `https://mudx.org/docs/chat` returned 200) | nuspec `<projectUrl>`; `curl` |
| License | MIT (`<license type="expression">MIT`), with `requireLicenseAcceptance=true` | nuspec; GitHub API `license.spdx_id: MIT` |
| Latest | **9.14.1**, published 2026-11-20. Before it: 9.14.0 (2026-11-19), 9.11.0 (2026-11-18), 9.10.0 (2026-10-07), 9.5.0 (2026-06-24), 9.0.0 (2026-02-24) | NuGet registration |
| Downloads | 117,512 in total | NuGet search |
| Activity | Repo created 2025-04-03 and last pushed 2026-11-20. 54 stars, 4 forks, 2 open issues. Contributors: `versile2` (416 commits) and `mudxtra-bot` (11). | GitHub API |
| Origin of Chat | MudBlazor issue #12039, "Deprecate MudChat and move to MudXtra/MudX", closed 2025-12-20, notes that `versile2` originally submitted the component. PR #12151, "MudChat: Remove components in v9, redirect to MudX", merged 2025-12-20. The v9 migration guide lists `MudChat`, `MudChatBubble`, `MudChatHeader`, `MudChatFooter`, `ChatBubblePosition` and `ChatArrowPosition` as removed. | MudBlazor #12039, #12151, #12666 |
| MudBlazor 9.12.0 itself | It has no `Chat` types in `MudBlazor.xml` and no `chat` paths in the `v9.12.0` source tree. | nupkg XML; GitHub tree |

### 3.2 Disambiguation

| Package | Owner / repo | Relationship to "MudX Chat" |
|---|---|---|
| `MudX.MudBlazor.Extension` | MudXtra/MudX | **This is the one.** It ships `MudXChat*`. |
| `MudX.MudBlazor.Extensions` (plural) | same project | The README calls it "an older package line and is not the active package". Its last version is 8.9.1, and its published date is 1900-01-01, which is how NuGet shows an unlisted version. |
| `MudXComponents` 1.0.6 | licentia88/MudXComponentSolution | Unrelated, despite the similar name. |
| `MudBlazor.Extensions` 9.13.1 ("MudEx") | fgilde/MudBlazor.Extensions, MIT | Has no `Chat` types in its XML docs. Depends on MudBlazor 9.13.0. |
| `CodeBeam.MudBlazor.Extensions` 9.14.0 | CodeBeamOrg, MIT | Has no `Chat` types in its XML docs. Depends on MudBlazor 9.14.0. |

## 4. MudX Chat components (API and behaviour)

All four inherit `MudComponentBase`, so they get `Class`, `Style`, `Tag` and `UserAttributes`. `UserAttributes` is `[Parameter(CaptureUnmatchedValues = true)]` (MudBlazor `src/MudBlazor/Base/MudComponentBase.cs:64-66` @ v9.12.0), so `data-*` hooks and `aria-*` attributes pass through to the root `<div>`. The source below is `src/MudX/Components/MudXChat/*` @ `06e027e`. The Chat source and the CSS from `Mud Chat - Core` onward are identical in 9.11.0: the GitHub compare `292a53b...06e027e` touches no Chat or style files, and a `diff` of the two packages' `mudx.css` from that marker is empty.

| Component | Renders | Parameters | Events / fragments |
|---|---|---|---|
| `MudXChat` | One `<div class="mudx-chat …">` that wraps `ChildContent` in three nested `CascadingValue`s: variant, colour, arrow position (`MudXChat.razor:4-15`) | `Color` (default `Default`), `Variant` (default `Text`), `ChatPosition` (`Start`/`End`), `ArrowPosition` (`Top`/`Middle`/`Bottom`/`None`, default `Top`), `Elevation` (0), `Square`, `Dense`. `RightToLeft` is a cascading parameter. (`MudXChat.razor.cs:14-89`) | `ChildContent` |
| `MudXChatBubble` | One `<div>` with `@onclick`/`@oncontextmenu` through `AsNonRenderingEventHandler` (`MudXChatBubble.razor:4-8`) | `Color?`, `Variant?` (override the parent), plus cascading `ParentVariant`/`ParentColor`/`ParentArrowPosition`. It exposes `ElementReference`. (`MudXChatBubble.razor.cs:30-78`) | `ChildContent`, `OnClick`, `OnContextClick` (`MouseEventArgs`). It adds `mud-ripple` when a click handler is set (`:22-23`). |
| `MudXChatHeader` | `<div>` holding `ChildContent`, or `Name` plus `<time>@Time</time>` (`MudXChatHeader.razor:4-17`) | `Name`, `Time` (strings) | `ChildContent` |
| `MudXChatFooter` | `<div>` holding `ChildContent` or `Text` (`MudXChatFooter.razor:4-13`) | `Text` | `ChildContent` |

**Theming hooks.** The classes are `mudx-chat`, `mudx-chat-{start|end}`, `mudx-chat-arrow-*`, `mudx-chat-rtl`, `mud-dense`, `mud-square`, `mud-elevation-N`, `mudx-chat-bubble`, `mudx-chat-{variant}-{color}`, `mudx-chat-header` and `mudx-chat-footer`. The colours read `--mud-palette-*` variables, for example `.mudx-chat-filled-primary { background-color: var(--mud-palette-primary) }` (`staticwebassets/mudx.css:1018-1022` in the 9.14.1 nupkg). The layout is CSS grid (`.mudx-chat { display: grid }`, `:526-532`). Bubble defaults are `max-width: 90%`, `min-height: 2.75rem` and `padding: .5rem 1rem` (`:644-654`). Dense mode uses `min-height: calc(2.75rem/3)` and `padding: .1875rem 1rem` (`:535-538`).

**What it does not do.** No member of these components, and nothing in `mudx.js` or `modules/*.js`, deals with scrolling, lists or virtualization. A `grep -i chat` over the package JS found nothing, and the XML docs list only the members above. The docs page tells the consumer to own the transcript container: "place `role="log"`, `aria-live="polite"`, and `aria-relevant="additions"` on one outer transcript container" (`src/MudX.Docs/Components/Docs/Chat/ChatDocPage.razor:28-37` @ `06e027e`). The docs model is one `MudXChat` per speaker turn, with a header and one or more bubbles (`ChatTranscriptAccessibilityExample.razor:3-16`).

**Known issue.** MudX #60, closed 2026-06-22, reports a `TypeLoadException` in `MudXChatBubble` after upgrading MudBlazor to 9.5 with MudX 9.0.0. The failure was a missing `MudBlazor.VariantSourceGeneratorEnumExtensions` (MudX #60). The bubble has depended on MudBlazor internals before.

## 5. Compatibility

### 5.1 Declared dependencies (nuspec / registration)

| MudX version | TFMs | MudBlazor dependency | Works with `[9.12.0]`? |
|---|---|---|---|
| 9.14.1, 9.14.0 | net8.0, net9.0, net10.0, net11.0 | `>= 9.14.0` | **No** |
| 9.11.0 | net8.0, net9.0, net10.0 | `>= 9.11.0` | **Yes** (see 5.2) |
| 9.10.0 / 9.5.0 / 9.0.0 | net8.0, net9.0, net10.0 | `>= 9.10.0` / `>= 9.5.0` / `>= 9.0.0` | Yes by range (not probed) |

The ranges are open floors with no upper bound. The nuspec also excludes MudBlazor's `Build,Analyzers` assets.

### 5.2 Observed restore/build (disposable probe, outside the repo)

I built a `Microsoft.NET.Sdk.Razor` project on `net10.0` with `MudBlazor [9.12.0]`, the MudX package, and one `.razor` file that uses `MudXChat Dense`, `MudXChatHeader` and `MudXChatBubble data-sk="x"`.

- **MudX 9.14.1** failed: `error NU1605: Warning As Error: Detected package downgrade: MudBlazor from 9.14.0 to 9.12.0 … Probe -> MudBlazor (= 9.12.0)`. Microsoft Learn says the .NET SDK treats NU1605 as an error through `WarningsAsErrors` ([NU1605][nu1605]).
- **MudX 9.11.0** reported `Build succeeded.`

I did not run the probe at runtime. MudX 9.11.0 was compiled against MudBlazor 9.11.0, and given #60, runtime binding against 9.12.0 still needs a real render check **(inference)**.

### 5.3 Render mode, JS/CSS, services

- **No service registration.** MudX has no `AddMudX…` in the README's setup steps (README, steps 1-5, in the nupkg).
- **CSS and JS.** Either add `<MudXProvider />` or reference `_content/MudX.MudBlazor.Extension/_mudx.min.css` and `mudx.min.js` yourself (README step 3). The README's CSS name has a leading underscore, but the nupkg ships `staticwebassets/mudx.min.css` without one. The README looks wrong there **(inference)**. `MudXProvider` renders `<div class="mudx-provider" tabindex="-1"/>`. On first render it imports `mudxProvider.js`, which appends a `<link>` and a `<script>` at runtime and throws if that fails (`src/MudX/Providers/MudXProvider.razor:2`, `MudXProvider.razor.cs:22-39`; `staticwebassets/modules/mudxProvider.js:2-5`). Injecting the link after first render would show chat unstyled until the CSS loads. A static `<link>` in `App.razor`, next to MudBlazor's (`App.razor:15`), avoids that **(inference)**. The package JS references no CDN URLs, so `grep https://` found nothing. That keeps the "no CDN for app assets" rule (`docs/frontend-foundation-contract.md:162`).
- **Render mode.** The Chat components have no JS and no `OnAfterRender` logic. Bubbles take `@onclick`, and those clicks need interactivity. Rendering the components themselves works in any mode **(inference from source)**. In this app they would render under `InteractiveServer`.

## 6. Smooth scrolling: what exists

### 6.1 What MudX provides

Nothing (§4).

### 6.2 Blazor `<Virtualize>` on .NET 10 (the repo's TFM)

- On .NET 10 the docs say: "All rendered content items, including placeholder content, are of identical height", and that content items which vary in height prevent `Virtualize` from working correctly ([Learn: virtualization][virtualize], "Advanced styles and scroll detection"). The default `ItemSize` is 50, and items are measured after the initial render (same page, "Item size", ≤ .NET 10 moniker).
- `AnchorMode` (`End` "is useful for a chat or logging user experience"), `ScrollToItemAsync`, `InitialItemIndex` and running-average variable heights appear only under the **`aspnetcore-11.0`/`12.0`** monikers (same page).
- Inference: on `net10.0`, `<Virtualize>` cannot pin to the bottom or handle variable-height chat rows (wrapped text, replies, emotes). It also moves every scroll-window change through the circuit under Interactive Server. That matches the earlier rejection (`docs/research/twitch-chat-replay.md:381`).

### 6.3 Blazor per-message component cost

- Microsoft measured "around 0.06 ms per component instance" (in WebAssembly), and the docs advise inlining child components or using `RenderFragment`s for large lists. Their own example is a chat message list ([Learn: rendering performance][perf], "Avoid thousands of component instances").
- The docs say each non-fixed `[CascadingParameter]` "is substantially more expensive than a regular `[Parameter]`" (same page, "Ensure cascading parameters are fixed"). `MudXChat` cascades three values without `IsFixed` (`MudXChat.razor:7-9`).
- The docs advise avoiding attribute splatting for components "that render at scale" (same page). Every MudX component splats `UserAttributes`.
- For Interactive Server, the docs say the savings "are usually dwarfed by other costs, for example, the SignalR diff (DOM edits) transport" (same page, "Implement `SetParametersAsync` manually").
- Inference: 1000 kept rows means 2000 to 3000 component instances (one `MudXChat` and one `MudXChatBubble` per message, plus a header). Every reveal would then be a server render and a SignalR diff, at the 250 ms playhead cadence and the 500 ms poll.

### 6.4 Web-platform options (MDN / CSSWG)

| Mechanism | What the source says | Fit for this pane (inference) |
|---|---|---|
| `scroll-behavior: smooth` | Applies "when scrolling is triggered by the navigation or CSSOM scrolling APIs". User scrolls are not affected, and "User agents are allowed to ignore this property". Widely available since March 2022. ([MDN][scroll-behavior]) | The cheapest change. Assigning `scrollTop` is a CSSOM scroll, so `:762` would animate. Needs a `prefers-reduced-motion` override, as the repo already does elsewhere (`components.css:58`). |
| `Element.scrollTo({top, behavior})` | `behavior` is `smooth`, `instant` or `auto`, and `auto` follows `scroll-behavior`. Widely available since January 2020. ([MDN][scrollto]) | Per-call control: smooth when following, `instant` for seeks, jumps and the first load. |
| Scroll anchoring / `overflow-anchor` | Anchoring "adjusts the scroll position to compensate for the changes outside of the viewport". Changes to size, position or transform on the anchor's path suppress it ([MDN guide][anchoring]). The spec does not select an anchor while the scroller is "not scrolled away from the origin" (§2.1) and has no rule for a scroller sitting at its end ([CSSWG][anchoring-spec]). Supported in Chrome 56, Firefox 66 and Safari 27, and Baseline since September 2026 ([MDN][overflow-anchor]; [BCD][bcd-oa]). | It keeps a reader's place when old rows are pruned from the top. It does **not** stick to the bottom by itself, and a full `replaceChildren()` rebuild removes the anchor node. |
| `flex-direction: column-reverse` | The scroll origin is the "block-start inline-start corner"; "in a flex container it is the main-start cross-start corner" ([CSS Overflow 3 §2.3][overflow3]). | Inference: with `column-reverse` the main-start is the bottom, so the scroller starts at the newest row and stays there with no JS. The costs: DOM order becomes reverse-chronological, which affects `role="log"` reading order and the gap interleaving at `:750-760`, and `scrollTop` turns zero-or-negative, which breaks `onScroll` at `:699-707`. |

### 6.5 Why the current pane is not smooth (inference from code)

1. Every visible-set change rebuilds the whole log (`:738`), up to 1000 rows. During busy chat that happens on almost every 250 ms playhead event, because new messages cross `mt <= currentTime + 0.5`.
2. The follow scroll is an instant jump (`:762`), so each batch snaps into place.
3. The rebuild recreates every emote `<img>` (`:857-893`). Their height is fixed at 28 px (`components.css:583`) but their width is `auto`, so lines can re-wrap while images decode.

At dozens of messages per second, a 250 ms reveal batch holds several rows. Smooth-scrolling each batch (a user-agent-defined duration) can overlap the next batch, so the logic must re-target the scroll rather than queue animations, and it must treat programmatic scrolls as still following.

## 7. Fit / gap against the current ChatPane

| Current behaviour | With MudX Chat |
|---|---|
| Row shell, name, timestamp | Maps onto `MudXChat` + `MudXChatHeader Name` + `MudXChatBubble`. The bubble-per-turn layout (44 px min-height, 90 % width) is a different visual model from compact Twitch rows. You would need `Dense`, `ArrowPosition.None` and CSS overrides. |
| Name colour (channel colour, or Twitch colour with 4.5:1 check against the measured ground) | Stays custom. `Color` accepts only MudBlazor theme colours. |
| Badges, reply quote, `/me` italics | Stays custom (`ChildContent`). |
| Native and third-party emotes (code-point ranges, https-only catalogue, `srcset`, `referrerpolicy`, text fallback on error) | Stays custom. Porting `matchEmotes`/`buildEmoteCatalog` to C# would duplicate logic that is JS-tested today (`tests/js/chat-pane.test.js`), unless the JS versions are deleted. |
| Chat gaps between messages | Stays custom. |
| Replay time sync (`sk:playhead`, windowing, backfill, look-ahead, sync offset, retry) | Stays custom. Moving it to Blazor would put time updates on the circuit, against `frontend-foundation-contract.md:159,163`. |
| Pause on scroll-up, "Jump to current time (N)", auto-scroll | Stays custom, with no MudX support. |
| `role="log"`, `aria-live="off"`, status announcements | You provide the container. The MudX docs suggest `aria-live="polite"`, but the repo's memo deliberately keeps per-message announcements off. |
| Theming via `tokens.css` / `--mud-palette-*` | MudX CSS reads `--mud-palette-*`, which the repo's `MudTheme` already drives. |

## 8. Risks

1. **Dependency decision.** This is a new package, with a high-impact decision under `AGENTS.md` and an ADR, plus a supply chain with one maintainer (416 of 427 commits). The NuGet license requires acceptance.
2. **Version lock-step.** Every MudX release since 9.0 sets its floor to the MudBlazor version it was built with (§5.1). Staying on MudX 9.11.0 freezes you on an older line, and taking MudX fixes forces a MudBlazor upgrade PR under ADR 0012. #60 shows a binary break across a MudBlazor minor version.
3. **Performance.** You get per-message component instances, non-fixed cascades and attribute splatting (§6.3), plus a SignalR diff per reveal under Interactive Server.
4. **Contract churn.** A rewrite amends the foundation and player contracts and the design-system README, and it invalidates most of the 47 JS tests and parts of the bUnit and E2E coverage.
5. **The goal is not met.** None of the above makes scrolling smooth. That work is the same in every option.

## 9. Options

| Option | What | Dependency | Meets "smooth" | Cost / risk (inference) |
|---|---|---|---|---|
| **A. Full Blazor rewrite on MudX 9.11.0** (as requested) | A `@foreach` of `MudXChat` per message in Razor. Polling, sync and emotes move to C#, and JS keeps only scroll. | Add MudX 9.11.0 | Only with new JS scroll code | Highest. Contract conflict (§2), per-row component overhead, a frozen MudX version. |
| **B. Keep the JS island and adopt the MudX look** | The JS emits `div.mudx-chat > div.mudx-chat-bubble` markup and ships `mudx.min.css`, or the repo copies the few rules it needs into `components.css`. | MudX for its CSS only, or none | With the change in C | Low. The CSS class names become an unversioned coupling if you take them from the package. |
| **C. Fix smoothness in `ChatPane.razor.js` only** | Append new rows and prune old ones from the top instead of `replaceChildren()`. Follow with `scrollTo({behavior:'smooth'})`, or `instant` on seek and first load, honouring `prefers-reduced-motion`. Let scroll anchoring keep a reader's place during pruning. | None | Yes | Lowest. It keeps the contracts, and the existing Vitest suite covers it. |

B and C can be combined.

## 10. Open questions for you

1. Is the goal **the MudX visual style** (bubbles, header and time), **smooth scrolling**, or both? Option C delivers smoothness without MudX.
2. Do you accept a new dependency, and an ADR, that locks MudX's version to MudBlazor's? If yes, do you stay on MudX 9.11.0, or upgrade MudBlazor to 9.14.x first as a separate ADR 0012 PR?
3. Do you want to overturn the binding "JS-owned chat island" decision (`frontend-foundation-contract.md:159,163`, design-system README)? A "full Blazor rewrite" requires that.
4. Should the bubble-per-turn layout replace compact Twitch-style rows, given 1000 kept rows and high-volume chat?
5. Should smooth follow-scroll be turned off under `prefers-reduced-motion`, and should seeks and jump-to-current stay instant?
6. What chat rate should the work target? The earlier memo's load test assumed 50 msg/s (`docs/research/twitch-chat-replay.md:551`). That would set the reveal batching and scroll-retargeting design.

## 11. Verified facts vs inferences

**Verified** (package contents, source, docs, probe): package identity, license, versions, dates and dependency ranges; the component API and markup; no scroll or virtualization support in MudX; MudBlazor's removal of `MudChat` in v9; the NU1605 failure with 9.14.1 and the successful build with 9.11.0 against `[9.12.0]`; the .NET 10 limits of `Virtualize` and the .NET 11-only `AnchorMode`; the MDN and CSSWG behaviour quoted in §6.4; and every repository fact in §2.

**Inferred** (not tested): runtime correctness of MudX 9.11.0 on MudBlazor 9.12.0; the circuit and render cost of a Blazor-rendered log in this app; the `column-reverse` scroll-origin consequence; the causes of jank in §6.5; and the option ratings in §9.

**Not reached:** nothing. All primary sources listed below responded.

## 12. Sources (retrieved 2026-11-20)

- NuGet registration, `MudX.MudBlazor.Extension`: https://api.nuget.org/v3/registration5-gz-semver2/mudx.mudblazor.extension/index.json
- NuGet package 9.14.1 (nuspec, README, XML, static assets): https://api.nuget.org/v3-flatcontainer/mudx.mudblazor.extension/9.14.1/mudx.mudblazor.extension.9.14.1.nupkg
- NuGet package 9.11.0: https://api.nuget.org/v3-flatcontainer/mudx.mudblazor.extension/9.11.0/mudx.mudblazor.extension.9.11.0.nupkg
- NuGet search (disambiguation): https://azuresearch-usnc.nuget.org/query?q=mudx
- MudX source @ `06e027e`: https://github.com/MudXtra/MudX/tree/06e027edd078846b22649178a5913c987071072b/src/MudX/Components/MudXChat
- MudX provider: https://github.com/MudXtra/MudX/blob/06e027edd078846b22649178a5913c987071072b/src/MudX/Providers/MudXProvider.razor.cs
- MudX Chat docs page source: https://github.com/MudXtra/MudX/blob/06e027edd078846b22649178a5913c987071072b/src/MudX.Docs/Components/Docs/Chat/ChatDocPage.razor
- MudX docs site: https://mudx.org/docs/chat
- MudX issue #60: https://github.com/MudXtra/MudX/issues/60
- MudBlazor #12039: https://github.com/MudBlazor/MudBlazor/issues/12039 ; PR #12151: https://github.com/MudBlazor/MudBlazor/pull/12151 ; v9 migration guide #12666: https://github.com/MudBlazor/MudBlazor/issues/12666
- MudBlazor `MudComponentBase.cs` @ v9.12.0: https://github.com/MudBlazor/MudBlazor/blob/v9.12.0/src/MudBlazor/Base/MudComponentBase.cs
- [virtualize]: https://learn.microsoft.com/en-us/aspnet/core/blazor/components/virtualization?view=aspnetcore-10.0
- [perf]: https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/rendering?view=aspnetcore-10.0
- [nu1605]: https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1605
- [scroll-behavior]: https://developer.mozilla.org/en-US/docs/Web/CSS/scroll-behavior
- [scrollto]: https://developer.mozilla.org/en-US/docs/Web/API/Element/scrollTo
- [overflow-anchor]: https://developer.mozilla.org/en-US/docs/Web/CSS/overflow-anchor
- [bcd-oa]: https://github.com/mdn/browser-compat-data/blob/main/css/properties/overflow-anchor.json
- [anchoring]: https://developer.mozilla.org/en-US/docs/Web/CSS/Guides/Scroll_anchoring/Overview
- [anchoring-spec]: https://drafts.csswg.org/css-scroll-anchoring/ (Editor's Draft, 30 September 2026)
- [overflow3]: https://drafts.csswg.org/css-overflow-3/ (Editor's Draft, 13 August 2026)

[virtualize]: https://learn.microsoft.com/en-us/aspnet/core/blazor/components/virtualization?view=aspnetcore-10.0
[perf]: https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/rendering?view=aspnetcore-10.0
[nu1605]: https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1605
[scroll-behavior]: https://developer.mozilla.org/en-US/docs/Web/CSS/scroll-behavior
[scrollto]: https://developer.mozilla.org/en-US/docs/Web/API/Element/scrollTo
[overflow-anchor]: https://developer.mozilla.org/en-US/docs/Web/CSS/overflow-anchor
[bcd-oa]: https://github.com/mdn/browser-compat-data/blob/main/css/properties/overflow-anchor.json
[anchoring]: https://developer.mozilla.org/en-US/docs/Web/CSS/Guides/Scroll_anchoring/Overview
[anchoring-spec]: https://drafts.csswg.org/css-scroll-anchoring/
[overflow3]: https://drafts.csswg.org/css-overflow-3/
