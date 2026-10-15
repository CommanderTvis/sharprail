# Prototype completion audit

The goal remains active. Passing functional checks does not prove the complete
visual and docking contract. This ledger preserves the original scope and
identifies the evidence still needed before declaring completion.

The current cross-module gap backlog is [PORTING-GAPS.md](PORTING-GAPS.md). This document
keeps completion criteria and milestone evidence; module specs keep the contracts.
Current implementation and acceptance gaps are tracked in the inventory rather than duplicated here.

On `main` the authoritative upstream is [CommanderTvis/thinkrail:claude-code-integration-plugin-api](https://github.com/CommanderTvis/thinkrail/tree/claude-code-integration-plugin-api), which is [JetBrains/thinkrail:main](https://github.com/JetBrains/thinkrail/tree/main) plus the fork; the `upstream` branch tracks JetBrains alone (see `UPSTREAM.md`).
Reference authorities are `SPEC.md`, Thinkrail's
`apps/web/src/shell/layout/SPEC.md`, its actual `Workbench.tsx`/`model.ts`,
`SettingsDialog`, `MarkdownPreview`, project views, and
`prototypes/ThinkRailNative/screenshots/original-workspace.png`. When prose and
the current renderer disagree, record the distinction rather than silently
substituting an easier behavior.

| Requirement | Current evidence | Completion limit |
| --- | --- | --- |
| C# host independent of Pi/AI; direct local calls and typed WebSocket RPC remote | Host projects, `ProjectChecks.cs`, published local/remote parity, cancellation and Git parity checks | Functional prototype evidence; no production remote deployment claimed |
| Optimized open-world CoreCLR/R2R, nullable checking, single latest package | `Directory.Build.props`, `scripts/publish.sh`, published checks require R2R and exercise dynamic assembly loading/JIT | Preserve these checks after every implementation change; iOS execution/distribution remains the documented future gate |
| Functional tabs, preview/keep, close/context actions, reorder/overflow/focus and deferred navigation | `UiChecks.cs`, `NavigationChecks.cs`, `DockInputChecks.cs`; native double-click and drag captures | Final contract audit must cover the combined interaction surface, including all drop collisions and viewport changes |
| Persistent settings and XAML where it fits | Six compiled XAML files; settings persistence/theme/font/width/preset checks and native Settings capture | Menu/control typography comparison against the reference is still incomplete |
| Translated upstream E2E tests | Translated cases covering the theme catalogue, system theme pair across clients and high-contrast tabs, multi-window and remote-client sync of settings, presets, projects, workspace lifecycle and renames with reconnect rehydration, rendered Markdown diffs, line-width drafts, live refresh, Project Home/Welcome, workspace and project actions, workspace shortcuts, the new-workspace branch picker, terminal provisioning, start-failure retry, exit notices, busy-close confirmation and switch survival, Projects-only tab groups and explicit terminal creation, preview tabs, Markdown links/images/anchors/callouts/Mermaid diagrams/source, project-rail persistence, workspace document isolation, mounted outer workbench regions and side-tool selection, independent slots, deferred opens, side shortcuts, side-menu ownership and terminal placement, terminal hidden-side drag/resize/folding, pointer reorder/splits and expanded/folded side split targets, accessible keyboard focus, one-row strip geometry, keyboard/menu group commands, outer-separator hiding, frame retention across workspace switches and remote reconnects, resize persistence, narrow viewport compression, overflow geometry, confirmed Review/default preset application with reload, settings container caps, grandfathered group-limit overages, multiwindow active drag/resize isolation, local gesture cancellation feedback versus quiet idle transitions, destination hint emphasis, absence of hidden-bottom drop targets, theme switching/reload persistence, compact Changes Tree folders/counts and row dropdown/right-click/clipboard actions, Changes scopes/targets/commit fallback/failed reads and diff header/whitespace/wrapping, project picker/host-path/init flows, Scintilla editor opening/re-theming/file-width wrapping, Local GitHub status, same-id terminal remount and the fixed themed header | Full suite translation remains active; per-case inventory in `E2E.md`. Deferred document completion additionally preserves chrome/focus; inner chrome across workspace transitions and the full theme catalog remain open |
| Multiple windows and shared host state (settings, presets, labels, projects, workspace lifecycle) with local events and WebSocket RPC streaming | `MultiClientE2E.cs` and the synced cases in `ThemeE2E`, `LineWidthE2E`, `LayoutSettingsE2E` and `ProjectContextE2E` (`--sync` runs them); per-window profile entries and one-time `state.json` migration | Terminal sessions shared across clients arrive with host-owned sessions; separately launched local processes stay independent by design; native multi-window use (menu bar entry, window restore order) is unverified outside headless checks |
| Native Markdown preview with actual scrolling and selectable content | Markdig renderer checks; wheel/anchor/navigation tests; native scroll and rendering captures | Reference typography, spacing and complete representative-document comparison still need final visual review |
| Opening projects, workspace/window restoration, Git changes/diffs and worktree workflows | `ProjectChecks.cs`, `GitUiChecks.cs`, isolated Git fixtures, restoration/navigation checks; explicit-target merge-base/net-working-content and commit catalog local/remote regressions; commit picker, lightweight index-independent catalog parity, workspace selection and fresh-window persisted-query checks | Commit membership refresh/fallback and complete read-error fidelity remain open; visual fidelity of the corresponding panels/dialogs still needs final review |
| Exact frame geometry, docking grammar, panes, alignment, fold/show/restore and resize behavior | `LayoutChecks.cs`, pointer/menu/keyboard checks, source-verified side projection and collision regressions | Auxiliary stack projection with multiple folded/expanded groups and narrow viewports needs a source comparison; verify any resulting gaps |
| Screenshot-perfect layout and details | Matched sampled geometry/palette, original screenshot and native own-window captures recorded in `VALIDATION.md` | Sampled matches are insufficient: native glyph/control/menu differences remain unresolved or unverified |
| Publish and show the finished app; no Pi/AI or benchmarks | Canonical `artifacts/SharpRail.app`, published runtime checks, own-window native captures; implementation scope in `SPEC.md` | The current package is a working prototype, not yet proof that all preceding completion limits are resolved; macOS terminal execution and the Scintilla editor are separately in scope |
| Android client of a remote host | `src/SharpRail.Android` and its `SPEC.md`; Debug and Release builds run on an Android 16 arm64 tablet emulator against `SharpRail.Host.Remote` (connect and auto-connect, workbench, menus, dialogs, project by host path, file tree, Markdown, Scintilla editor, live terminal, Settings, Disconnect, Back), recorded in `VALIDATION.md` | Done with limits: emulator only. Physical devices, phones, IMEs other than Gboard, accessibility, plugin UI beyond the default workbench, HTTPS and CI are unverified; cleartext HTTP, an unencrypted stored token, one window, no on-device tests, untrimmed JIT package |

The remote transport migration uses protobuf WebSockets across desktop, browser and Android,
including the normal embedded listener. Cancellation, multiplexing, bounded queues, errors,
heartbeats and reconnect/replay are implemented and covered by focused checks. The browser's
macOS Command+A/Command+V regression is covered by real Chromium input. Existing hosts using
WebSocket RPC require an updated build/restart; the user's running host was left intact.

Browser milestone evidence:

The browser client is implemented in `src/SharpRail.Browser`, with full shared-workbench parity as its
contract and exclusively remote host adapters. Chromium acceptance covers WASM Scintilla editing,
save conflicts, live Ghostty PTYs and reconnects, PDFium text/raster/Unicode/rotation/crop, Mermaid,
modal settings, view-state persistence and external managed plugin UI/settings.
The published bundle passed a Chromium smoke check. The consolidated gate and targeted
continuations pass 120 of 122 named cases; the unchanged narrow diff-header and Spec Dialect
failed-update assertions remain. A clean full regression gate remains open. Safari, Firefox,
non-macOS browser keyboard/IME behavior, real notification permission and production HTTPS remain
platform verification gates; they do not define a reduced browser feature set.

Browser terminal batching is measured at 110.0 benchmark output FPS and 47.1 terminal redraws/s
in the recorded Chromium setup. Native batched text matches the shared renderer pixel for pixel;
120 FPS browser drawing remains unachieved. See `VALIDATION.md` for the profile and current gate failures.

The current docking, visual comparison and final-package gates are listed in
[PORTING-GAPS.md](PORTING-GAPS.md#outstanding-acceptance-work).

MacOS Scintilla/Skia integration is now a separately authorized scope extension.
Its architecture and remaining editor limits are recorded in
`src/SharpRail.Scintilla/README.md`; this does not close the broader fidelity gates.

## Additional porting gaps

- Workspace-local review comments and CI status.
- PDF page-pair diffs and sanitised HTML for byte-only content on a diff side. Bytes are fetched over WebSocket RPC
  at an immutable commit (not HTTP routes); the host never executes content and the UI draws pixels only,
  so repository script never runs.

## Plugin API

The Plugin API and all nine original builtins are registered. Functional host,
local/remote UI and packaging checks are recorded in [VALIDATION.md](VALIDATION.md);
translated scenarios are recorded in [E2E.md](E2E.md). Current plugin differences are tracked in
[PORTING-GAPS.md](PORTING-GAPS.md#fork-plugin-differences-and-acceptance-gaps),
with [acceptance tasks](PORTING-GAPS.md#outstanding-acceptance-work) consolidated separately,
with contracts in the owning module specs. Historical milestone evidence does not
close the current source-fidelity, native appearance or final-package gates.

## Tailscale pairing completion limits (2026-10-11)

The optional builtin Tailscale plugin owns tailnet discovery and pre-connection device approval. Its
injected serving bridge supplies approved-token authentication and embedded listener startup; the host
remains transport-independent below Remote. See [the plugin contract](src/SharpRail.Plugins.Tailscale/SPEC.md).

The new builtin's implementation and automated host/client/UI checks are complete;
desktop and Android Release builds pass. Full gate coverage completes across its
initial run and successful resume, with optional Git-source fixtures skipped.
Acceptance on two real Tailscale devices, physical Android UI and the packaged
application remains unverified. The owning module spec is draft pending review,
and the changes are preserved in signed commits.


## Terminal file-reference verification (2026-10-11) — source workspace before landing

The implementation and focused local/remote, editor and native Metal checks are complete on
`chat-file-links`. The full gate remains at 119/121 completed cases: the source-dependent commit-scope
fixture and the narrow diff-header assertion reproduce outside the changed file-link paths. Details
are in VALIDATION.md. Android/device and packaged-app verification were not run. Changes are
uncommitted in the feature workspace.


## Main landing gate (2026-10-11)

The gate with upstream Git fixtures completes 121/122 cases across the four-lane run and named serial continuations. ChangesDiffE2E still fails its existing narrow DiffSplit visibility assertion. Terminal chrome, bottom-panel, layout, rendered-document opening and quit-command timing failures pass on unchanged retries. The final added/deleted diff slice passes syntax, rendered diff, resource and continuous review checks; unchanged full-gate slices and workspace 13 full-gate evidence are reused. Verification includes the pending Git graph edits in the working tree. The imported work is committed on main. Earlier Git graph edits remain uncommitted; Android and packaged verification remain unrun.


## Document and tree rendering limits (2026-10-11)

The viewport/drawn-row changes and automated gates are complete in the
`perf/drawn-document-and-tree-views` workspace. The user-authorized synthetic
measurements and their tradeoffs are recorded in `VALIDATION.md`. They do not establish
native presentation latency or whole-app RAM savings. The Files scrolling follow-up
reduces allocations from 75.74 to 29.21 MB over 120 steps, still above the fully prebuilt
baseline's 3.60 MB; Git graph's process-resident sample did not decrease.
Markdown virtualization is between top-level blocks, with full content retained for
selection and Find; a single large list/table/code block and media storage remain
unbounded internally. Native screen-reader and physical Android acceptance, packaging
and a running-app review have not been performed for this change.

The browser uses restty WebGL2 by default, with Skia only as an initialization fallback.
Live input, clipboard text, resize, UTF-8 and reload/reattach checks pass. It reaches the
60 Hz headless browser cadence, but desktop performance parity remains unproven.
Shared-texture composition, full font fallback, clipboard images, full scrollback reads,
high-contrast handling and overlapping in-window UI remain incomplete.

## Workspace switching verification limits (2026-10-14)

Workspace-23's Projects tab sizing, held-routing retention and agent-preview checks pass,
with a clean Release build and formatting. The Git-enabled full gate stops in the unchanged
Tailscale host transport teardown assertion before the UI gate. Native visual animation,
Android/browser runtime and packaged-app review remain unverified; see VALIDATION.md.
