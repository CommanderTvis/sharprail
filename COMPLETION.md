# Prototype completion audit

The goal remains active. Passing functional checks does not prove the complete
visual and docking contract. This ledger preserves the original scope and
identifies the evidence still needed before declaring completion.

On `main` the authoritative upstream is [CommanderTvis/thinkrail:claude-code-integration-plugin-api](https://github.com/CommanderTvis/thinkrail/tree/claude-code-integration-plugin-api), which is [JetBrains/thinkrail:main](https://github.com/JetBrains/thinkrail/tree/main) plus the fork; the `upstream` branch tracks JetBrains alone (see `UPSTREAM.md`).
Reference authorities are `SPEC.md`, Thinkrail's
`apps/web/src/shell/layout/SPEC.md`, its actual `Workbench.tsx`/`model.ts`,
`SettingsDialog`, `MarkdownPreview`, project views, and
`prototypes/ThinkRailNative/screenshots/original-workspace.png`. When prose and
the current renderer disagree, record the distinction rather than silently
substituting an easier behavior.

| Requirement | Current evidence | Completion limit |
| --- | --- | --- |
| C# host independent of Pi/AI; direct local calls and code-first gRPC remote | Host projects, `ProjectChecks.cs`, published local/remote parity, cancellation and Git parity checks | Functional prototype evidence; no production remote deployment claimed |
| Optimized open-world CoreCLR/R2R, nullable checking, single latest package | `Directory.Build.props`, `scripts/publish.sh`, published checks require R2R and exercise dynamic assembly loading/JIT | Preserve these checks after every implementation change; iOS execution/distribution remains the documented future gate |
| Functional tabs, preview/keep, close/context actions, reorder/overflow/focus and deferred navigation | `UiChecks.cs`, `NavigationChecks.cs`, `DockInputChecks.cs`; native double-click and drag captures | Final contract audit must cover the combined interaction surface, including all drop collisions and viewport changes |
| Persistent settings and XAML where it fits | Six compiled XAML files; settings persistence/theme/font/width/preset checks and native Settings capture | Menu/control typography comparison against the reference is still incomplete |
| Translated upstream E2E tests | One hundred forty-three cases covering the theme catalogue, system theme pair across clients and high-contrast tabs, multi-window and remote-client sync of settings, presets, projects, workspace lifecycle and renames with reconnect rehydration, rendered Markdown diffs, line-width drafts, live refresh, Project Home/Welcome, workspace and project actions, workspace shortcuts, the new-workspace branch picker, terminal provisioning, start-failure retry, exit notices, busy-close confirmation and switch survival, bottom-panel alignment/height/group geometry, preview tabs, Markdown links/images/anchors/callouts/Mermaid diagrams/source, project-rail persistence, workspace document isolation, mounted outer workbench regions and side-tool selection, independent slots, deferred opens, side shortcuts, side-menu ownership and terminal placement, terminal hidden-side drag/resize/folding, pointer reorder/splits and expanded/folded side split targets, accessible keyboard focus, one-row strip geometry, keyboard/menu group commands, outer-separator hiding, frame retention across workspace switches and remote reconnects, resize persistence, narrow viewport compression, overflow geometry, confirmed Review/default preset application with reload, settings container caps, grandfathered group-limit overages, multiwindow active drag/resize isolation, local gesture cancellation feedback versus quiet idle transitions, destination hint emphasis, hidden-bottom overlap priority, theme switching/reload persistence, compact Changes Tree folders/counts and row dropdown/right-click/clipboard actions, Changes scopes/targets/commit fallback/failed reads and diff header/whitespace/wrapping, project picker/host-path/init flows, Scintilla editor opening/re-theming/file-width wrapping, Local GitHub status, same-id terminal remount and the fixed themed header | Full suite translation remains active; per-case inventory in `E2E.md`. Deferred document completion additionally preserves chrome/focus; inner chrome across workspace transitions and the full theme catalog remain open |
| Multiple windows and shared host state (settings, presets, labels, projects, workspace lifecycle) with local events and gRPC streaming | `MultiClientE2E.cs` and the synced cases in `ThemeE2E`, `LineWidthE2E`, `LayoutSettingsE2E` and `ProjectContextE2E` (`--sync` runs them); per-window profile entries and one-time `state.json` migration | Terminal sessions shared across clients arrive with host-owned sessions; separately launched local processes stay independent by design; native multi-window use (menu bar entry, window restore order) is unverified outside headless checks |
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
gRPC require an updated build/restart; the user's running host was left intact.

Next work is bounded by these remaining gates:

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

1. Auxiliary stack sizing, folded weights, spacers and viewport compression have
   source-library regression and native rendering evidence in `VALIDATION.md`.
   Preserve those checks during the remaining audit.
2. Finish the requirement-by-requirement docking and keyboard/menu audit; add
   observable regressions for concrete gaps rather than counting existing tests.
3. Compare representative native panels, dialogs, Markdown and menus at matching
   dimensions/content; resolve the recorded typography and control differences.
4. Re-run the applicable checks, publish the canonical package, verify its
   signature/runtime and inspect it running after the final changes. Only then
   evaluate the full goal for completion.

MacOS Scintilla/Skia integration is now a separately authorized scope extension.
Its architecture and remaining editor limits are recorded in
`src/SharpRail.Scintilla/README.md`; this does not close the broader fidelity gates.

## Plugin API

The Plugin API is merged: the host runtime, its seams and the wire (`src/SharpRail.Host.Core/Plugins`, the
local and remote plugin adapters), the app side (the UI kit, the registry and loader in `src/SharpRail.UI/Plugins`,
the shell's contribution points and Settings › Plugins) and the composition in `App.cs`. `--plugins` checks
both halves, and the fork's `e2e/plugins/fixture/external-plugin.spec.ts` runs with the fixture plugin installed
from disk, once through the in-process runtime and once through a remote host over gRPC. Open:

- `WorkspaceUpdated` fires on relabelling only, not on a branch switch, and plugin routes serve no static
  assets (see `Plugins.SPEC.md`, Not yet ported).
- Not ported on the UI side: Scintilla selection events (the control exposes none),
  faint-cell blanking in terminal accessories. `WatchWorkspaceAsync` now prepares watches beyond the
  workspaces a window has open; its native multi-window behaviour is unverified.
- Branch Graph now exercises `SetDiffScope` through local and gRPC windows with an independent host commit
  lookup. Blueprint and Claude Code exercise companions, launchers and terminal decorations; remaining
  fidelity gates are in their owning specs.
- The spec dialect builtin now owns the Specs panel, graph and seven MCP tools. The remaining eight
  builtin plugin ports are in progress; the recovered Claude worktrees contain partial implementations.
  Blueprint is now registered with its host, companion, author opener and editable properties/controls.
  Its focused runtime and headless checks pass, including gRPC parity and fork source-span stamping.
  The fork's start-dialog, Claude-gate, takeover and author-recovery cases are translated (see `E2E.md`); the brief
  streaming `@agent` run, the remaining blueprint-watch steps, a native fidelity review against the fork's
  `assets/screenshots/blueprint.png` and full-suite gates remain before calling it complete.
  Claude Code is now registered with its configuration pane, launcher, settings, accessory and IDE events.
  Isolated host, authenticated IDE round trip and headless checks pass, with both launcher cases and the host halves of the
  configuration mutation cases translated; `E2E.md` records the pane-driven, review and terminal-facts cases still pending.
  Its owning spec lists pending protocol cases, notification, input,
  layout, remote and complete verification gates. The fork itself answers IDE openDiff with
  `diffShown: false`; that behavior is preserved rather than expanded.
  Discord is now registered with the fork's presence decisions, Unix IPC client, settings and SVG mark.
  Its local/remote settings, gRPC and IPC framing/lifecycle checks pass; native appearance,
  published output and full-suite gates must be verified before closing its port.
  PDF Preview is registered with rendering, selection, zoom and live local/remote refresh; its owning spec records
  the remaining fidelity/verification gates. Selection across visible and offscreen pages,
  clipboard/Select All, native Unicode extraction and rotated/cropped text geometry pass focused checks.
  Branch Graph is registered with history/patch methods and a windowed side tool. Its focused local/remote
  checks pass, including compiled row/menu templates, ref-update focus/menu retention and disable/remount;
  remaining lifecycle and visual cases, published output and full gates are open.
  Visualize is registered with its MCP tool, session persistence and live diagram/comparison companion.
  Focused local/gRPC host and UI checks pass, including comparison contents/columns, drag panning, a second
  terminal and disable/re-enable; remount render cancellation, one terminal in two windows, native,
  published and full-suite gates remain open. File Icons is registered with generated assets and local/gRPC
  tree, tab, Changes, theme, resize and fallback checks. Native/published app/full-suite gates remain open.
  Codex is registered with configuration, account/models, launcher/revive, hooks, rollout facts and IDE IPC.
  Focused logic and real gRPC UI checks pass; source fidelity, multi-window editor lists,
  process-group, native/published/full gates remain open.

All nine builtin plugins pass the combined published checks in
`.bench/plugins-published-plugin-checks-2.log`. Native terminal probes and local/remote Avalonia paths
pass, and the canonical package passes deep/strict signature verification with plugin assets under
Resources. These close the recorded functional packaging and terminal checks; plugin-specific native
appearance, complete source fidelity and the latest full repository runs remain open. Codex's settings,
pane header/navigation, configuration rows, notices and terminal accessories are compiled XAML.

On 2026-10-02 the latest source passed the full repository gate with Git fixtures
(`.bench/final-full-checks.log`, exit 0) and format verification (`.bench/final-format.log`).
Still open: rebuilding and publishing the canonical package from this source, deep signature,
R2R and published-check verification of it, native GUI review of every plugin's appearance and
input, native multi-window lifecycle, and the per-plugin fidelity gates in their owning specs.

## Tailscale pairing completion limits (2026-10-11)

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
