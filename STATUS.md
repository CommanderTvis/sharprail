# SharpRail status

One ledger for where the port stands: what completion requires, which ThinkRail revisions the specs follow and what
moved since, which upstream E2E scenarios are translated, and what is still open. It replaces the separate
completion, upstream-tracking and E2E-inventory files.

- [Completion criteria](#completion-criteria): the original scope, the evidence for each requirement and its limits.
- [Upstream tracking](#upstream-tracking): the two tracked lines, synced commits, spec mapping, sync triage and the fork port log.
- [E2E porting inventory](#e2e-porting-inventory): upstream scenarios against their SharpRail translations.
- [Backlog](#backlog): current porting gaps, outstanding acceptance work and suggested batches.

## Completion criteria

The goal remains active. Passing functional checks does not prove the complete
visual and docking contract. This section preserves the original scope and
identifies the evidence still needed before declaring completion.

The current cross-module gap backlog is [Current porting gaps](#current-porting-gaps). This section
keeps completion criteria and milestone evidence; module specs keep the contracts.
Current implementation and acceptance gaps are tracked in the inventory rather than duplicated here.

The authoritative upstream lines are described under [Upstream tracking](#upstream-tracking).
Reference authorities are `SPEC.md`, Thinkrail's
`apps/web/src/shell/layout/SPEC.md`, its actual `Workbench.tsx`/`model.ts`,
`SettingsDialog`, `MarkdownPreview`, project views, and
`prototypes/ThinkRailNative/screenshots/original-workspace.png`. When prose and
the current renderer disagree, record the distinction rather than silently
substituting an easier behavior.

| Requirement | Current evidence | Completion limit |
| --- | --- | --- |
| C# host independent of Pi/AI; direct local calls and typed WebSocket RPC remote | Host projects, `ProjectChecks.cs`, published local/remote parity, cancellation and Git parity checks | Functional prototype evidence; no production remote deployment claimed |
| Optimized open-world CoreCLR/R2R, nullable checking, single latest package | `Directory.Build.props`, `scripts/mise run package:macos`, published checks require R2R and exercise dynamic assembly loading/JIT | Preserve these checks after every implementation change; iOS execution/distribution remains the documented future gate |
| Functional tabs, preview/keep, close/context actions, reorder/overflow/focus and deferred navigation | `UiChecks.cs`, `NavigationChecks.cs`, `DockInputChecks.cs`; native double-click and drag captures | Final contract audit must cover the combined interaction surface, including all drop collisions and viewport changes |
| Persistent settings and XAML where it fits | Six compiled XAML files; settings persistence/theme/font/width/preset checks and native Settings capture | Menu/control typography comparison against the reference is still incomplete |
| Translated upstream E2E tests | Translated cases covering the theme catalogue, system theme pair across clients and high-contrast tabs, multi-window and remote-client sync of settings, presets, projects, workspace lifecycle and renames with reconnect rehydration, rendered Markdown diffs, line-width drafts, live refresh, Project Home/Welcome, workspace and project actions, workspace shortcuts, the new-workspace branch picker, terminal provisioning, start-failure retry, exit notices, busy-close confirmation and switch survival, Projects-only tab groups and explicit terminal creation, preview tabs, Markdown links/images/anchors/callouts/Mermaid diagrams/source, project-rail persistence, workspace document isolation, mounted outer workbench regions and side-tool selection, side shortcuts, side-menu ownership and terminal placement, terminal hidden-side drag/resize/folding, pointer reorder, no main column splits, expanded/folded side split targets, accessible keyboard focus, one-row strip geometry, keyboard/menu group commands, outer-separator hiding, frame retention across workspace switches and remote reconnects, resize persistence, narrow viewport compression, overflow geometry, confirmed Review/default preset application with reload, settings container caps, grandfathered group-limit overages, multiwindow active drag/resize isolation, local gesture cancellation feedback versus quiet idle transitions, destination hint emphasis, absence of hidden-bottom drop targets, theme switching/reload persistence, compact Changes Tree folders/counts and row dropdown/right-click/clipboard actions, Changes scopes/targets/commit fallback/failed reads and diff header/whitespace/wrapping, project picker/host-path/init flows, Scintilla editor opening/re-theming/file-width wrapping, Local GitHub status, same-id terminal remount and the fixed themed header | Full suite translation remains active; per-case inventory in [E2E porting inventory](#e2e-porting-inventory). Deferred document completion additionally preserves chrome/focus; inner chrome across workspace transitions and the full theme catalog remain open |
| Multiple windows and shared host state (settings, presets, labels, projects, workspace lifecycle) with local events and WebSocket RPC streaming | `MultiClientE2E.cs` and the synced cases in `ThemeE2E`, `LineWidthE2E`, `LayoutSettingsE2E` and `ProjectContextE2E` (`--sync` runs them); per-window profile entries and one-time `state.json` migration | Terminal sessions shared across clients arrive with host-owned sessions; separately launched local processes stay independent by design; native multi-window use (menu bar entry, window restore order) is unverified outside headless checks |
| Native Markdown preview with actual scrolling and selectable content | Markdig renderer checks; wheel/anchor/navigation tests; native scroll and rendering captures | Reference typography, spacing and complete representative-document comparison still need final visual review |
| Opening projects, workspace/window restoration, Git changes/diffs and worktree workflows | `ProjectChecks.cs`, `GitUiChecks.cs`, isolated Git fixtures, restoration/navigation checks; explicit-target merge-base/net-working-content and commit catalog local/remote regressions; commit picker, lightweight index-independent catalog parity, workspace selection and fresh-window persisted-query checks | Commit membership refresh/fallback and complete read-error fidelity remain open; visual fidelity of the corresponding panels/dialogs still needs final review |
| Exact frame geometry, docking grammar, panes, alignment, fold/show/restore and resize behavior | `LayoutChecks.cs`, pointer/menu/keyboard checks, source-verified side projection and collision regressions | Auxiliary stack projection with multiple folded/expanded groups and narrow viewports needs a source comparison; verify any resulting gaps |
| Screenshot-perfect layout and details | [Recorded native comparison limits](src/SharpRail.UI/Rendering/SPEC.md#native-visual-acceptance-limits) | Sampled matches are insufficient: native glyph/control/menu differences remain unresolved or unverified |
| Publish and show the finished app; no Pi/AI or benchmarks | Canonical `artifacts/SharpRail.app`, published runtime checks, own-window native captures; implementation scope in `SPEC.md` | The current package is a working prototype, not yet proof that all preceding completion limits are resolved; macOS terminal execution and the Scintilla editor are separately in scope |
| Android client of a remote host | `src/SharpRail.Android` and its `SPEC.md`; Debug and Release builds run on an Android 16 arm64 tablet emulator against `SharpRail.Host.Remote` (connect and auto-connect, workbench, menus, dialogs, project by host path, file tree, Markdown, Scintilla editor, live terminal, Settings, Disconnect, Back), with scope recorded in the owning Android spec | Done with limits: emulator only. Physical devices, phones, IMEs other than Gboard, accessibility, plugin UI beyond the default workbench, HTTPS and CI are unverified; cleartext HTTP, an unencrypted stored token, one window, no on-device tests, untrimmed JIT package |

The remote transport migration uses protobuf WebSockets across desktop, browser and Android,
including the normal embedded listener. Cancellation, multiplexing, bounded queues, errors,
heartbeats and reconnect/replay are implemented and covered by focused checks. The browser's
macOS Command+A/Command+V regression is covered by real Chromium input. Existing hosts using
WebSocket RPC require an updated build/restart; the user's running host was left intact.

Browser milestone evidence:

LSP inline error rows and the host-wide LSP Settings category pass focused host/UI,
design and conformance checks, with headless visual inspection. Native/Android appearance
is unverified; the previous full-gate terminal and file-icon failures remain open.

Explicit agent file-list/project diagnostic scans now pass local/remote and MCP checks.
The latest full gate passes lanes 1 and 3 but stops in shared-terminal PhoneToDesktop
and file-icon raster checks; see the newest src/SharpRail.Host.Core/Language/SPEC.md entry for exact failures.

The 2026-10-14 LSP revisions pass focused checks for full-catalog startup, passive
Problems queries, whole-pane scrolling, status icons, context-menu usages popups and
semantic replacement. The latest full gate is incomplete at diff-opening waits in
RenderedDiffE2E and ChangeActionChecks; lanes 3 and 4 complete. See src/SharpRail.Host.Core/Language/SPEC.md.

Workspace LSP support has host/local/WebSocket and editor regression coverage, plus
real TypeScript, Pyright and clangd checks. The remaining catalog installation recipes
need live verification on their supported hosts; SDK-dependent servers require the
documented prerequisites. Shared indexes use only server-supported cache mechanisms,
with mutable analysis isolated by workspace. The full regression gate remains
incomplete at shared-terminal creation and startup timing checks (see src/SharpRail.Host.Core/Language/SPEC.md).
Android/browser compilation and native appearance of the new editor controls remain
unverified.

The LSP management pane and explicit Codex/Claude capability entries have focused
host, terminal-authenticated MCP, editor and workbench mounting coverage. The later
four-lane gate remains incomplete at rendered-diff and shared-terminal checks; its
catalog-tool fixture assumption was corrected and passes the targeted layout run.

The browser client is implemented in `src/SharpRail.Browser`, with full shared-workbench parity as its
contract and exclusively remote host adapters. Chromium acceptance covers WASM Scintilla editing,
save conflicts, live Ghostty PTYs and reconnects, PDFium text/raster/Unicode/rotation/crop, Mermaid,
modal settings, view-state persistence and external managed plugin UI/settings.
The published bundle passed a Chromium smoke check. Source and packaged results have different scopes; the [checks contract](tests/SharpRail.Checks/SPEC.md#recorded-verification-scope)
records the latest retained integration evidence without upgrading interrupted packaged runs. Safari, Firefox,
non-macOS browser keyboard/IME behavior, real notification permission and production HTTPS remain
platform verification gates; they do not define a reduced browser feature set.

Native batched text matches the shared renderer pixel for pixel. Restty is now the default;
its recorded output rate is distinct from GPU draw cadence, and 120 Hz presentation remains unverified. See the [browser measurements](src/SharpRail.Browser/SPEC.md#recorded-rendering-evidence) and [packaged-gate limits](tests/SharpRail.Checks/SPEC.md#recorded-verification-scope).

The current docking, visual comparison and final-package gates are listed in
[Outstanding acceptance work](#outstanding-acceptance-work).

MacOS Scintilla/Skia integration is now a separately authorized scope extension.
Its architecture and remaining editor limits are recorded in
`src/SharpRail.Scintilla/README.md`; this does not close the broader fidelity gates.

### Porting scope

Review, CI and rich-resource differences are tracked once in
[Current porting gaps](#current-porting-gaps); owning specs define inert-content and host boundaries.

### Plugin API

The Plugin API and all nine original builtins are registered. Functional host,
local/remote UI contracts and acceptance limits are recorded in the owning plugin specs and
[checks contract](tests/SharpRail.Checks/SPEC.md#recorded-verification-scope);
translated scenarios are recorded in [E2E porting inventory](#e2e-porting-inventory). Current plugin differences are tracked in
[fork plugin differences](#fork-plugin-differences-and-acceptance-gaps),
with [acceptance tasks](#outstanding-acceptance-work) consolidated separately,
with contracts in the owning module specs. Historical milestone evidence does not
close the current source-fidelity, native appearance or final-package gates.

### Tailscale pairing completion limits (2026-10-11)

The optional builtin Tailscale plugin owns tailnet discovery and pre-connection device approval. Its
injected serving bridge supplies approved-token authentication and embedded listener startup; the host
remains transport-independent below Remote. See [the plugin contract](src/SharpRail.Plugins.Tailscale/SPEC.md).

The new builtin's implementation and automated host/client/UI checks are complete;
desktop and Android Release builds pass. Full gate coverage completes across its
initial run and successful resume, with optional Git-source fixtures skipped.
Acceptance on two real Tailscale devices, physical Android UI and the packaged
application remains unverified. The owning module spec is draft pending review,
and the changes are preserved in signed commits.

### Verification scope

The [checks contract](tests/SharpRail.Checks/SPEC.md#recorded-verification-scope) retains the dated
source-gate and packaged-gate distinction. Earlier feature-workspace and partial-gate milestones
do not describe the current main tree or close platform acceptance.

### Document and tree rendering limits (2026-10-11)

Viewport/drawn-row behavior is implemented; its historical surface measurements are scoped evidence. The user-authorized synthetic
measurements and their tradeoffs are recorded in the [UI kit spec](src/SharpRail.Plugins.UI.Kit/SPEC.md#recorded-large-document-measurement-limits). They do not establish
native presentation latency or whole-app RAM savings. The measured Files allocation tradeoff and graph resident-memory limit do not imply whole-app savings.
Markdown virtualization is between top-level blocks and chunks of large top-level
tables, lists and code blocks, from estimated heights refined on realization, with full
content retained for selection and Find; nested large blocks and media storage remain
unbounded internally. Native screen-reader and physical Android acceptance, packaging
and a running-app review have not been performed for this change.

The browser uses Restty WebGL2 by default, with Skia only as an initialization fallback.
Its [owning spec](src/SharpRail.Browser/SPEC.md) records current capabilities and rendering limits;
headless cadence does not establish desktop performance parity.

### Workspace switching acceptance

Focused Projects retention and geometry checks do not establish native animation or physical-client
acceptance. Current cross-module gates remain in the [acceptance inventory](#outstanding-acceptance-work).

### Language intelligence acceptance

Language services and agent workspace configuration are implemented. Focused host/UI and real-server checks pass; full-gate Git-only Changes fixture failures and platform acceptance limits remain recorded in the language and checks specs.

Optional JetBrains Context has a consent-guarded, workspace-aware indexing/search
workflow and SharpRail agent tools. Fixture, local/remote, UI and lifecycle checks
pass; the final broad gate completed with a terminal-focus retry. Real authenticated
cloud search is unverified. Existing vendor login/cache is shared and disclosed;
fully private vendor state is not established and that scope decision remains pending.

## Upstream tracking

SharpRail ports two ThinkRail lines, one per SharpRail branch. Both live in the checkout at
`/Users/commandertvis/IdeaProjects/thinkrail`, which has one remote for each.

| SharpRail branch | Ports | ThinkRail ref |
| --- | --- | --- |
| `main` | CommanderTvis's fork: JetBrains plus the Plugin API, its builtin plugins and the fork's general improvements | `origin/claude-code-integration-plugin-api` (the fork's default branch) |
| `upstream` | JetBrains ThinkRail only | `upstream/main` |

Each branch keeps its own copy of this file with its own synced commit. Work on the `upstream`
branch happens in its worktree, `/Users/commandertvis/.thinkrail/worktrees/sharprail/upstream`;
do not port JetBrains-only changes onto `main` by hand, and do not port fork-only changes onto
`upstream`.

The fork's branch is a rebased chain that is force-pushed often, so its hashes do not survive:
general improvements (one commit each), then one Plugin API commit, then one commit per builtin
plugin, then one fork-only README commit. The fork's `AGENTS.md` ("The fork's commits, by title")
lists the chain. Record fork work here by commit title as well as hash, and when the recorded
hash is gone, find the new base by title with `git log --format='%h %s'`.
`main` mirrors that shape: the fork's general improvements land as one commit, the Plugin API as
one commit, and each builtin plugin as its own commit.

The `sync-upstream-specs` skill (`.claude/skills/sync-upstream-specs/SKILL.md`) pulls later
changes for the current branch and advances its synced commit.

Synced commit (`main`): specs follow the fork's `30a81cab5` ("The fork describes itself: what it adds, how to
run it, how its history moves", 2026-09-14 authored; refs read locally on 2026-10-18), which now sits on JetBrains
`3bee4f16454515674c5548cb96ed46c57c2a4956` (2026-10-16), "fix(web): stack the history search overlay above
transcript controls (#748)". The previous synced commit was JetBrains `f7cca7bfb0b1d0543eb9898c52d28df5a784eb09`
(2026-10-11). Fork code porting is recorded by fork commit title in "Fork port log" below.

Latest tracked JetBrains commit: `d3aae2d04` (2026-10-21), "Minor performance improvements (#798)" (`upstream/main`
fetched on 2026-10-21). The 19 commits after `3bee4f164` are triaged under [Current porting gaps](#current-porting-gaps);
none of them is in the fork yet (its tip `5c7a43068` still sits on `3bee4f164`), so the `main` spec sync above was not
advanced. The separate `upstream` branch is synced to `5ab283a35` (2026-10-13) and still needs its own pass for the
commits after it; do not carry them over by hand.

Latest tracked fork commit: `30a81cab56e98a541d2dfa61bf3bfbd9318bbfb8`, the tip of
`origin/claude-code-integration-plugin-api` in the checkout. The previously recorded tip `281bda97` carries the same
title: the fork chain was rebased onto `3bee4f164` (78 fork commits above it). The fork-owned specs
(`packages/plugin-*`, `plugin-adoption.md`, `packages/server/src/plugins`, `apps/web/src/plugins`) are
byte-identical between the two tips, so the rebase moved no fork behavior.

### Mapping

| Upstream | SharpRail |
| --- | --- |
| `architecture.md` | `ARCHITECTURE.md` |
| `packages/shared/SPEC.md`, `packages/server/SPEC.md`, `packages/server/src/host/SPEC.md` | `src/SharpRail.Host.Core/SPEC.md`, `src/SharpRail.Host.Abstractions/SPEC.md` |
| `packages/server/src/{git,changes}/SPEC.md` | `src/SharpRail.Host.Core/Git.SPEC.md` |
| `packages/server/src/workspaces/SPEC.md` | `src/SharpRail.Host.Core/Workspaces.SPEC.md` |
| `packages/server/src/{projects,settings,persistence}/SPEC.md` | `src/SharpRail.Host.Core/HostState.SPEC.md` |
| `packages/server/src/{fs,watch,trash}/SPEC.md` | `src/SharpRail.Host.Core/Files.SPEC.md` |
| `packages/server/src/spec/SPEC.md` | `src/SharpRail.Host.Core/Specs.SPEC.md` |
| `packages/plugin-spec-dialect/SPEC.md`, `packages/spec-graph/{core,tools}/SPEC.md` | `src/SharpRail.Plugins.SpecDialect/SPEC.md` |
| `packages/plugin-discord/SPEC.md` | `src/SharpRail.Plugins.Discord/SPEC.md` |
| `packages/plugin-pdf-preview/SPEC.md` | `src/SharpRail.UI/Resources/Pdf/SPEC.md` |
| `packages/plugin-branch-graph/SPEC.md` | `src/SharpRail.Plugins.BranchGraph/SPEC.md` |
| `packages/plugin-visualize/SPEC.md` | `src/SharpRail.Plugins.Visualize/SPEC.md` |
| `packages/plugin-file-icons/SPEC.md` | `src/SharpRail.Plugins.UI.Kit/FileIcons/SPEC.md` |
| `packages/plugin-codex/SPEC.md`, `packages/plugin-codex/host/ideBridge/SPEC.md` | `src/SharpRail.Plugins.Codex/SPEC.md` |
| `packages/server/src/{terminal,subprocess}/SPEC.md` | `src/SharpRail.Host.Core/Terminals.SPEC.md`, `src/SharpRail.UI/Terminal/SPEC.md` |
| `packages/contracts/SPEC.md` | `src/SharpRail.Host.Protocol/SPEC.md` |
| `apps/web/src/transport/SPEC.md` | `src/SharpRail.Host.Client/SPEC.md` |
| `packages/server/src/auth/SPEC.md` | `src/SharpRail.Host.Remote/SPEC.md` |
| `apps/web/SPEC.md`, `apps/desktop/SPEC.md`, `apps/web/src/resources/SPEC.md` | `src/SharpRail.UI/SPEC.md` |
| `apps/web/src/shell/SPEC.md`, `apps/web/src/shell/{layout,layoutState,layoutIntents}/SPEC.md` | `src/SharpRail.UI/Docking/SPEC.md` |
| `apps/web/src/shell/locationBar/SPEC.md` | `src/SharpRail.UI/SPEC.md` |
| `apps/web/src/store/SPEC.md`, `apps/web/src/navigation/SPEC.md` | `src/SharpRail.UI/State/SPEC.md` |
| `apps/web/src/panels/SPEC.md` | `src/SharpRail.UI/Panels.SPEC.md`, `src/SharpRail.UI/Panels/SPEC.md` |
| `apps/web/src/components/SPEC.md`, `packages/ui/SPEC.md`, `apps/web/src/lib/SPEC.md`, `apps/web/src/resources/SPEC.md`, `apps/web/src/styles/TYPOGRAPHY.md` | `src/SharpRail.UI/Rendering/SPEC.md` |
| `apps/web/src/themes/SPEC.md` | `src/SharpRail.UI/Rendering/Themes.SPEC.md` |
| `apps/web/src/styles/COLOR.md` | `src/SharpRail.UI/Rendering/COLOR.md` |
| `apps/web/src/styles/SPACING.md` | `src/SharpRail.UI/Rendering/SPACING.md` |
| `apps/web/src/styles/NOTIFICATIONS.md` | `src/SharpRail.UI/Rendering/SPEC.md` (toast and notice look; its "Not yet ported") |
| `e2e/SPEC.md` | `tests/SharpRail.Checks/SPEC.md` |
| `scripts/SPEC.md` | `scripts/SPEC.md` |
| `packages/plugin-api/SPEC.md`, `plugin-adoption.md` (fork) | `src/SharpRail.Plugins.Api/SPEC.md` |
| `packages/server/src/plugins/SPEC.md` (fork) | `src/SharpRail.Host.Core/Plugins.SPEC.md` |
| `apps/web/src/plugins/SPEC.md`, `apps/web/src/plugins/{loader,registry}/SPEC.md` (fork) | `src/SharpRail.UI/Plugins/SPEC.md` |
| `packages/plugin-ui/SPEC.md` (fork) | `src/SharpRail.Plugins.UI.Kit/SPEC.md` |

### Not tracked

Out of scope for SharpRail (the pi packages, CLI launcher, website,
analytics, GitHub/PR review, desktop updates, web-only tooling): every other upstream
`SPEC.md`, plus `goal-and-requirements.md`, whose applicable scope already lives in
`SPEC.md`. A new upstream spec is triaged into one of the two lists on the next sync.

### Latest sync triage

Compared the previously synced `f7cca7bfb` with the fork tip `30a81cab5` on 2026-10-18 (113 commits: 35 JetBrains,
through #748, and 78 fork). The refs are those present in the read-only checkout; nothing was fetched. Spec triage
covered the 62 files changed in the JetBrains range (specs and style documents). Applicable deltas are in the mapped destinations:

- UI: Ctrl+, opening Settings on Windows and Linux (macOS Cmd+, was already ported), under UI "Not yet ported".
- Panels (Settings): the redesign (#710) with grouped rail, shared row primitives, auto-save inputs, Appearance
  mode and contrast controls, Layout applied/default marks, Add new layout, Advanced limits and one confirmation;
  the Changes tree's folder counts shown only while collapsed (#735); tree rows at the shared 22px role.
- Docking: resize handles that keep the preceding panel's scrollbar usable (#736 and the UI kit handle change).
- Rendering: the neutral notice and toast look with a warning severity (#730), and the large-diff guard,
  virtualization and bounded stacked sections (#731).
- Host: the login-shell environment import is opt-in for launchers (#714), with its post-mortem.

Already ported or inherent to SharpRail earlier, so only recorded here: the calm captioned location bar (#712),
newest-created-first rail with a recency switcher (#725, #713), 28px panel headers, 22px rows and the named layout
roles (#703), OS-level window focus for notification suppression (#705).
Not tracked (AI, pi or web-only): MCP management, tool cards and status (#691-#695, mcp specs), the TODO list
rail and plan page (#706), project trust (#690), extension on/off (#709), background-work attention and
read-when-seen unread (#716, #728), restored chats and model visibility (#685, #745), pi 1.2.0 and the npm scope
(#723, #734), the loader redesign (#727; SharpRail has no working badge until A17), Pierre and WebKit selection
fixes, the typography line-height scale and radius tokens (web-only CSS), `architecture.md` and
`packages/contracts` (AI/MCP only), `e2e/SPEC.md` and `e2e/workflows` (AI/MCP scenarios plus Playwright details),
and the new `agent/mcp`, `chat/tools/mcp` and `mcp` specs. `ARCHITECTURE.md` needed no port.
The fork-only specs and plugin documents did not change.

### Previous sync triage (2026-10-11)

Compared the previously tracked revision with the requested commit
`f7cca7bfb0b1d0543eb9898c52d28df5a784eb09`: six commits, through the continuous
Changes review feature (#668). The requested object was fetched directly from
JetBrains/thinkrail into this checkout; the authoritative upstream checkout was
not changed. Triage completed on 2026-10-11.

Every changed spec and the changed styles were triaged. Applicable deltas are
adapted in their mapped destinations, with unimplemented behavior explicitly
recorded under Not yet ported:

- UI, shell/layout, layoutState, layoutIntents and lib: persistent edge tool rails,
  compatible tool/terminal panes, remount-free visibility and topology changes,
  workspace-fenced gestures/focus, atomic attention updates, no-op notification
  suppression, Home routing and shared modal/text-entry keyboard ownership.
- Panels, store and resources: continuous Changes review tabs, sidebar navigation,
  single-click reveal versus double-click individual diff, lazy bounded sections,
  stacked/one-file presentation, viewed and content-keyed hunk Keep progress,
  scoped session state, historical-scope immutability and renderer sizing.
- Themes: opaque primary-fill semantics and the Minimal Light/Dark pair.
- Shared host: login-shell environment import with inherited-value precedence and
  excluded probe variables; it remains an implementation gap.
- Spec graph: exclude assistant scratch directories from indexing.
- E2E: pending rail/layout/Home translations, continuous Changes journeys and
  fixture/disclosure-anchor invariants; no new translated coverage is claimed.

The mapping now includes layoutIntents under Docking and resource-renderer
contracts under Rendering as well as UI. The new
`packages/server/src/turns/SPEC.md` is not tracked: agent-run snapshots and change
receipts are AI functionality. Changes to contracts, transport, server/git/host,
persistence, settings, workspaces, agent, pi-delegation, chat, reviews and
planReview specify excluded AI turn tracking, subagent concurrency or review
guides. Web scripts' React compiler census and styles' CSS token allowance are
web-only; chat colour changes remain excluded. No specs were deleted.

This revision records completed specification triage, not implementation parity.
Remaining native work is recorded in the owning specs.

`apps/web/src/notifications/SPEC.md`, left untracked by an earlier sync, is adapted on `main` in
`src/SharpRail.UI/SPEC.md` ("Away notifications") for the Claude Code and Codex terminals, whose
plugins are the only attention source. Its pi session-state observer, browser permission preface
and session switcher have no counterpart.

### Fork port log

Historical fork port decisions, by title, with what happened at each recorded pass.
Current gaps are consolidated in [Current porting gaps](#current-porting-gaps); skipped chat/pi
commits below document exclusions, not pending work. Hashes are from
the fork chain as fetched on the date given and will not match after a force-push.

2026-10-18 additions after that pass (original SharpRail work, recorded for completeness):
- Air Governance: original builtin plugin adapted from the fork's `jbcentral` and JetBrainsAiCard on the fork's `central-access-api` branch (a separate branch from `claude-code-integration-plugin-api`), with per-launch `central run` wrapping of Claude Code and Codex in place of the fork's pi extension. It rests on the agent launch seam (Plugin API H18/W22). Access sources are listed automatically while Central is chosen and signed in (no List button), and with Central governing a launcher the Codex and Claude Code Account tabs show the Central account and credits instead of the vendor account (launch contribution `Account`, Plugin API H18; not run against a real Central endpoint). Checks: `--air-governance`; gate cases `air-governance` and `air-governance-ui`.
- omp: original builtin plugin with early (basic) support, marked by the new manifest `support` field and its Settings › Plugins badge: launcher and start action with `--model`, `--approval-mode`, `--advisor` and `--no-lsp` (SharpRail code intelligence by default), SharpRail's MCP server through a SharpRail-owned omp profile, a session folder per launch for revival, context files with nested @-imports and Add offers, version and missing-binary state, and zombie-safe agent detection; not run against a real omp, and not governed by Air (Central has no omp agent). Checks: `--omp`; gate cases `omp-host` and `omp-ui`.
- Air Context: the semantic code search moved out of the host into a builtin plugin, switched only through Settings › Plugins and disabled by default, using the plugin state directory and tool guidance handles (Plugin API H19/H20). Checks: `--context`.

Out of scope for every pass: pi, CLI, website/analytics, desktop update flow, Electron-
and browser-only mechanics with no Avalonia counterpart.

#### General improvements, one commit (2026-10-01)

Range: the synced commit above to the fork's "Plugin API: the contract, the host loader, the web
registry, and the UI kit" (`4737df6d3`), exclusive; fork chain fetched 2026-09-30, tip `0304a543e`.
Commits are in chain order; the first ten are JetBrains commits the fork carries.

- `2f99a3939` Plan page: live Session block, cumulative Summary, and in-plan interaction (#555): skipped (pi/chat)
- `15c66331e` feat(delegation): add resource ownership and captured history (#540): skipped (pi)
- `be8a5e433` feat(dag): add validated contracts and durable storage (#541): skipped (pi)
- `f8f0b725c` feat(dag): add durable execution and portable pi adapter (#542): skipped (pi)
- `15a92a264` fix(e2e): preserve hidden consoles for Windows descendants (#559): skipped (Playwright harness on Windows)
- `5383b8506` feat(updates): add consent-driven update flows (#558): skipped (desktop updates)
- `fdbbc1d2f` docs(specs): make the goal doc a living product document (#573): skipped (goal doc is not tracked)
- `f90ea4c0b` fix(web): default markdown diffs to the rendered view (#569): ported
- `32ce2cad9` Text insertion issue and padding adjustment (#560): skipped (the non-chat part is the create-workspace dialog's prompt image chips and caret restoration; SharpRail's create-workspace flow has no prompt or image attachments, the AI prompt being excluded)
- `d49d91bef` feat(website): add consented journey analytics foundation (#524): skipped (website/analytics)
- `fba9d8853` Reveal a file in the file manager, from our own menu: ported (Files row menu "Reveal in Finder" via host `ApplyFileActionAsync("reveal")`: `open -R` / `explorer /select,` / containing folder; the pi-runtime diagnostics hunk skipped as pi)
- `4fdb372e1` Open a plain folder as a project, with no git required: partial (plain folders open directly and the git-init prompt and host "init" action are removed; Changes/Review withholding for a plain or unborn repo, the `tool-needs-git` notice and the gitless Start-work dialog copy are not ported)
- `008c972b0` web: our own tab tooltips, and they stop blocking what they cover: skipped (SharpRail's tab tooltips are already its own Avalonia tooltips anchored below the tab; the pointer-transparent popup has no verifiable Avalonia counterpart, and the rest of this commit is a web-only merge of unrelated fork work)
- `1f6aa7dfb` The desktop window stops handing its own host to the browser: skipped (Electron navigation)
- `1829e0736` Send an editor selection into a pi chat: skipped (pi chat)
- `10183e913` Where a tab can go, drawn instead of listed: skipped (a new drawn placement picker in the tab menu is a large docking-menu redesign; not attempted in this pass)
- `20a3450be` The light theme's diff canvas stops reading as disabled: ported
- `f4408d2a3` Previewing a file before you keep it is a setting: ported
- `0d59b4878` The desktop window comes back to the port it had, and with it your tabs: skipped (desktop host port; SharpRail embeds its host)
- `ec1ec6823` tests: a fixture open survives a neighbour's pick and a loaded machine: skipped (Playwright fixture timing and picker-pointer state; SharpRail checks use isolated fixtures)
- `32f07e106` Frontmatter edits like Obsidian properties, above the rendered view: ported (editable preview properties, chips, folding, key operations, shape conversion and suggestions share the source draft; unsupported YAML and diffs remain read-only)
- `b047c8f29` The spec tools reach any agent in a ThinkRail terminal, over MCP: partial (MCP protocol, per-terminal token, THINKRAIL_MCP_URL stamping and a loopback HTTP/1.1 route on both the remote host and the local terminal relay are ported; only spec_get and spec_grep are served, because SharpRail has no spec authoring or full graph yet, so spec_create/update/delete/graph/validate and grep's tag/dependsOn filters are missing; the carried mouse-mode guard, process tree and title changes have no SharpRail counterpart)
- `25133aa27` Panes a resource carries: embedded, never a tab of their own: partial (Markdown Split view; visualization/blueprint companions are AI surfaces and skipped)
- `76c3ec2f3` A terminal whose pty inherited a stale utmpx record no longer runs as the wrong user: ported
- `b9548ea25` The outline moves to the pane's edge and drives both preview and source: ported
- `0d61c37c8` A terminal frozen by a drain event the OS never delivered thaws on its own: partial (the drain latch is skipped: gRPC/HTTP-2 flow control has no drain event to lose; of the carried server changes only the unborn-HEAD workspace refusal was ported; published-branch rename, branchDetails/deleteBranch/fetch, suggestWorkspaceName and vcs gap belong to features SharpRail lacks; GIT_OPTIONAL_LOCKS was already present)
- `f8730d329` On the desktop, the host is this computer, and the copy stops calling it "the host": ported for the strings SharpRail has (Enter path… and the path dialog's explanation locally; GitHub/JetBrains AI card strings do not exist here)
- `308b0eef4` A file in the tree can be dragged to where it is wanted: skipped (the diff is xterm drag-and-drop of a tree path into the web terminal; SharpRail's Files rows are not drag sources and its terminal is native Ghostty, so this needs native-bridge drop work)
- `c4eebc5f1` feat(web): put the target choice above the Start work header: ported (the model/effort labels are AI and omitted; an edited name becomes the workspace's host label)
- `87325ac26` An issue number in a comment stops being painted as a colour: already present (Scintilla has no colour decorators)
- `1e4449a2a` A repository URL becomes a project, cloned into a folder you choose: ported 2026-10-03 (`CloneProjectAsync` through every host layer, the Clone repository… dialog and both `clone-project.spec.ts` scenarios; the remaining diff deletes web-only unit tests)
- `7a7685057` The desktop's right-click menu stops offering Look Up, Fonts, and Services: skipped (Electron context menu)
- `3f01f8942` A file in the tree can be deleted, into the trash, after asking: ported (Delete file/folder asks, then moves to the OS trash via NSFileManager on macOS or gio on Linux; also the row menu itself and Copy absolute path; git-ignored dimming is now ported with host rule checks and headless styling/opening coverage; composer drag remains unported)
- `1d30e44be` A diff too narrow for two columns opens inline until you say otherwise: ported
- `82419d3ad` Cmd+F finds text in every preview, not only inside the editor: partial (find bar over Markdown previews and rendered diffs with count and stepping; only the current match is selected, other matches are not painted; no find over terminals)
- `be216ba71` Typechecking the desktop stops waiting on a dev host that is running: skipped (Bun tooling)
- `b71bb30a9` A diagram inline in a document stops at a height you can see past: ported
- `99d36b658` Search the whole workspace from one popup: partial (Markdown hits open the rendered document without the fork's block flash)
- `99ed15307` The rendered markdown diff gets the outline and the properties block: ported (properties diffed per key rather than through the text merge)
- `2a2739307` A workbench frame belongs to the project it was arranged in: skipped (needs a per-project frame stash in the persisted DockState, touching presets, validation and document pruning; deferred as too risky for this merge)
- `bf7881065` A selection you can see in the dark theme: ported
- `4496970ed` The projects rail's plus says what it does: already present (the Add project button already has its tooltip)
- `155735756` A spec's own frontmatter is a properties table, not a raw block: ported (multi-line flow sequences and one-level mappings read as properties; editable preview properties now share the source draft)
- `783b0b04e` An inline diagram zooms like the fullscreen one, and pans inside its box: ported
- `d55683607` chore: bump TypeScript to 7.0.2 and migrate off the legacy compiler API: skipped (TS tooling)
- `e597593d7` A spec is titled by its frontmatter, and its [[links]] go somewhere: ported
- `f6371aa62` Folding a rail stops rebuilding the whole workbench: already present (a regression check was added)
- `fb6c0147d` The branch in the topbar opens the project's branches: partial (SharpRail lists every worktree as a workspace, so the fork's "foreign worktree" branch deletion is refused instead)
- `cf91102e9` A project with no specs opens its rail on Files: ported
- `356366850` A terminal nobody is looking at stops rendering: skipped (xterm/CSS content-visibility; Ghostty renders natively)
- `f9afe4049` The bottom row's controls clear the window's rounded corner: skipped (SharpRail's bottom group header sits at the top of the bottom region, never against the window's rounded corner)
- `c4c236097` A code font you choose, with its ligatures: skipped (needs a new host setting through all five host layers plus font plumbing into Scintilla's bundled SKTypeface, Ghostty's config shim and Markdown code blocks; recorded under Not yet ported in Rendering/SPEC.md)
- `ebedcf702` Settings answer to Command+, on macOS: ported
- `0d40a484b` The topbar says what "from main" means: ported (SharpRail's topbar has no "from main" line, so the tooltip is on the workspace placeholder's "· from main", the only place it appears)
- `4171b16c2` A large markdown preview stops re-doing its own work: already present (document controls are cached across tab switches, so a revisited preview is not rebuilt; there is no tokenizer to cache)
- `2d8fce7b1` A selection reaches the document, not the chrome around it: already present (only content surfaces use SelectableTextBlock; chrome is plain TextBlock)
- `9e5e727a2` Tests, fixtures and spec text the fork's changes left behind: skipped (nothing in scope: web unit tests, Playwright fixtures and the New project spec, a feature SharpRail lacks)
- `7f94b1832` The CLI waits a beat for a tab that is already open before opening another: skipped (CLI)
- `8e05d76c0` A default model and thinking level you can choose in Settings: skipped (AI)
- `5bdc70e39` Vertical tabs can live under their workspace in Projects: ported later, as `8feda8584` (see "Fork refresh, 2026-10-02")
- `ba90884c6` A dirty worktree can be force-removed before its branch: partial (the host has `force-remove-worktree`, and removing a dirty workspace from the rail asks a second time before forcing; the branch list still refuses to delete a branch a workspace is on instead of removing that worktree first)
- `ba3d301c4` Branch pickers show every remote, collapsibly, and a click can switch or start a workspace: partial (collapsible, profile-remembered remote groups in the New Workspace picker are ported; the topbar branch list and its click-to-switch/start actions do not exist in SharpRail)
- `a86e95aaf` The Welcome provider warning recognizes connected JetBrains AI: skipped (AI)
- `2c44d749d` JetBrains AI access source switching, for accounts with more than one org: skipped (AI)
- `a204860fb` Hidden models you can filter out in Settings and the model picker: skipped (AI)
- `3123ee248` A tooltip when a model name truncates in the model selector: skipped (AI chat)
- `063b43f55` Questionnaires superseded by later assistant activity clear the waiting status: skipped (AI chat)
- `557f0f3d6` Attached images in chat draft persist across tab switches: skipped (AI chat)
- `26e511629` An open file says when its file is deleted on disk: ported (tab "deleted" mark and a Scintilla banner; a remote read maps a missing file to gRPC NotFound so the coded miss survives the wire; the banner is only on Scintilla editors, so Markdown/image tabs get the tab mark alone)
- `8f6b555d8` A file or folder can be created, or renamed, from the Files tree: ported (New file…/New folder…/Rename… with one name dialog: inline collision, stem selection, host error shown in place; no per-file-type icon since the tree has none)
- `be1bf412e` Remove the CLAUDE.md alias of AGENTS.md: skipped (ThinkRail repo housekeeping)
- `df19980af` Terminals start clean after a host restart: already present (SharpRail never persisted terminal output; the not-yet-ported revival note now says revived shells start blank)
- `524db465d` OpenAI models carry their provider mark: skipped (AI chat)
- `4d2e6b2d2` Chat tabs carry the Pi mark: skipped (AI chat)
- `915b6d978` A live terminal reattach restores full-screen input modes: ported (host replay appends the live alternate screen and mouse/alternate-scroll modes)
- `4c774f45e` Chats survive a provider switch when their saved model is unavailable: skipped (AI chat)
- `626031274` A project's absolute path can be copied from its context menu: ported (plus the workspace menu's Copy path -> Copy absolute path and new Copy name)
- `d4d03db1b` A worktree made in ThinkRail's folder shows up as a workspace: already present (SharpRail's workspace rows come straight from `git worktree list`, so any worktree made from a terminal already shows; there is no workspace registry to adopt into or dismissal list to keep)

#### Plugin API, one commit (2026-10-01)

- `4737df6d3` Plugin API: the contract, the host loader, the web registry, and the UI kit: ported as the three
  `SharpRail.Plugins.Api*` assemblies, the host runtime (`Host.Core/Plugins`), the app runtime (`UI/Plugins`)
  and `SharpRail.Plugins.UI.Kit`, with the wire as one generic gRPC call and stream. Not ported: pi (H11, H16,
  the agent tool surface and the `pi` block, which is parsed and ignored), chat (chat companion hosts,
  `openChat`, tool renderers, the `writtenPathGroup` slot), workspace auto-naming hints, the `styles` manifest
  field, and the kit's settings-row and terminal-fact components, which arrive with their first plugin. The
  file-icons plugin's assets and the Specs panel's move are later commits.

#### Builtin plugin ports in progress (2026-10-01)

- `e8a751bd7` Spec Dialect: recovered and extended to all seven MCP tools, the Specs tree, spec links,
  rail defaults and legacy layout migration. Specs discovery and MCP ownership now belong to the plugin.
  Focused checks pass; the signed commit awaits full-suite verification and the signing agent.
- `b2a6c75a6` Blueprint: registered host/UI halves with the contract, streaming format, reconciliation,
  exact prompts, persistent author, keyed state, tool, properties, controls, companion, start dialog and
  file-to-author redirect. Focused runtime, gRPC and headless checks pass. Remaining fidelity/acceptance
  gates are in [Completion criteria](#completion-criteria); the signed commit and full-suite gate remain pending. Pi and bundled AI
  chat remain excluded.
- `6401ccae8` Claude Code: recovered host/configuration/IDE bridge/hook assets and UI controls;
  added activation entry point and builtin registration. Host status/configuration/review/revive,
  transcript folding, headless pane/launcher/settings lifecycle and 40 hook checks pass. Static
  settings use compiled XAML. Other fidelity and full verification gates remain in the plugin spec;
  no signed plugin commit yet. IDE `openDiff` retains the fork's documented file-opening fallback.
- `eb382d75e` Discord: recovered and registered host/UI halves, preserving the fork's presence decisions,
  IPC protocol, settings, disabled default and Discord mark. Local/remote settings and gRPC checks pass;
  native/published/full-suite gates and the signed plugin commit remain pending.
- `ab23be7cf` PDF Preview: recovered and registered the UI-only builtin, with PDFium rendering,
  text selection, shared zoom math and local/remote live reload. Focused engine/UI checks pass.
  The remaining fidelity/verification gates are still open.
  Selection across visible pages, clipboard/Select All and rotated/cropped text geometry now pass checks.
  Captured drags scroll across offscreen pages; native Unicode extraction and compiled toolbar/retry pass checks.
- `e91aef250` Branch Graph: recovered and registered contract, host and UI halves. Parser/lane fixtures,
  local/gRPC history and patches, windowed/paged UI, copy actions and Changes scoping pass focused checks.
  Independent host commit lookup closes the comparison-menu dependency; unchanged graph rows retain their
  controls during refresh. Compiled row/menu templates, glyphs, changed-ref focus/menu retention and
  disable/remount are verified. Remaining lifecycle/visual scenarios and complete gates are open.
- `b8d8ec066` Visualize: registered contract, host and UI halves with the terminal MCP drawing tool,
  persisted session adoption, whole-workspace state channel and renderer reports. Shared comparison and
  Mermaid controls use compiled XAML and the PDF zoom gesture vocabulary. Focused local/gRPC host and
  companion checks pass, including rollback, revision reuse and restart. Remaining multi-terminal/window,
  content/lifetime, MCP HTTP ownership, native/published/full gates are in its owning spec.

- `40678fd30` File Icons: registered the UI-only builtin, exact filename/extension mappings and pinned
  Material Icon Theme 5.38.1 generation. All 1,251 recoloured SVGs match the fork generator byte for byte.
  Local/gRPC tree, tab, Changes, theme, resizing and disabled/missing/broken asset fallbacks pass focused checks.
  Native appearance, canonical published app and full-suite gates remain open.

- `177abd30c` Codex: recovered and registered contract/host/UI draft with TOML provenance, hooks,
  launch/revive, rollout facts, app-server account/models and IDE v0 IPC. Logic checks pass; 297
  documented keys/types match fork. Focused real gRPC UI account/config/context/IDE/launcher/model/status
  checks pass; complete fidelity, native/published/full gates remain open.

### Pending fork refresh, 2026-10-01

The local fork ref now points to `1c7c90a5a`. Compared with the previous `0304a543e` tree,
plugin sources remove workspace-name suggestions from the host API and Claude/Codex hooks,
and add the UI kit's `Switch` control and tests. SharpRail already omits the naming operation.
The obsolete Codex report fields and corresponding test expectations are now removed. The shared
switch's controlled state, geometry, input, automation and disabled theme checks pass in the
isolated build; combined verification and transition/native appearance review remain. Historical
port references above remain until the source audit is complete.

### Fork refresh, 2026-10-02

Tracked fork commit at this refresh: `867bffea508a25e48b7b8500f95ba8991bbc587f` ("The fork describes itself:
what it adds, how to run it, how its history moves"), the tip of `claude-code-integration-plugin-api`
fetched 2026-10-02. The chain is now rebased onto JetBrains `be804a563`, the commit `upstream` and
therefore `main` already follow. Compared with `1c7c90a5a`, by title:

- `d356661a5` feat(state): replace session activity with normalized session lifecycle (#525): skipped (pi/chat)
- `be804a563` feat(desktop): frameless Windows title bar with HTML window controls (#536): inherited from `upstream`
- `ee852215c` perf(web): enable the native React Compiler: skipped (web-only)
- `71e884170` Restoring the latest Pi chat in every workspace is a setting: skipped (pi/chat)
- `b9ec7d8ba` Recents stops listing project folders that no longer exist: ported (a read-time projection in
  `HostStateStore`; missing or file-replaced paths are hidden, other errors keep them, and the stored list
  is untouched)
- "Folding a rail stops rebuilding the whole workbench" left the chain; its SharpRail regression check remains.
- `4a8aa9191` Claude Code integration is a builtin plugin, amended: the appended system prompt is the fork's
  compact text with SharpRail's tools; render-time host reads were already reactive through `WatchHost`.
- `667cf631f` OpenAI Codex is a builtin plugin, amended: wire version 3, tab-scoped configuration and
  AGENTS.md discovery from the tab's CWD, the "Tell Codex it is running in SharpRail" setting with its
  developer-instructions file, launch-flag source and hook tagging, and revival that reapplies the
  setting once. See `src/SharpRail.Plugins.Codex/SPEC.md`.
- `8feda8584` Vertical tabs can live under their workspace in Projects: ported (vertical centre column with
  px width, tab panes as workspace-view metadata with the fork's model rules, pane drops and menu verbs,
  no centre split in the mode, ambiguous-name folder lines, the strips nested in Projects with other
  workspaces' read-only tab lists). Its other parts were already covered or are web-only: keep-alive
  terminals (SharpRail retains terminal controls), `useElementSize`, `content-visibility`, and the window
  corner gutter. The "show beside a Claude terminal" open rule and `unofferedTools` are not ported.

### Fork refresh, 2026-10-03

`claude-code-integration-plugin-api` is still at `867bffea5` (checked with `git ls-remote` on 2026-10-03).
Two of its features were missing here and are now ported:

- The Settings dialog nests plugin sections under Plugins (its `SettingsDialog.tsx` fieldset): plugin pages
  sit indented behind a rule directly below Settings › Plugins instead of after the core sections.
- `1e4449a2a` A repository URL becomes a project: see its entry in the port log above.

### Fork refresh, 2026-10-18

The fork's `claude-code-integration-plugin-api` (local ref `30a81cab5`, title unchanged) was rebased onto
JetBrains `3bee4f164`. Compared by title with the chain recorded at `281bda97` (83 titles, base `68bb83747`), the
current chain has 78. Absent now, because they are no longer separate fork commits:

- "Chats survive a provider switch when their saved model is unavailable" (JetBrains took it as #685/#740) and
  "Settings answer to Command+, on macOS" (JetBrains #747, which adds Ctrl+, on Windows and Linux): the macOS
  half stays ported; the Windows and Linux chord is recorded in UI "Not yet ported".
- "A file in the tree can be deleted, into the trash, after asking", "A project with no specs opens its rail on
  Files" and "An issue number in a comment stops being painted as a colour": not in the current chain and not
  found in `upstream/main` by title. SharpRail keeps its existing ports; whether the fork dropped or folded them
  was not established from the local refs.

No fork-only commit was added. Everything else is as logged above.

## E2E porting inventory

Authoritative source: the `e2e` folder of the ThinkRail line the branch follows ([Upstream tracking](#upstream-tracking)): on `main`
[CommanderTvis/thinkrail:claude-code-integration-plugin-api](https://github.com/CommanderTvis/thinkrail/tree/claude-code-integration-plugin-api),
on the `upstream` branch [JetBrains/thinkrail:main](https://github.com/JetBrains/thinkrail/tree/main/e2e).

Tests drive real Avalonia pointer/keyboard input and host calls in a headless window.
Each case gets an isolated repository fixture and profile. Native macOS checks remain
necessary for window moving, zooming, platform dialogs and final visual verification.
Multiwindow gesture cases open the same project with independent window profiles
and explicitly focus the shortcut recipient before sending keyboard input; shared-state
cases open the second window from the same app so both windows share one host.
Browser URL/page-count assertions become internal workspace navigation assertions.
ARIA roles and relationships become Avalonia automation-peer assertions. Delayed
WebSocket replies become held host-call responses; local mode uses direct C# calls.
AI chat is no longer restricted (see `SPEC.md`); the passes recorded here skipped it, so the initial upstream chat tab is omitted from
document counts, chat-only cases are excluded, and mixed cases keep only their
non-chat assertions.
Workspaces start empty. Terminal cases explicitly create the terminal tabs they exercise.
Headless terminal tabs run real host PTY sessions (`/bin/sh`) and show their output as
plain text; commands print split markers so an echoed command line never satisfies an
output assertion. Delayed or failed `terminal.attach` replies become held or failing
host starts. Ghostty itself cannot run headless.
Project picker cases inject a fake folder picker (the platform dialog is native); the
non-git case initialises through the host `init` action, which commits the folder's existing
files as upstream does. Rail expansion is checked after activating the first project's default
workspace, because only the active project lists its workspaces. Same-id terminal
bodies are provoked by opening one terminal tab id in two workspaces through the
layout API, because generated terminal ids never repeat. The
Local GitHub case empties `PATH` so the real `gh` probe degrades.
Workspace suites commit the sample project and click its project row to reach Project
Home, where upstream's `openFixtureProject` lands. A reload closes the window and opens
a new one on the same profile; the profile's last location stands in for the URL route.
Native shell execution, Metal presentation, Mod+Shift+J from a focused Ghostty view and
remote Ghostty tabs relayed to a gRPC host PTY have separate checks in
`scripts/mise run check:terminal` and `SharpRail.Checks --native-terminal`.
The upstream one-pixel PNG has an invalid IDAT checksum and truncated compressed
data. Its translated fixture uses a valid one-pixel PNG because Skia rejects the
original; the image-loading assertion remains intact.

The rendered Markdown diff cases replace upstream's web worker and long-task observer
with a merge seam on the window: it records the thread each merge runs on, can hold or
fail a merge, and the test times every dispatcher turn while the merge lands. Git-backed
rendered-diff and live-refresh fixtures commit the sample README and SPEC.md on the base
branch before creating the worktree, matching upstream's sample project.

Run with a local clone of `JetBrains/thinkrail:main`:

```sh
git clone --branch main https://github.com/JetBrains/thinkrail.git .bench/thinkrail
SHARPRAIL_TEST_GIT_SOURCE="$PWD/.bench/thinkrail" .tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release
```

### Case inventory

Ported cases retain upstream titles. `Ported` does not mean the latest run passed.
Existing host/layout checks provide additional coverage but are not counted as translations.

| Suite | Upstream case | Status |
| --- | --- | --- |
| `preview-tabs.spec.ts` | a single click previews into one reusable slot, a double click keeps the tab | Ported |
| `preview-tabs.spec.ts` | a double click claims the slot on its way to keeping the tab, at any latency | Ported |
| `preview-tabs.spec.ts` | a double click on an unopened file sends exactly one fs.readFile | Ported |
| `preview-tabs.spec.ts` | a browse the user has navigated away from is dropped, not activated on arrival | Ported |
| `preview-tabs.spec.ts` | of two browse clicks in flight at once, the later one wins | Ported |
| `preview-tabs.spec.ts` | a keep that lands first does not invalidate a browse requested after it | Ported |
| `preview-tabs.spec.ts` | a newer tab click cancels an older preview-tab settle timer | Ported |
| `preview-tabs.spec.ts` | the Specs panel shares the one slot, and closing the preview tab releases it | Ported |
| `preview-tabs.spec.ts` | with previewing off, every click keeps a tab of its own | Ported (fork) |
| `markdown-links.spec.ts` | a parent-relative file link cannot escape into browser navigation | Ported |
| `markdown-links.spec.ts` | relative links, images, and heading anchors work in the rendered markdown view | Ported |
| `markdown-alerts.spec.ts` | renders GitHub-style alert callouts in the rendered markdown view | Ported |
| `markdown-mermaid.spec.ts` | renders mermaid fences as diagrams in the rendered markdown view | Ported (with the fork's capped inline diagram: the box stays under 480 px, zoom enlarges the drawing, a drag pans it) |
| `layout.spec.ts` | workbench strips and feature toolbars keep one-row geometry with ARIA tabs | Ported |
| `layout.spec.ts` | overflow uses directional fades without changing tab-strip geometry | Ported for side tool strips; document tabs live in Projects |
| `layout.spec.ts` | auxiliary panel scrollbars stay quiet at rest and expose only clipped edges | Pending: relies on browser WebKit scrollbar pseudo-elements, hover-intent attributes and forced-colors media; Avalonia has no quiet-scroll cue surface to assert |
| `layout.spec.ts` | ARIA tabs use roving keyboard focus, recover after close, and expose keyboard separators | Ported |
| `layout.spec.ts` | outer side widths publish on pointer-up and restore after reload | Ported |
| `layout.spec.ts` | one local frame survives workspace switches while resource tabs stay workspace-specific | Ported |
| `layout.spec.ts` | a duplicated tab remints copied surface storage and preserves both layouts on reload | Pending: browser sessionStorage surface ids and duplicated tabs have no desktop-window analogue |
| `layout.spec.ts` | dragging outer separators hides both sides and preserves their restore state | Ported |
| `layout.spec.ts` | the side group menu shows tools for its own side and opens terminals in that group | Ported |
| `layout.spec.ts` | a terminal can move to its own side group; resize, fold, and visibility gate its one body | Ported |
| `layout.spec.ts` | a project with no specs opens its rail on Files, not on the empty Specs panel | Ported (fork; the fixture's specs are removed rather than opening a plain folder) |
| `layout.spec.ts` | side groups expose broad per-panel above and below split targets | Ported |
| `layout.spec.ts` | Mod+B and Mod+J hide and restore local sides without affecting bottom | Ported |
| `layout.spec.ts` | keyboard and menu commands reorder and search tabs in the one main column group | Diverges: the main column never splits |
| `layout.spec.ts` | pointer drag reorders one tab and the main column offers no split targets | Diverges: the main column offers tab groups only |
| `layout.spec.ts` | applying the Review preset preserves resources and installs its auxiliary topology | Diverges: Review keeps one center group |
| `layout.spec.ts` | the local default preset drives an explicit frame reset | Ported |
| `layout.spec.ts` | custom presets synchronize while defaults and group limits remain window-local | Ported (the peer is a second window of the app; save, rename, set default, limits and delete) |
| `layout.spec.ts` | Layout settings controls keep their container-preset max-widths | Ported; the case left upstream's suite by 2026-10-18 (the Settings redesign), so the translation is now additional coverage |
| `layout.spec.ts` | an accepted side-group overage is grandfathered without allowing further growth | Ported |
| `layout.spec.ts` | a narrow viewport compresses locally without rewriting recursive topology | Ported (one center group) |
| `layout.spec.ts` | frontend windows keep chat and file placement independent | Ported for file placement and terminal selection across two windows of the app (a terminal opened in one arrives in the other unselected, since terminal membership is the host's catalog); the chat-tab and closed-chat history assertions are excluded (AI chat is a non-goal) |
| `layout.spec.ts` | layout survives a transport reconnect and remains writable | Ported |
| `layout.spec.ts` | another window cannot cancel or rearrange an active tab drag | Ported |
| `layout.spec.ts` | another window cannot cancel or adopt an active side resize | Ported |
| `layout.spec.ts` | local layout transitions with no gesture in progress never announce a canceled drag | Ported |
| `layout.spec.ts` | a local transition during a side resize cancels the gesture and says so | Ported |
| `layout.spec.ts` | a tab drag reveals every valid destination subtly, then emphasizes the one under the pointer | Diverges: tab strips are not framed; their tabs are the only strip targets |
| `layout.spec.ts` | the hidden bottom drop zone wins overlapping terminal targets and reveals its frame group | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `projects.spec.ts` | opens a git repo as a project via the directory picker | Ported |
| `projects.spec.ts` | opens a project from an explicit host path | Ported |
| `projects.spec.ts` | picker failure falls back to host-path entry on every host platform | Ported |
| `projects.spec.ts` | manual path from the rail supersedes a picker started from Welcome | Ported |
| `projects.spec.ts` | opening a non-git folder offers to initialise a repo, then opens it end-to-end | Superseded by the fork's `gitless.spec.ts`: a plain folder opens directly, with no git required (the check asserts no dialog, no `.git`, and Create workspace disabled) |
| `projects.spec.ts` (fork) | project context menu copies its absolute path without changing the active workspace | Ported |
| `projects.spec.ts` | rail expansion is per-browser view state that survives a reload | Ported |
| `projects.spec.ts` | activating a workspace in one project keeps the other project's rail expansion | Ported |
| `projects.spec.ts` | project context actions stay compact and close/reopen is lossless across clients | Ported (the observer is a second window of the app, opened after the cancel/Escape focus steps; touch long-press has no desktop input, and the menu has no Open existing worktree entry) |
| `new-workspace-shortcut.spec.ts` | Mod+N opens the Create workspace dialog for the selected project from the Welcome screen, and Escape closes it | Ported |
| `new-workspace-shortcut.spec.ts` | The Mod+Alt+N alias opens the same dialog and Mod+Shift+N does not | Ported |
| `new-workspace-shortcut.spec.ts` | Mod+N works inside an active workspace | Ported |
| `workspace-actions.spec.ts` | Open in launches the detected editor detached at the worktree path | Ported (a stub `code` on `PATH` records its argument) |
| `workspace-actions.spec.ts` | Copy path copies the worktree's absolute path to the clipboard | Ported |
| `workspace-actions.spec.ts` | a managed workspace can rename its display label inline without changing Git | Ported |
| `workspace-actions.spec.ts` | an open inline rename survives reconnect | Ported (a real gRPC host behind a relay that drops and restores the client's connection) |
| `workspace-actions.spec.ts` | the Default workspace's kebab menu offers only non-mutating actions | Ported |
| `workspace-actions.spec.ts` | right-click opens the workspace's kebab menu without activating it | Ported |
| `workspace-actions.spec.ts` | the kebab is hover-only ONLY on devices that actually have hover — never invisible by default | Ported (Avalonia has no hover media query; asserts opacity 0 at rest and 1 on row hover) |
| `welcome.spec.ts` | opens a clean ThinkRail with no projects imported | Ported (the title is SharpRail; the side tool frame stays, empty) |
| `welcome.spec.ts` | the Welcome provider warning only shows when no provider is connected, and opens Settings | Excluded: AI providers |
| `welcome.spec.ts` | Settings → Providers lists in-app auth options | Excluded: AI providers |
| `welcome.spec.ts` | a real provider's API key round-trips through the login dialog (add in Settings, sign out) | Excluded: AI providers |
| `welcome.spec.ts` | clicking Sign in (Settings) opens the in-app login dialog, and Cancel dismisses it | Excluded: AI providers |
| `welcome.spec.ts` | Settings → Providers offers JetBrains AI with host-authoritative Central guidance | Excluded: AI providers |
| `welcome.spec.ts` | a project with specs offers Start building over Set up, beside the project-folder fork | Ported (non-AI assertions: Project home context and the single Work in project folder fork; Start building opens an AI chat, so Create workspace is the call to action) |
| `welcome.spec.ts` | a project without specs suggests setting it up | Excluded: Set up project pre-fills the setting-up-a-project AI skill prompt; the dialog's project-folder path is covered by the new-workspace folder-mode case |
| `welcome.spec.ts` | opening a non-git folder from the Welcome screen offers to initialise a repo | Superseded by the fork's `gitless.spec.ts`: the folder lands on its Project Home without git init (the Set up project call to action is AI and omitted) |
| `welcome.spec.ts` | clicking a project returns to its Welcome, deselecting the active workspace | Ported |
| `default-workspace.spec.ts` | the Welcome fork's “Work in project folder” enters the Default workspace — the project folder itself | Ported (the terminal `pwd` step is omitted: headless checks do not execute shells) |
| `default-workspace.spec.ts` | a terminal branch switch converges every Default branch label live | Ported (the fixture switches branch with Git directly instead of through an embedded terminal) |
| `default-workspace.spec.ts` | the Default workspace is non-removable and unique; project home stays reachable | Ported |
| `workspaces.spec.ts` | opens and safely forgets an existing user-owned worktree | Pending: SharpRail lists every Git worktree from `git worktree list`; there is no Open existing worktree dialog or adopt/forget registry for user-owned checkouts |
| `workspaces.spec.ts` | an attached worktree cannot also be opened as a project | Pending: opening a linked worktree resolves to its owning project rather than reporting a conflict; needs the external-worktree registry above |
| `workspaces.spec.ts` | creates, removes, and re-creates worktree workspaces (no branch collision) | Ported |
| `new-workspace.spec.ts` | the dialog lists local branches (no stray origin) and creates a worktree | Ported (the model/effort selectors and initial chat tab are AI and omitted) |
| `new-workspace.spec.ts` | an edited name names the worktree, and the placeholder leaves naming to the host | Ported (fork; the edited name becomes the workspace's host label, and the AI naming hint is omitted) |
| `branch-list.spec.ts` | the branch chip lists branches with their worktrees, and guards deletion | Ported (fork; the checked-out branch is refused too, and the host's refusal is checked directly) |
| `branch-list.spec.ts` | a branch held by a worktree ThinkRail did not make is still the user's to delete | Excluded: SharpRail lists every worktree of a project as a workspace, so a worktree-held branch is always refused |
| `branch-list.spec.ts` | Fetch brings the remotes up to date from the branch list | Ported (fork) |
| `search.spec.ts` | Mod+Shift+F searches the worktree and a hit opens its file at that line | Ported (fork; also checks that Git-ignored files are skipped; the line is scrolled to on macOS, where Scintilla opens text files) |
| `search.spec.ts` | a hit in a markdown file flashes the block it landed in | Pending: the rendered Markdown view has no source-line landing |
| `search.spec.ts` | a query with no matches says so, and Escape closes the popup | Ported (fork) |
| `fold-perf.spec.ts` | folding a side group does not remount the centre | Ported (fork; already held, since folding re-projects cached document and terminal controls) |
| `new-workspace.spec.ts` | folder-mode Start with an empty prompt lands in a fresh chat in the Default workspace | Ported (Start lands in the Default workspace; no chat is created) |
| `new-workspace.spec.ts` | a project's committed skills are gated behind trust, then autocomplete | Excluded: AI skills |
| `new-workspace.spec.ts` | the start prompt shares template completion and slot behavior without live-only commands | Excluded: AI start prompt |
| `new-workspace.spec.ts` | Enter in the prompt creates; Shift+Enter inserts a newline | Ported (the start prompt is AI, so Enter in the dialog creates; there is no multiline field for Shift+Enter) |
| `new-workspace.spec.ts` | a base whose fetch fails reports git's error, not a request timeout | Ported |
| `new-workspace.spec.ts` | the branch picker groups by host-supplied remotes and creates from the selected ref | Ported |
| `new-workspace.spec.ts` | opening New Workspace prefetches a stale default before create | Ported |
| `new-workspace.spec.ts` | opening New Workspace prefetches a missing default tracking ref | Ported |
| `branch-list.spec.ts` | a remote group can be collapsed and expanded, and stays that way in every branch picker | Ported (the New Workspace picker; remembered per remote in the profile) |
| `branch-list.spec.ts` | left-clicking a branch checked out by a ThinkRail workspace switches to it | Excluded: SharpRail has no topbar branch list |
| `branch-list.spec.ts` | the branch list groups branches from every configured remote, and a remote-only branch opens New Workspace prefilled | Excluded: SharpRail has no topbar branch list |
| `branch-list.spec.ts` | the topbar branch list marks its Local branches, mirroring the Changes picker | Excluded: SharpRail has no topbar branch list |
| `branch-list.spec.ts` | a dirty external worktree requires a second force-delete confirmation | Excluded: SharpRail has no branch list with branch deletion |
| `new-workspace.spec.ts` | a pasted image in the workspace dialog rides along into the first chat turn | Excluded: AI chat |
| `workspace-lifecycle.spec.ts` | workspace removal propagates — no zombie row in a second tab | Ported (second window of the app; the removed workspace's notice stands in for the toast) |
| `workspace-lifecycle.spec.ts` | workspace rename propagates live and rehydrates a tab that missed a later snapshot | Ported (two gRPC clients; the peer's connection is dropped and restored by a relay) |
| `workspace-lifecycle.spec.ts` | removing the active workspace restores the previously selected workspace | Ported |
| `workspace-lifecycle.spec.ts` | workspace creation propagates to a second tab's rail | Ported (second window of the app) |
| `reload-navigation.spec.ts` | reloading from the older of two chats returns to that exact chat without rail clicks | Excluded: AI chat |
| `reload-navigation.spec.ts` | a directly opened exact-chat fragment restores that chat; two tabs keep independent routes | Excluded: AI chat |
| `reload-navigation.spec.ts` | missing chat, workspace, and project fall back to the nearest valid location | Ported (the chat step is omitted; the profile's last location stands in for the URL) |
| `reload-navigation.spec.ts` | a transient workspace read failure preserves the URL and restores after reconnect | Ported (the relay refuses the restoring client's connection, then allows it; the remembered workspace stands in for the URL and the chat tab is omitted) |
| `reload-navigation.spec.ts` | a failed exact-chat transcript waits for reconnect instead of duplicating its read | Excluded: AI chat |
| `reload-navigation.spec.ts` | user navigation while the restore read is delayed wins over the late response | Ported (the held read is the startup project open) |
| `reload-navigation.spec.ts` | reload from a file tab restores its shared placement under the workspace route | Ported |
| `reload-navigation.spec.ts` | workspace rows still list after a reload restore (the light list is complete) | Ported |
| `files.spec.ts` | shows files and compacts single-directory runs in the Files tree | Ported |
| `files.spec.ts` (fork) | a file row has our own context menu, not the webview's | Ported |
| `files.spec.ts` (fork) | file and compacted folder menus copy their host absolute paths | Ported for a file row |
| `files.spec.ts` (fork) | deleting a previewed file leaves the workbench interactive | Ported (macOS only: it moves a real file to the Trash) |
| `files.spec.ts` (fork) | a folder row creates a file inside it, whose icon follows the name as it is typed | Ported without the icon step: the tree has no per-file-type icons |
| `files.spec.ts` (fork) | a new file warns as soon as its name already exists | Ported (within the folder-row case) |
| `files.spec.ts` (fork) | a file row creates a folder beside it, and renames itself | Ported (the stem selection is asserted; the rename text is set rather than typed) |
| `files.spec.ts` (fork) | an open tab says when its file is deleted on disk, and recovers when it returns | Ported (the banner is asserted on macOS, where text files open in Scintilla) |
| `files.spec.ts` (fork) | an entry git ignores is dimmed, whichever rule ignores it | Ported: host checks cover ignore rules locally and over gRPC; headless input checks dimming, italics, tooltip and opening an ignored file |
| `files.spec.ts` (fork) | a file row drags into the composer as a mention and into a terminal as a path | Not ported: AI composer excluded; tree rows are not drag sources |
| `clone-project.spec.ts` (fork) | clone repository | Not ported yet |
| `gitless.spec.ts` (fork) | Changes and Review withheld for a plain folder or an unborn repository | Not ported yet |
| `editor.spec.ts` | opens a file in a center Monaco tab, focuses on re-open, and closes | Ported (Markdown source is SharpRail's read-only source view; the ready placeholder omits chats) |
| `editor.spec.ts` | hides YAML frontmatter in the rendered view but shows it in source | Ported (the prose is checked apart from the editable properties block) |
| `editor.spec.ts` | opens a non-markdown file straight to Monaco with no rendered-view toggle | Ported (macOS Scintilla) |
| `workspace-tabs.spec.ts` | editor tabs are scoped to the active workspace | Ported |
| `workspace-tabs.spec.ts` | the selected side tool follows workspace switches | Ported |
| `workspace-tabs.spec.ts` | switching workspaces re-targets the mounted workbench instead of remounting it | Ported |
| `workspace-tabs.spec.ts` | a same-id terminal body remounts instead of carrying across workspaces | Ported |
| `terminals.spec.ts` | a workspace opens a terminal automatically, rooted in the worktree, with working I/O | Ported with an explicit launch; automatic startup is excluded |
| `terminals.spec.ts` | a shell start failure explains recovery and retries the same tab | Ported (failure text, preserved focus, disabled then focused Retry, same-tab recovery; SharpRail has no terminal Settings page to link; take-back is covered by the second-client row) |
| `terminals.spec.ts` | xterm uses the shared quiet rail and directional curtains | Excluded: xterm.js scrollbar and CSS curtain styling; Ghostty draws its own native scrollback |
| `terminals.spec.ts` | terminals are workspace-scoped and survive workspace switches | Ported |
| `terminals.spec.ts` | multiple terminals per workspace keep independent buffers and can be closed | Ported |
| `terminals.spec.ts` | the terminal's shell counts characters, not bytes | Ported |
| `terminals.spec.ts` | a shell survives a trip to Project Home and back | Ported (the test leaves through the project's default workspace, which also unmounts the worktree's terminal; the Project Home route is not exercised yet) |
| `terminals.spec.ts` | historical terminal queries do not become input on remount | Excluded: SharpRail keeps the live terminal surface across remounts instead of replaying output into a new emulator, so no replayed query can answer itself |
| `terminals.spec.ts` | rapid re-entry never spawns a second shell | Ported |
| `terminals.spec.ts` | a shell survives a page reload | Ported (reload is a new window of the same app over the same profile and terminal host; earlier output is replayed once) |
| `terminals.spec.ts` | a terminal's output never reaches another client | Ported (two windows, each with its own connection to a real gRPC host) |
| `terminals.spec.ts` | a tab says so when its shell exits | Ported |
| `terminals.spec.ts` | Ctrl+C still interrupts while an input method is active | Excluded: Chromium keyCode 229 composition events; native Ctrl+C through the AppKit responder chain is covered by `--native-terminal` |
| `terminals.spec.ts` | a shell that dies while detached is not re-attached as if alive | Ported (the reopened tab shows the final output and `[process exited with code 137]` without starting a shell; unlike upstream, the exited tab stays and a new terminal gives a live shell) |
| `terminals.spec.ts` | a shell survives losing the connection and reconnecting | Ported (a TCP proxy severs the gRPC connection; the session resumes from its last output position without duplicates) |
| `terminals.spec.ts` | a terminal attach response lost with its socket is replayed exactly once | Ported (a client interceptor drops the first attach reply; the retry attaches the same session and the host starts one shell) |
| `terminals.spec.ts` | final shell output is delivered before exit after reconnect | Ported (the proxy holds the reconnect while the shell exits; final output arrives once, then exit code 7) |
| `terminals.spec.ts` | a second client takes a terminal over and the first is told | Ported (second window, real gRPC host; the second watches the held terminal and takes it over, the first keeps watching it live under the take-back offer and takes the terminal back) |
| `terminals.spec.ts` | closing a tab with a running process asks first | Ported |
| `terminals.spec.ts` | a rejected forced close stays correlated and permits a clean retry | Excluded: closing disposes the tab's own session; SharpRail has no host close request that can be refused |
| `terminals.spec.ts` | closing an idle tab does not ask | Ported |
| `terminals.spec.ts` | a terminal opened in one browser never creates placement in another | Not ported: SharpRail shares every catalogued terminal, so the peer's terminal arrives passively (`SharedTerminalsE2E.cs`) |
| `terminals.spec.ts` | a shell that dies during a reclaim is not presented as alive | Ported (the take-back attach reply is delayed while the shell is killed; the tab ends exited) |
| `terminals.spec.ts` | a terminal that was hidden while it printed keeps its width | Excluded: xterm DOM rendering; Ghostty renders natively and keeps its grid while hidden |
| `terminals.spec.ts` | terminal forwards pointer capture and Ctrl+T to a raw PTY | Ported as a host check (a live reattach replays the alternate screen and mouse modes); pointer forwarding is Ghostty's |
| `terminals.spec.ts` | terminal sends alternate-scroll wheel gestures to the PTY | Ported as a host check (alternate scroll, mode 1007, is replayed with the mouse modes) |
| `bottom-panel.spec.ts` | full-height panel-header actions stay square | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | a new workspace starts with one accessible terminal group in a 30% bottom panel | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | a hidden local frame keeps the host terminal reserved without attaching until shown | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | a completed initial-terminal handshake never recreates a terminal after explicit close | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | Mod+Shift+J works from xterm, preserves its PTY through hide and reload, and is modal-aware | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | bottom height, all alignments, and keyboard resizing persist across reload | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | bottom alignments give excluded lower corners to the actual side panels | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | bottom alignments follow locally compressed side geometry at narrow widths | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | narrow side resizing persists only the side whose separator moved | Ported without bottom alignment; pointer and keyboard resizing retain the untouched side |
| `bottom-panel.spec.ts` | closing a final bottom resource retains its frame groups until explicit removal | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | bottom alignments follow side geometry while a resize gesture is in progress | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | bottom groups arrange left-to-right, resize, fold to 27px, restore, and enforce their own limit | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | a narrow viewport locally compresses bottom groups without rewriting their topology | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | bottom visibility and alignment stay local to each window and survive its reload | Excluded: bottom zone retired; explicit terminals use workspace tab groups |
| `bottom-panel.spec.ts` | an old host layout stays inert while a pristine surface starts Balanced | Excluded: SharpRail never stored layouts on the host, so there is no legacy host layout to ignore |
| `theme.spec.ts` | appearance switches a discovered theme and persists it across reload | Ported (Settings lists every bundled manifest) |
| `theme.spec.ts` | system mode follows each client and retains its explicit pair | Ported (the application theme variant stands in for the emulated media query; also checks the pair after reload). The peer is a second gRPC client with its own settings copy; both share one process and so one device appearance, so the peer's light-device theme is checked by resolving its synced mode and pair |
| `theme.spec.ts` | Monaco opens files and re-themes under every discovered manifest | Ported (macOS Scintilla; every bundled manifest, with the high-contrast selected-text overrides) |
| `theme.spec.ts` | selected workspace tabs keep their surface and edge marker in high contrast | Ported (center, right and terminal strips; brush, 2px geometry and rendered pixels) |
| `line-width-settings.spec.ts` | line-width controls validate drafts, converge on broadcasts, and persist | Ported: draft Escape/validation/Enter/Save, a held host broadcast keeping the toggle at the host's value until released, a second window converging, and persistence across a fresh window, with the Markdown width standing in for the excluded chat measure. SharpRail's "Limit lines to this width" toggle falls back to the pane width rather than exceeding it |
| `line-width-settings.spec.ts` | the file width wraps source and updates an already-mounted editor | Ported (macOS Scintilla) |
| `line-width-settings.spec.ts` | the default file width wraps both sides of a long-line diff | Ported |
| `line-width-settings.spec.ts` | chat uses the selected measure and optionally exceeds a narrow pane | Excluded: AI chat is a non-goal |
| `settings.spec.ts` | settings shows the Local GitHub status block and degrades gh gracefully | Ported |
| `settings.spec.ts` | macOS opens settings with its own Preferences chord, and other platforms do not | Ported (fork) |
| `changes.spec.ts` | Changes tab shows the active worktree's diff and swaps per workspace | Ported |
| `changes.spec.ts` | Rendered markdown diff of a large repetitive file never blocks the main thread | Ported (the merge runs off the dispatcher thread; no dispatcher turn may exceed 250 ms instead of the browser's 1 s long task) |
| `changes.spec.ts` | Rendered markdown diff shows an error placeholder when the merge worker fails | Ported (an injected failing merge replaces the aborted worker script) |
| `changes.spec.ts` | Rendered markdown diff follows live edits on disk (stale merge cancelled, fresh one lands) | Ported (a merge held for an intermediate edit must be cancelled by the next one) |
| `changes.spec.ts` | Changes has a List&#124;Tree toggle; Tree groups files into folders with +/- counts | Ported |
| `changes.spec.ts` | Changes scope selector filters by commit / uncommitted; each scope is its own diff tab | Ported |
| `changes.spec.ts` | Uncommitted scope converges when HEAD moves out-of-band (a commit in a terminal) | Ported |
| `changes.spec.ts` | The scope menu's target-branch picker re-points what the changes are measured against | Ported |
| `changes.spec.ts` | A target that advanced past the fork point adds no phantom changes (merge-base semantics) | Ported |
| `changes.spec.ts` | A change row's action menu opens from the ⌄ button and from right-click; Copy path writes the relative path | Ported |
| `changes.spec.ts` | The diff viewer collapses unchanged context and has a per-tab hide-whitespace + copy header | Ported |
| `changes.spec.ts` | Change rows stay one aligned, fully-highlighted row — menu slot included, long names truncated | Ported |
| `changes.spec.ts` | The diff header keeps its controls on a narrow pane, however long the file's path | Ported |
| `changes.spec.ts` | A commit scope keeps the header readable: short sha on the pill, subject in its tooltip | Ported |
| `changes.spec.ts` | The scope menu is per workspace: its commit rows never carry over to another worktree | Ported |
| `changes.spec.ts` | Re-pointing the target branch re-reads an open branch-scope diff tab — active or backgrounded | Ported |
| `changes.spec.ts` | A commit scope whose commit is rewritten away falls back to All changes with a toast | Ported |
| `changes.spec.ts` | A failed read says so — it never renders as an empty (clean) change set | Ported |
| `changes.spec.ts` | Closing a diff tab disposes Monaco cleanly — no 'TextModel got disposed' assertion | Excluded: Monaco model lifetime; SharpRail diff tabs do not use an editor |
| `changes.spec.ts` (fork) | The diff header keeps its controls on a narrow pane: an unpinned diff opens inline there, a click pins Split | Ported with the native width rule: layout pins apply while wide enough; narrow panes hide Split and use inline |
| `changes.spec.ts` (fork) | The rendered markdown diff carries the outline and the properties block | Ported (properties are diffed per key rather than through the text merge; the marks are the same ins/del runs) |
| `changes.spec.ts` (fork) | A markdown diff drops to one column on a narrow pane like every other file | Covered by the width rule shared with source diffs; no separate case |
| `frontmatter.spec.ts` (fork) | frontmatter renders as editable properties, and an edit lands in the draft | Ported (shared source draft, dirty save point, undo/redo, Escape cancellation and Save) |
| `frontmatter.spec.ts` (fork) | list chips add and remove, and the block folds away | Ported |
| `frontmatter.spec.ts` (fork) | the type menu converts between text, sequence, and mapping | Ported (including per-entry mapping edits) |
| `frontmatter.spec.ts` (fork) | a type property offers the spec vocabulary as suggestions | Ported |
| `frontmatter.spec.ts` (fork) | a block the editor cannot speak renders read-only instead of guessing | Ported |
| `spec-documents.spec.ts` (fork) | a spec is titled by its frontmatter and its [[links]] reach the spec they name | Ported |
| `spec-documents.spec.ts` (fork) | ordinary markdown keeps its own first heading and leaves [[text]] alone | Ported |
| `find.spec.ts` (fork) | Mod+F over a preview opens the find bar and highlights the matches | Partial: the current match is selected and counted; other matches are not painted |
| `find.spec.ts` (fork) | Mod+F inside the editor is left to Monaco's own find widget | Excluded: Markdown source is a read-only text view, not an editor with its own find |
| `outlineTree.test.ts` (fork) | the outline reads headings from the source, skips fences and jumps preview and source | Ported as an interaction check (`MarkdownDocumentE2E`) |
| `mcp-tools.spec.ts` (fork) | a companion pane embeds beside its host; Markdown gets a Split view | Partial: the Markdown Split view only; visualization and blueprint companions are AI surfaces |
| `live-refresh.spec.ts` | worktree changes on disk appear live in Specs, Files, Changes, and an open file tab | Ported |
| `live-refresh.spec.ts` | churn canary: a write storm coalesces to a few frames and the host stays responsive | Ported (fsChanged frames become coalesced watcher refreshes; `/health` becomes a timed host listing) |
| `topbar-chrome.spec.ts` | ordinary browsers have a fixed themed header with zero native insets | Ported (fixed 40px themed header, workbench below it, Settings inside at narrow width; native inset elements are unrepresentable) |
| `topbar-chrome.spec.ts` | live safe areas on either edge preserve header and workbench geometry | Pending: safe-area insets cannot be injected; the title bar has a fixed OS-dependent margin |
| `topbar-chrome.spec.ts` | the action cluster keeps Update, quota Retry and Settings out of the drag region | Pending: SharpRail has no Update or quota Retry actions |
| `plugins/fixture/external-plugin.spec.ts` (fork) | the roster lists a discovered external plugin as disabled | Ported (`PluginUiChecks`: the fixture is installed from disk into the host's state directory and run twice, through the app's in-process runtime and through a remote host over gRPC; the row shows external, `v1.0.0` and disabled) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | enabling mounts its settings section and its side tool, and its method answers over the wire | Ported (the switch in Settings › Plugins; the UI half is read from the plugin directory through `IPluginService.ReadFileAsync`; the section's settings update reaches the host half, and a start action's call and the board's state channel round-trip) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | disabling unmounts the side tool and the method reports disabled | Ported (the tool tab keeps its slot with "Fixture board is off"; every contribution leaves; the call reports `Disabled`) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | a manifest with a mismatched API generation is refused, naming both generations | Ported (a copy installed with `apiGeneration` 999 is refused by the host and its row names 999 and 1) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | a plugin directory removed from disk disappears after a rescan | Ported (a second installed copy is deleted from disk; Rescan in Settings › Plugins drops its row) |
| `plugins/discord/discord.spec.ts` (fork) | Discord is disabled by default and uses ThinkRail's application once enabled | Ported (`DiscordChecks`, local and remote hosts, with an isolated fake Discord IPC server) |
| `plugins/discord/discord.spec.ts` (fork) | invalid application id is rejected; a valid one survives reload | Ported (reopen settings and read persisted host state, local and remote) |
| `plugins/discord/discord.spec.ts` (fork) | a blocked project stays blocked after reload | Ported (checkbox and status line, local and remote) |
| `plugins/pdf-preview/pdf-preview.spec.ts` (fork) | PDF renders, zoom rasterizes larger, text is selectable and Markdown chrome is absent | Ported through local and gRPC hosts (`PdfPreviewChecks`, PDFium replaces browser-only pdf.js) |
| `plugins/pdf-preview/pdf-preview.spec.ts` (fork) | rewrite, rename and Reload show new bytes without reopening | Ported through local and gRPC hosts with live host file revisions |
| `plugins/branch-graph/branch-graph.spec.ts` (fork) | graph windowing and paging, common gutter, gutter only as wide as the rows on screen, merge/orphan rendering, copy hash/patch and Changes scoped to a clicked commit | Ported (`BranchGraphChecks`): windowing, gutter, copy and scope through local and gRPC hosts; lane-gutter and merge/orphan cases on fresh local fixtures |
| `plugins/visualize/visualize.spec.ts` (fork) | terminal drawing, title, diagram navigation, comparison rewrite, close/reopen and renderer refusal | Ported through local and gRPC hosts (`VisualizeE2E`), with actual remote PTY MCP HTTP ownership and background-window updates; companion share and UI session adoption remain open |
| `plugins/visualize/visualize.spec.ts` (fork) | agent chat diagram and comparison cards | Excluded: Pi/chat is outside SharpRail's scope; shared card content and comparison geometry still need focused verification |
| `plugins/file-icons/file-icons.spec.ts` (fork) | core typed tree/tab glyphs, Changes list/tree and disabled fallback | Ported through local and WebSocket hosts (`FileIconsChecks`) |
| `plugins/codex/codex-account.spec.ts`, `codex-config-file.spec.ts`, `codex-context.spec.ts`, `codex-launcher.spec.ts` (fork) | account/limits, external configuration edits, instruction offers and launch options | Selected flows ported through a real gRPC host (`CodexE2E`); complete visual/lifecycle coverage remains open |
| `plugins/codex/codex-model-picker.spec.ts`, `codex-status.spec.ts` (fork) | session-only picker, hook badges, separate IDE command submission, rollout tokens and plan | Ported with host-owned PTYs and real terminal HTTP routes (`CodexE2E`); native/remount/title variants remain open |
| `plugins/codex/codex-ide.spec.ts` (fork) | editor context, rebasing, closed editors and provider disablement | Selected flows ported through gRPC and real IPC; selection is injected through the editor-event API. Native selection, multi-window and reload remain open |
| `plugins/codex/codex-subscription-notice.spec.ts` (fork) | Pi subscription invitation | Excluded: Pi/chat is outside SharpRail's scope |
| `plugins/spec-dialect/hot-toggle.spec.ts` (fork) | disabling spec-dialect turns its tab into the dormant placeholder, re-enabling brings it back | Ported (`SpecDialectChecks`; spec links are disabled and restored too) |
| `plugins/spec-dialect/specs-panel.spec.ts` (fork) | Specs tab renders the worktree's spec tree and opens a spec as an editor tab | Ported (`SpecDialectChecks`: nesting, role tags, collapse, editor tabs and specs added mid-session); pixel offsets and the fork's created-workspace fixture are not reproduced |
| `plugins/spec-dialect/specs-panel.spec.ts` (fork) | Specs offers Retry only after its automatic graph read fails | Ported (failure injected at the app's plugin service; a fresh profile's rail default also reads the graph, so reads fail until Retry) |
| `plugins/spec-dialect/specs-panel.spec.ts` (fork) | a failed automatic Specs update keeps the previous tree until Retry succeeds | Ported |
| `plugins/blueprint/blueprint.spec.ts` (fork) | opening BLUEPRINT.md opens its author chat with the Blueprint as companion | Ported for a terminal author (`BlueprintChecks` UI); the chat author is excluded with Pi |
| `plugins/blueprint/blueprint.spec.ts` (fork) | a catalogued terminal author with no layout tab is restored with its Blueprint | Ported (`BlueprintRecoveryChecks`, local and gRPC: recorded, fresh and retry recovery) |
| `plugins/blueprint/blueprint.spec.ts` (fork) | a brief streams into a spec whose controls react to a change | Partial: streamed partials and control reconciliation are host checks (`BlueprintChecks` format/reconcile); the `@agent` run with a live model is not reproduced |
| `plugins/blueprint/blueprint.spec.ts` (fork) | the Claude host is offered only once its plugin is on | Ported (`BlueprintStartChecks.ClaudeGate`, with the real Claude Code plugin toggled while the dialog is open) |
| `plugins/blueprint/blueprint.spec.ts` (fork) | a takeover starts from what already exists, and only from inside the project | Ported (`BlueprintStartChecks` over gRPC: start labels, source switching, document pick, outside document refused with a notification) |
| `plugins/blueprint/blueprint-watch.spec.ts` (fork) | a terminal author's write reaches the pane through the watcher alone | Partial (`BlueprintChecks` UI: two on-disk rewrites reach the mounted pane); reload, frontmatter discard, outline, alert, Mermaid and session-record steps are not translated |
| `plugins/claude-code/claude-config.spec.ts` (fork) | claude.ai connectors and plugin servers appear as rows, switched only in /mcp | Partial: host parsing of a fake `claude mcp list` (`ClaudeCodeChecks`); pane rows and the absent menu are not driven |
| `plugins/claude-code/claude-config.spec.ts` (fork) | an imported instruction file is drawn as a branch of the file that pulled it in | Partial: host layer at depth one; the drawn connector is not asserted |
| `plugins/claude-code/claude-config.spec.ts` (fork) | the pane describes the Claude session in the active terminal, and says which | Pending |
| `plugins/claude-code/claude-config.spec.ts` (fork) | a narrow Claude pane scrolls its switcher, never the pane | Pending |
| `plugins/claude-code/claude-config.spec.ts` (fork) | the Claude pane reports the account and the usage windows it can name | Pending |
| `plugins/claude-code/claude-config.spec.ts` (fork) | the mcp health check reaches ThinkRail's own server, so the row is not a false failure | Pending |
| `plugins/claude-code/claude-config.spec.ts` (fork) | a list setting is composed entry by entry, empty rows dropped | Pending |
| `plugins/claude-code/claude-config.spec.ts` (fork) | a settings value is composed, scoped, shown as a diff, and only then written | Partial: the value composer dialog and a reviewed host write (`ClaudeCodeChecks`), not one pane-driven flow |
| `plugins/claude-code/claude-config.spec.ts` (fork) | an MCP server is added to the project's own file, then switched off from the same pane | Partial: host plan/apply to `.mcp.json` and the local deny list; the pane's composer and menu are not driven |
| `plugins/claude-code/claude-config.spec.ts` (fork) | a skill, a hook and a plugin are each composed, then written where the scope says | Partial: host plan/apply, plugin move and marketplace add through a recording CLI; composers, uninstall and marketplace remove rows are not driven |
| `plugins/claude-code/claude-config.spec.ts` (fork) | setting source links reveal the nested declaration, including after unsaved edits | Pending |
| `plugins/claude-code/claude-config.spec.ts` (fork) | a user setting can be overridden locally without changing its source | Partial: host plan/apply, winning local value and shadowed user value; the pane row is not driven |
| `plugins/claude-code/claude-launcher.spec.ts` (fork) | the launcher starts Claude Code in its own group, and offers per-run flags on right-click | Ported (`ClaudeCodeChecks.Launcher`: a recording CLI receives the agent-view variable, prompt file and model across both settings; tooltip hand-off is not asserted) |
| `plugins/claude-code/claude-launcher.spec.ts` (fork) | the launch command is a command line, and never reaches a shell blank | Ported |
| `plugins/claude-code/claude-review.spec.ts` (fork) | a review sent to a Claude Code terminal arrives as one prompt and the agent resolves it over MCP | Pending: needs a scripted agent answering over the terminal's MCP route |
| `plugins/claude-code/claude-terminal-facts.spec.ts` (fork) | ten terminal facts cases: file column beside the terminal, model facts and mid-chat switch, model/effort chips driving pickers, sealing, typed-prompt refusal, Esc fallback, TodoWrite plan, attach chip, spending | Pending as e2e translations; accessory identity, agent newline mode and transcript spending have focused checks in `ClaudeCodeChecks` |

Upstream cases combining supported docking with terminal/chat content need adapted
fixtures while preserving the layout assertions. Multi-client cases use a second window
of the same app (`E2eWorkspace.NewWindow`, Mod+Shift+N) as the in-process peer, and
separate clients of a real gRPC host for remote peers; `CutProxy` drops and restores one
client's connection where upstream routes a WebSocket. Separately launched local
processes are independent by design, so no case runs two app processes. Terminal
session sharing across clients remains pending with host-owned sessions.
No benchmarks or Git commits are run.

SharpRail regressions for the Projects-only tab model cover empty workspace startup, removal of legacy
bottom frames, explicit terminal splits, Projects tab menus and pane grouping. These replace retired
bottom-zone and alternate tab-mode behavior; they are additional coverage rather than upstream translations.

### Refresh, 2026-10-18

Compared the previously synced JetBrains `f7cca7bfb` with the fork tip `30a81cab5` (JetBrains `3bee4f164` plus
78 fork commits) in the read-only checkout; no fetch was run. Test titles were extracted from
`e2e/**/*.spec.ts` at each ref and compared. In the JetBrains range 45 case titles were added and 10 removed
or renamed; the fork's own suites did not change. Existing translations were checked against
`tests/SharpRail.Checks` (`Gate.Case` names and `upstream <suite>` markers); `--list-cases` of the only
published checks executable lacks recent cases, so the sources were used. No new scenario was ported: each
relevant one needs behavior that is not implemented yet (the cases would only fail), and the toolchain was not
built in this refresh.

Relevant to SharpRail and not yet translated (backlog):

| Suite | Upstream case | Status |
| --- | --- | --- |
| `close-quit-shortcuts.spec.ts` | native open-settings opens Settings, keeps it as is, and yields to other dialogs | Partial: `SettingsGitHubE2E` drives Cmd+, and `AppCommandChecks` a modal shielding Mod+W; an already-open Settings keeping its section and the tooltip chord are not asserted. |
| `close-quit-shortcuts.spec.ts` | Windows Ctrl+, opens Settings, from a terminal too; Shift+Comma does not | Backlog (S02): blocked by the missing chord. `SettingsGitHubE2E` currently asserts that Ctrl+, stays unbound on every platform and must be inverted with the feature. |
| `close-quit-shortcuts.spec.ts` | the browser binds no Settings shortcut | Backlog with S02 (browser acceptance, `tests/SharpRail.Browser.Checks`). |
| `changes.spec.ts` | large diffs render virtualized and wide rewrites wait behind a notice | Backlog (U27): needs the changed-line guard decision. |
| `changes.spec.ts` | a large source diff in the stacked review renders virtualized in a bounded viewport | Backlog (U27). |
| `changes.spec.ts` | a small edit in a large stacked file stays virtualized when context expands | Backlog (U27). |
| `layout.spec.ts` | resize hit areas leave the preceding panel's scrollbar edge alone | Backlog: Docking "Not yet ported". |
| `tool-rails.spec.ts` | rail clusters include unplaced tools, boundary drops create panes, and tools never join a terminal pane (renamed from "rail clusters mirror the panes ...") | Backlog: Checks "Not yet ported" (tool-rail journeys). |
| `settings-model-context.spec.ts` | custom budgets save on Enter, validate the 272K–1M range and keep unrelated config | Excluded: AI model context. The rename records the auto-save pattern SharpRail lacks (S01). |

Added or renamed upstream cases that are excluded (AI, pi, MCP or chat; nothing to translate):
`attention-background-work.live.spec.ts` (2), `chat-plan.spec.ts` (5 added, 2 removed: the TODO rail and the plan
page), `extensions-settings.spec.ts` (2), `mcp-settings.spec.ts` (6), `mcp-tool-cards.spec.ts` (3),
`new-workspace.spec.ts` (project trust for subagent definitions), `plan-ask.spec.ts` (1),
`plan-review.spec.ts` (4 added, 4 renamed: Session became Now, Start review became Check it),
`session-state.spec.ts` (4 added, 1 removed: unread results clear when seen), `skills-switches.spec.ts` (1),
`visualize.live.spec.ts` (1), `welcome.spec.ts` (the trust notice) and the `tool-rails.spec.ts` plan rail entry
cases (2). `review.spec.ts` gained three Pierre selection and virtualized-window cases (web diff engine
mechanics); review authoring itself is not implemented (R01–R08), so they stay pending with it.

Upstream suites that have no row in the inventory above. Most are chat or AI suites and are excluded as a group:
the `chat-*`, `composer-*`, `history-*`, `message-actions`, `model-picker`, `templates-*`, `subagent*`,
`skills-*`, `provider-*`, `00-jbcentral*`, `analytics-consent`, `ask-user-question*`, `agent-*`, `mcp-*`,
`plan-*`, `session-*`, `todos-plan.live`, `tool-cards.live`, `turn-divider.live`, `workflows/*` and the other
`*.live.spec.ts` suites, `shard-runner` and `perf/*` (Playwright harness). The rest, with what was found:

| Suite | Status |
| --- | --- |
| `tool-rails.spec.ts`, `rail-layout-regressions.spec.ts`, `project-home-chrome.spec.ts` | Pending translations, recorded in the checks contract's "Not yet ported". `RailRetentionChecks` is separate regression coverage. |
| `close-quit-shortcuts.spec.ts` | Partial: the quit and close cases are covered by `QuitConfirmationChecks` and `AppCommandChecks`, which are SharpRail checks rather than 1:1 translations; the Settings chord cases are listed above. |
| `settled-shelf.spec.ts`, `topbar-location.spec.ts` | Covered in spirit by `SettledWorkspacesE2E` and `ShellChecks`/`TopbarChromeE2E`; no per-case mapping is recorded, so the cases are not claimed as ported. |
| `changes-review.spec.ts`, `changes-review-drafts.spec.ts`, `changes-review-turn.spec.ts`, `review.spec.ts`, `phone-review.spec.ts` | Continuous Changes review is covered by `ChangesReviewChecks`; review authoring, drafts, turn scopes and phone review are not implemented (R01–R10). |
| `typography.spec.ts` | Replaced by the `--design` guards; no 1:1 translation. |
| `editor-save.spec.ts`, `new-project.spec.ts`, `terminal-links.spec.ts`, `tab-switch-latency.spec.ts`, `visualize.spec.ts` | Related checks exist (`FileSavingChecks`, `CreateProjectChecks`, the `terminal-links` case, `SwitchLatencyChecks`, `VisualizeE2E`); the first four are not mapped per case. |
| `custom-icons`, `fonts`, `host`, `shell`, `feedback`, `privacy`, `error-boundary`, `port-block`, `terminal-images`, `project-layout`, `activity-breadcrumbs`, `tool-file-links` | No translation located. `activity-breadcrumbs` and `tool-file-links` are chat surfaces (excluded); `feedback` and `privacy` are product telemetry and feedback surfaces (excluded); `port-block` and `host` are the web server's own harness; the others are unassessed backlog. |
| `plugins/codex/*`, `plugins/spec-dialect/*` | Plugin suites; see the plugin rows above and the plugin checks. |

## Backlog

The cross-module implementation differences and the acceptance work that remains, with the per-case E2E backlog in
[E2E porting inventory](#refresh-2026-10-18). Module specs keep the contracts and their "Not yet ported" sections;
dated task continuations stay in `context-log.md`.

### Current porting gaps

This is the cross-module inventory of applicable ThinkRail implementation differences and
Codex/Claude terminal workflow gaps. Module specs own contracts and durable verification facts;
Completion criteria own the completion requirements and the E2E porting inventory lists translations. There is no separate
current gap ledger. Embedded chat, pi execution and their internal services remain excluded.
`Decision` rows are proposals, not accepted feature designs; `Missing` means absent and `Partial`
means a narrower counterpart exists. Acceptance tasks do not imply missing implementation.

The research was performed on 2026-10-14 against `main` at
`16138700b2fbf86d845ad76a133dacb7d6ab1d5c` (`audit-thinkrail-gaps`), excluding other
workspaces and the original checkout’s uncommitted browser script change. It covered the
475-commit JetBrains history, its 98 spec paths and E2E catalogue, the 22 commits after the
recorded sync, fork omissions and SharpRail module gaps. The tracked upstream revisions at the
top of this document remain authoritative: research does not advance specification or code sync.
The rows include later workspace-13 integration and main fixes; the original revision remains
comparison provenance.

#### Implemented since the research baseline

| Original IDs | Implementation |
| --- | --- |
| A33, A34 | Four shared CLI workflow skills: interview/bug brainstorming, questions, routing and spec-graph guidance. |
| H11 | Spec traversal and authoring exclude the five assistant/mockup scratch folders. |
| P13 | Terminal companions start with the fork's 45% share. |
| L01–L05, L09 | Persistent edge rails, compatible tool/terminal panes, placement/drop rules and independent Project Home visibility. The bottom zone remains retired. |
| W01–W07, W09 | Host activity, settling overrides/settings, client partition/latch, shelf/paging/notice and differentiated creation/activity ordering. |
| U02, U07, G01 | Semantic typography, quiet scrollbars/curtains and the calm 40px location strip. Minimal palettes are included under the narrowed U01 entry. |
| V08 | Browser Settings renderer ownership is already fixed; normal-bundle and physical-browser acceptance remains V07. |
| W13 | Intentional divergence: workspaces start empty and terminals launch explicitly. Initial-terminal provisioning is no longer a product requirement. |

These dispositions do not close native appearance, physical-device, complete packaged-suite or browser-engine acceptance. The owning specs record verification scope and limits; automated passes do not establish physical-platform acceptance.

#### Changes since the recorded JetBrains sync

Applicable commits below are linked to their immutable source diffs. Chat/pi-only changes are omitted; mixed commits retain only their applicable parts. Later commits supersede earlier ordering requirements.

| Commit | Change and SharpRail disposition |
| --- | --- |
| [1ab1b1e31](https://github.com/JetBrains/thinkrail/commit/1ab1b1e31) | Denser named layout tokens and tighter line heights. Named dense layout tokens and semantic typography are now implemented; remaining surface fidelity is tracked under U03 and V05. |
| [951863595](https://github.com/JetBrains/thinkrail/commit/951863595) | Explain and pass project trust for skills, extensions, settings and MCP. Provider-specific configuration/trust exists; no common pre-launch project-resource trust summary. |
| [dedfc1bac](https://github.com/JetBrains/thinkrail/commit/dedfc1bac) | OS window focus suppresses desktop notifications. SharpRail already uses app-window focus; live signed-bundle behavior still needs verification. |
| [9d6ad0b2f](https://github.com/JetBrains/thinkrail/commit/9d6ad0b2f) | Calmer captioned location bar. The calm 40px location strip is implemented; branch headlines and broader PR treatment remain separate gaps. |
| [1166af01a](https://github.com/JetBrains/thinkrail/commit/1166af01a) | Login environment only for launchers opting in. SharpRail lacks the equivalent bounded host environment repair/import. |
| [530dffdb1](https://github.com/JetBrains/thinkrail/commit/530dffdb1) | Stable rail order, recent-activity topbar switcher. Superseded in part by newest-created-first at the tip; Activity stamps and differentiated rail/switcher ordering are implemented. |
| [e04b2681f](https://github.com/JetBrains/thinkrail/commit/e04b2681f) | Shell environment probe must not pollute history. The opted-in environment probe is absent; pi background-completion cleanup is excluded. |
| [bf4da6614](https://github.com/JetBrains/thinkrail/commit/bf4da6614) | No unread-result attention while background children still work. SharpRail has terminal status marks, not this normalized parent/child attention model. |
| [ba8d9d898](https://github.com/JetBrains/thinkrail/commit/ba8d9d898) | Restore design interview and durable spec amendment workflow. The shared CLI bundle now ships brainstorming, question, routing and spec-graph workflows. |
| [e5eeebffe](https://github.com/JetBrains/thinkrail/commit/e5eeebffe) | MCP Settings editor, presets, admission, sign-in and diagnostics. Claude configuration provides part of this; no common cross-agent manager. |
| [5ab283a35](https://github.com/JetBrains/thinkrail/commit/5ab283a35) | Show rejected design alternatives and tradeoffs. Rejected alternatives and durable spec-amendment guidance now ship in the brainstorming workflow. |
| [ffc934fa4](https://github.com/JetBrains/thinkrail/commit/ffc934fa4) | Background work keeps running treatment; seen results clear owner-wide unread state. No equivalent SharpRail lifecycle/read watermark across clients. |
| [be06fb019](https://github.com/JetBrains/thinkrail/commit/be06fb019) | Rail workspaces newest-created-first with Default pinned, switcher still recent-activity-first. The rail now pins Default and reverses registry creation order; the switcher orders by activity. |

| [4ab88b522](https://github.com/JetBrains/thinkrail/commit/4ab88b522) | Folder diff counts in the Changes tree only while collapsed. Not ported; see Panels "Not yet ported". |
| [b664b9cd7](https://github.com/JetBrains/thinkrail/commit/b664b9cd7) | Resize handles leave the preceding panel's scrollbar edge usable. Not ported; see Docking "Not yet ported". |
| [fdebc5c4e](https://github.com/JetBrains/thinkrail/commit/fdebc5c4e) | Virtualized file and diff views that load large files; guard for wide rewrites. Web-engine mechanics are excluded; the changed-line guard and the bounded stacked section are recorded as U27. |
| [a746cf3dd](https://github.com/JetBrains/thinkrail/commit/a746cf3dd) | Settings redesign. Recorded as S01. |
| [55666a7db](https://github.com/JetBrains/thinkrail/commit/55666a7db) | One notice and toast look across the app. Recorded as U26. |
| [e67317aee](https://github.com/JetBrains/thinkrail/commit/e67317aee) | Settings opens with Cmd+, on macOS and Ctrl+, on Windows and Linux. macOS was already ported; the other chord is S02. |
| [5c7b83c8b](https://github.com/JetBrains/thinkrail/commit/5c7b83c8b) | Loader redesign of the working badge. Moot until the workspace rollup badge exists (A17). |

The researched range contains 22 commits after `f7cca7bfb`; the remaining 13 up to `3bee4f164` were triaged on
2026-10-18 and only the applicable ones are listed (the rest are AI, pi or MCP: #706, #727 content, #728, #730's
chat parts, #744, #745, #748 and the pi package commits).

#### JetBrains commits after `3bee4f164` (triaged 2026-10-21)

Verdicts: ported, port (needed; owning module), adapt (needs a SharpRail design), not applicable.

Files, Git and review panels:
- `6f85210f3` merge Changes into a VS Code-like Files panel (#779): ported (`7d6a0187`, `51c8c632`; see Panels spec).
- `443919c57` one spelling for Windows directory and relative paths (#775): ported (`d2d885de`; Files.SPEC "Path spelling"); unverified on a Windows host.
- `1ce7211de` always show Remote and Pull request in the topbar with the branch's next step (#771): ported (`79991218`, with #776; UI SPEC Location bar, Git.SPEC "Branch status and actions").
- `46b522166` GitHub review details and actions in the topbar (#776): ported (`79991218`); Fix with agent and Merge locally are not.
- `5bdaf6a4e` extract file and diff renderers into a package (#783): not applicable; TS packaging refactor.
- `bbb0e3079` visualize workspace files and mockups in chat (#784): not applicable; chat and pi extension.

Projects and workspaces:
- `7641d7df9` rename projects from the project context menu (#764): ported (`e9a5d4e0`).
- `ac5476b55` mark a workspace as read or unread (#770): ported with a workspace-level unread model that closes A18 (566a059e; HostState.SPEC "Unread workspaces").

Chat, pi, MCP and agent internals (skipped by earlier passes; pi is out of scope, embedded chat is no longer restricted and these are gaps to take up with it):
- `2ce6ccb73` extension dialogs in chat, MCP "Always allow" (#749); `4499b93f9` calmer Connect dialog (#761); `58bb7f96d` host questionnaires vs extensions (#754); `988a50662` MCP call arguments in approval prompt (#763); `dfdcaa9be` background results row (#778); `da9bb4235` transcript reads without watcher wait (#786); `d3aae2d04` minor chat/renderer performance (#798).

Build, docs and website (not applicable: Bun/CLI/website only):
- `a9c560a37` JSC bytecode CLI (#781); `54fbcd55a` pin Bun 1.4.3 (#780); `abafac7a8` README stars badge (#785); `ebb8e23b6` 0.2.0 blog post (#768).

#### Agent workflow gaps for Codex and Claude Code

These gaps concern the supported terminal workflows. Provider-owned adapters and host-neutral contracts may be needed; Decision entries remain proposals rather than accepted feature designs. CLI-owned chat controls are excluded.

SharpRail counterparts: [Codex](src/SharpRail.Plugins.Codex/SPEC.md), [Claude Code](src/SharpRail.Plugins.ClaudeCode/SPEC.md), [shared accessories](src/SharpRail.Plugins.Agent.UI/TerminalFacts.cs), [rail](src/SharpRail.UI/ProjectRail.cs) and [notification collection](src/SharpRail.UI/Notifications/AttentionNotifications.cs).

| ID | Status | ThinkRail has; SharpRail lacks or narrows |
| --- | --- | --- |
| A08 | Partial | New-workspace launch supports prompt, image, skill and model choices. SharpRail has prompt/images and provider launcher choices, but lacks the full completion/template/skill-selection workflow. |
| A13 | Partial | Unified model/effort picker with favorites, recents, provider glyphs, compatible efforts and refreshed catalog. Codex has a catalog/picker; Claude has CLI-driven choices; no shared full preference/catalog semantics. |
| A14 | Partial | Consistent account connection and default model/effort controls for the supported CLIs. Launch defaults and provider-owned account/config surfaces exist, but their workbench presentation remains uneven. |
| A16 | Partial | Live tokens/cost/context telemetry with explicit finalized boundaries. SharpRail exposes rollout/transcript tokens and account limits, but not the complete common context/cost presentation; Claude deliberately avoids estimated subscription cost. |
| A17 | Missing | Workspace/project rollup replaces identity glyph with the shared working brand badge; attention is a separate static binary dot. Plugin decorations are on terminal tabs, including nested Projects tabs, not a normalized workspace/project rollup. |
| A19 | Missing | CLI-reported background work keeps the workspace working and suppresses premature done attention. Establish which lifecycle signals Claude Code and Codex expose before defining a shared status contract; SharpRail does not own their command/subagent execution. |
| A26 | Partial | Common MCP Settings: user/project scope, shadowed entries, stdio/HTTP editors, JSON paste, presets and field-preserving writes. Claude has reviewed configuration composers; Codex exposes configuration/provenance; no unified manager matching this complete workflow. |
| A32 | Partial | Portable skills loaded from multiple agent roots with source provenance, group/individual enablement, live stale/reload badge and project trust acknowledgement. Provider capability panes expose discovery, but no shared per-session manager with those guarantees. |
| A35 | Partial | Before starting work, explain which project resources trust admits and which run code; revoke/acknowledge through one projection. Claude/Codex have their own trust/config semantics, but no common Welcome/Start-work/Skills summary. |

#### Review and agent-run change gaps

Upstream authorities: [reviews](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/reviews/SPEC.md), [turn receipts](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/turns/SPEC.md), [panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md). SharpRail: [Panels contract](src/SharpRail.UI/Panels.SPEC.md), [continuous review](src/SharpRail.UI/ChangesReviewPane.axaml.cs), [Git contract](src/SharpRail.Host.Core/Git.SPEC.md), [Review panel](src/SharpRail.UI/ReviewPull.cs).

| ID | Status | Gap |
| --- | --- | --- |
| R01 | Missing | Durable review records with draft/sent/resolved/dismissed lifecycle, clear/archive and per-file finished state. SharpRail's Review panel is principally branch/PR actions; continuous Changes review is a diff reader with local triage. |
| R02 | Missing | Select code or a hunk and write a review comment on it; whole-file and whole-change-set remarks. Editor-to-agent selection context already exists and is not review authoring. |
| R03 | Missing | Base/worktree anchor spaces pinned to immutable original blobs, quoted context and stale/moved/outdated re-anchoring. Existing IDE selections and CAS revert hashes do not persist review anchors. |
| R04 | Missing | Image/SVG region, CSV cell, JSON pointer, notebook cell and PDF page-region anchors, geometry projection and unplaced-thread strip. No shared review contract on rich renderers. |
| R05 | Missing | Per-file accordion with editable drafts, sent history, finding badges, focus/jump and review flags on tabs. |
| R06 | Decision | Send one or all reviewed comments as structured context to the chosen Codex/Claude terminal, retain a per-file recipient and resolve comments through MCP. The fork's Claude review round trip remains pending; no complete host comment subsystem backs it. |
| R07 | Missing | Hunk Ask agent action with displayed hunk context, source-side semantics and a review composer. Revert/Undo and Keep exist; this third action does not. |
| R08 | Missing | Review-section scratch drafts survive collapse, virtualization, renderer changes and Stacked/One-file switching without accidental focus/submission. |
| R09 | Decision | Read-only Last turn and selected agent-run Changes scopes from before/after Git tree snapshots, persisted bounded receipts and turn-divider navigation. Current commit/branch/working/index scopes do not answer what one run changed. |
| R10 | Decision | Agent-neutral run-boundary receipts for Codex/Claude, including shell/generator writes rather than trusting tool arguments. Hooks would need a new bridge; overlapping runs must remain interval changes, not exclusive authorship claims. |
| R17 | Partial | Complete review keyboard ownership: one active review tab answers V/J/N/P, and modal/menu/text-entry/terminal input wins. Current specs retain the shared keyboard gate as an open requirement. |
| R18 | Partial | Delayed section sizing preserves requested scroll/reveal until content can satisfy it or the user takes over. Current outer scroll restores only the available offset. |
| R19 | Partial | Registry-declared natural versus bounded review section height. Source/Markdown grow; other formats use generic bounded viewports rather than their own capability contract. |
| R20 | Missing | A diff whose file left the change set announces identical sides. Current open diff retains its previous content instead. |

#### Workbench and docking gaps

Authorities: upstream [layout](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/layout/SPEC.md), [intents](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/layoutIntents/SPEC.md); SharpRail [Docking contract](src/SharpRail.UI/Docking/SPEC.md), [UI contract](src/SharpRail.UI/SPEC.md), [profile](src/SharpRail.UI/State/ProfileStore.cs), and the older fork log in [Fork port log](#fork-port-log).

| ID | Status | Gap |
| --- | --- | --- |
| L06 | Partial | Preserve surviving pane chrome and bodies through visibility, folds and topology changes, not just cached document controls or unchanged refreshes. Full latest remount-free contract remains open. |
| L08 | Partial | Drop normalized no-op writes and atomically commit document/attention/tool fan-out; review-local writes invalidate only their body. |
| L10 | Missing | Older fork's per-project frame stash: returning to a project restores its own arrangement. Current profile owns a frame per window and workspace document views. |
| L11 | Missing | Older fork's drawn tab-placement picker instead of only text destination menus. |
| L12 | Partial | Older fork's open-beside-Claude rule and unoffered-tool handling in Projects tab mode. The core vertical/Projects mode itself is implemented. |

#### Projects and workspace lifecycle gaps

Authorities: upstream [workspaces](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/workspaces/SPEC.md), [panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md), [latest ordering](https://github.com/JetBrains/thinkrail/commit/be06fb019); SharpRail [Workspaces](src/SharpRail.Host.Core/Workspaces.SPEC.md), [HostState](src/SharpRail.Host.Core/HostState.SPEC.md), [State](src/SharpRail.UI/State/SPEC.md), [Panels](src/SharpRail.UI/Panels.SPEC.md).

| ID | Status | Gap |
| --- | --- | --- |
| W08 | Decision | Automatic settling on merged/closed PR and authoritative host review snapshot. Existing PR lookup does not provide this lifecycle. |
| W10 | Partial | Active workspace's parent project is revealed on initial mount/ownership change while a later manual collapse stays respected. SharpRail primarily restores its collapsed set. |
| W11 | Missing | Prefetch a selected non-default base off the creation critical path. Default-base prefetch and create-time fetch already exist. |
| W12 | Missing | Retry rejected workspace creation inside the same dialog. SharpRail closes it before the operation and reports failure separately. |
| W14 | Missing | Seed the scratch directory when a session starts in a user-owned attached workspace. Managed creation seeding is not that case. |
| W15 | Partial | Managed branch rename after its initial automatic name. First `set_title` names a untouched generated branch; later label renames deliberately do not rename Git. |
| W16 | Decision | Create a new project through an agent-assisted setup brief, not only open/clone an existing project. Existing starting-a-new-project skill does not provide this native flow. |
| W17 | Partial | Git-only pane withholding is implemented for plain folders, including direct actions and restoration; empty/unborn Git repositories intentionally remain eligible. The older fork's gitless Start-work explanatory copy and launcher-state fidelity are not established by the pane regressions and remain to be reviewed. See [Panels](src/SharpRail.UI/Panels.SPEC.md). |

#### Location bar and Git UI gaps

Authorities: upstream [location bar](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/locationBar/SPEC.md), fork history in [Fork port log](#fork-port-log); SharpRail [header layout](src/SharpRail.UI/WorkbenchWindow.axaml), [location actions](src/SharpRail.UI/LocationBar.cs), [branch list](src/SharpRail.UI/BranchList.cs), [panel gaps](src/SharpRail.UI/Panels.SPEC.md).

| ID | Status | Gap |
| --- | --- | --- |
| G02 | Missing | Header Remote divergence and Pull request chips; open/merged/closed state, provider URL and authoritative snapshot arbitration. PR details are confined to Review. |
| G03 | Partial | Branch card's separate Based on row, Remote/review rows and shared Compare-to chooser. Caption currently derives “from” from the comparison choice, so creation base and retargeted comparison are not separately represented. |
| G04 | Partial | Shared searchable comparison branch combobox; Local/Remote folder groups, leaf labels, full-ref tooltips and chosen target. Current Changes target menu does not implement the full grammar. |
| G05 | Partial | Fork's topbar branch picker lists every remote collapsibly and click switches to an existing worktree or starts a workspace. Native branch list currently lists local refs and delete/fetch actions. |
| G06 | Partial | Fork's branch deletion can first remove its worktree, with explicit dirty-force confirmation. Dirty workspace deletion exists, but branch deletion refuses occupied branches rather than composing the teardown. |
| G07 | Missing | PR setup guidance Run action opens a workspace terminal; preserve Git's authentication error text and host-key hint; remote-host commands follow host platform. Copyable generic guidance exists. |
| G08 | Missing | Registered OS URL scheme for serialized project/workspace/resource links. Serialization, in-app history and `--link` launch routing are implemented. |
| G09 | Partial | Remote host confirms workspace existence before history traversal. Current remote navigation cannot make that confirmation. |
| G10 | Partial | Back/Forward records user navigation separately from passive remote removal/close fallbacks, with observed locations persisted as links. Current history records any location change. |
| G11 | Missing | CI status beside the pull request chip is a recorded SharpRail request. [Panels contract](src/SharpRail.UI/Panels.SPEC.md) lists it as missing; [LocationBar.cs](src/SharpRail.UI/LocationBar.cs) and the window header have no CI chip. Keep separate from JetBrains parity. |

#### Rendering and resource gaps

Authorities: upstream [resource registry](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/resources/SPEC.md), [rich panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md), [themes](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/themes/SPEC.md); SharpRail [registry](src/SharpRail.UI/Resources/ResourceRegistry.cs), [Rendering](src/SharpRail.UI/Rendering/SPEC.md), [Themes](src/SharpRail.UI/Rendering/Themes.SPEC.md), [Core PDF preview](src/SharpRail.UI/Resources/Pdf/SPEC.md), [Scintilla limits](src/SharpRail.Scintilla/README.md#limits).

| ID | Status | Gap |
| --- | --- | --- |
| U01 | Partial | Minimal Light and Minimal Dark now join the four original palettes. The latest independently tuned standard Light solid accent remains unported. |
| U03 | Partial | Dense named layout tokens, semantic typography and line heights are implemented, including the 40px location strip, 28px panel headers and 24px header controls. Remaining surface adoption and matched-content fidelity belong to the visual acceptance audit. |
| U04 | Missing | User-selected code font and ligatures across editor, terminals, Markdown code and diagrams. Font size controls are not font-family selection. |
| U05 | Partial | Consume manifest syntax colours consistently across source and Markdown code. Editor TextMate grammars and theme updates exist, but their palette is separately authored; code fences have narrower tinting. |
| U06 | Missing | Content-shaped loading skeletons and resolved-content fade-in. Several native placeholders remain text. |
| U08 | Partial | Shared tooltip timing and disabled-trigger wrapper; pointer-transparent fork tooltips need framework-specific acceptance. Avalonia tooltips and individual disabled-control tips already exist. |
| U09 | Missing | Shared non-modal dialog/inspector semantics, Escape and focus return while other controls remain usable. Most native dialogs are modal windows. |
| U10 | Missing | Toast swipe dismissal and timer pause on hover/focus; route remaining gesture/error-line notices through the bounded/coalesced toast queue. |
| U11 | Partial | Find paints every match rather than just selecting the current one. Terminal search and the full preview search surface remain narrower than the fork's expectation. |
| U12 | Partial | Workspace-search Markdown hit opens and flashes the matching rendered block. Navigation exists; block flash does not. |
| U13 | Missing | Registry advertises phone support, anchor geometry, copy/layout/whitespace and natural/bounded height per renderer. Current registry primarily declares matching/rank and view/diff factories. |
| U14 | Missing | Sanitized HTML preview and 2-up HTML diff. HTML currently opens as source; notebook HTML output also lacks the upstream sandboxed rich presentation. |
| U15 | Missing | PDF page-pair diffs, synchronized page sequence and 2-up/swipe/onion/difference comparison. PDF file preview is core functionality. |
| U16 | Partial | PDF viewport render window with two-page overscan, cancellation/release of offscreen canvases, page/zoom state restoration and page-region review focus. Current core preview rasterizes a full page set and has no review geometry. |
| U17 | Partial | Free image zoom/pan gestures and intrinsic-space region selections. Fit/natural/button zoom and four visual diff modes already exist. |
| U18 | Partial | JSON array move detection and stable object hashes. Native alignment presents moves as removed/added. JSON/JSONC trees themselves exist. |
| U19 | Partial | Notebook source-language treatment, rich HTML outputs and structural review/selection parity. Notebook view/diff exists; full upstream output and authoring contract does not. |
| U20 | Partial | Rendered Markdown raw HTML tables/lists/styles and text wrapping around floated pictures. Current limited HTML treatment can show text or place a floated picture on its own line. |
| U21 | Partial | Regroup a multi-block `<details>` in Markdown diffs; compare list items robustly when documents contain different counts of top-level lists. Current native diff has documented limits. |
| U22 | Partial | Per-tab code/table/JSON/notebook view state and delayed scroll restoration after detach/reattach. Markdown/source positions and image-mode state are implemented subsets. |
| U23 | Partial | Deleted-file banner in every applicable preview, not only source editor; other file formats mainly receive the deleted tab mark. |
| U24 | Partial | Editor text input exists, but full IME preedit, an accessibility text provider, touch selection handles and a copy menu are absent, confirmed by [Scintilla limits](src/SharpRail.Scintilla/README.md#limits). These are implementation gaps, followed by platform acceptance; completion/LSP workstation features are not automatically upstream requirements. |
| U25 | Partial | Native AppKit Edit role items work inside native picker fields, plus About. Current Avalonia menu uses app commands instead. |

#### Settings and notice gaps

Authorities: JetBrains [panels](https://github.com/JetBrains/thinkrail/blob/3bee4f16454515674c5548cb96ed46c57c2a4956/apps/web/src/panels/SPEC.md), [shell](https://github.com/JetBrains/thinkrail/blob/3bee4f16454515674c5548cb96ed46c57c2a4956/apps/web/src/shell/SPEC.md) and [notices](https://github.com/JetBrains/thinkrail/blob/3bee4f16454515674c5548cb96ed46c57c2a4956/apps/web/src/styles/NOTIFICATIONS.md); SharpRail [Settings](src/SharpRail.UI/Panels/SPEC.md), [UI](src/SharpRail.UI/SPEC.md) and [Rendering](src/SharpRail.UI/Rendering/SPEC.md).

| ID | Status | Gap |
| --- | --- | --- |
| S01 | Partial | Settings redesign: grouped rail, shared row primitives, the one auto-save input pattern with Escape reverting the draft, Appearance mode and contrast controls, Layout applied and default marks, Add new layout, collapsed Advanced limits and one confirmation. Section content mostly exists; the shapes do not. |
| S02 | Missing | Ctrl+, opens Settings on Windows and Linux (not from Shift+Comma, an already-open Settings stays as it is, the tooltip names the chord only where bound). |
| U26 | Missing | Neutral notice and toast fill with severity only in the border and icon, a warning severity and the single-row or two-row layouts. SharpRail toasts keep the variant stripe. |
| U27 | Decision | A changed-line guard for expensive diffs and a bounded, virtualized stacked section above a line threshold. Measure the native diff before adopting the web thresholds (2000 changed lines, 1000 source lines). |

#### Host and transport gaps

Authorities: upstream [shared environment](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/shared/SPEC.md), [host](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/host/SPEC.md), [logging](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/log/SPEC.md), [watchers](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/watch/SPEC.md); SharpRail [Core](src/SharpRail.Host.Core/SPEC.md), [Files](src/SharpRail.Host.Core/Files.SPEC.md), [Git](src/SharpRail.Host.Core/Git.SPEC.md), [Client](src/SharpRail.Host.Client/SPEC.md), [Remote](src/SharpRail.Host.Remote/SPEC.md), [protocol counter](src/SharpRail.Host.Abstractions/Handshake.cs).

| ID | Status | Gap |
| --- | --- | --- |
| H01 | Missing | Bounded login-shell PATH/SSH_AUTH_SOCK repair and environment import for opted-in GUI launchers (the import is now opt-in at boot, #714), inherited-value precedence, noisy-probe parsing, retry and excluded control/history variables. Terminal login-shell spawning alone does not fix the host's own child environment. |
| H02 | Missing | Structured leveled rotating host logs and crash report under state directory. Console diagnostics are not this persistent support surface. |
| H03 | Partial | One bounded idempotent graceful shutdown path awaited by every launcher, signal handling and in-flight work drain. Existing disposers are not evidence of the entire upstream contract. |
| H04 | Missing | Retrying recursive teardown and automatic first-free-port selection for launchers. Explicit listener configuration is a different contract. |
| H05 | Partial | Streamed bounded file/blob bytes and child output, with cancellation/nonzero/truncation semantics. Native single-message byte reads and callback-loaded images work, but do not offer streamed large-resource transfer. |
| H07 | Partial | Additive capability gates for later host operations such as path inspection/prewarm/spec queries. Current protocol is 6 and request replay is enabled; not every later operation has its own introduction gate. |
| H08 | Partial | Immutable original snapshot for index/working-side diffs. Index bytes can change between reads; commit-side scopes already resolve immutable originals. |
| H09 | Missing | Periodic identity healing when a watched root is replaced while all subscribers remain attached and no fresh admission occurs. Current watcher heals on a new subscriber/prewarm or error. |
| H10 | Missing | Watcher admission/activity lifecycle seam that seeds retained HEAD baseline before metadata events, including recreation and failed-registration cleanup. |
| H12 | Partial | Full frontmatter YAML semantics for graph metadata: anchors, nested maps and multiline scalars. The portable spec dialect is deliberately narrower; editing preview properties does not make graph parsing a full YAML parser. |
| H13 | Partial | Windows shell resolution, ConPTY and platform filesystem/native build paths are implemented. The Settings shell picker and per-shell startup guidance are missing; current guidance only distinguishes SHELL from the default. See [Windows](WINDOWS.md), [terminal contract](src/SharpRail.Host.Core/Terminals.SPEC.md) and [startup handling](src/SharpRail.Host.Core/PtyTerminalService.cs). Real Windows execution is tracked once under V09. |

#### Fork plugin differences and acceptance gaps

These entries retain old omissions that a JetBrains-only delta audit would miss. Sources are [Fork port log](#fork-port-log), [E2E porting inventory](#e2e-porting-inventory), owning module specs and implementation. Implemented plugin behavior is not listed as a feature gap merely because a native acceptance journey has not been run.

| ID | Status | Gap |
| --- | --- | --- |
| P01 | Partial | Plugin `WorkspaceUpdated` on actual branch switch; existing creation/removal/relabel notifications are narrower. [Host plugin runtime](src/SharpRail.Host.Core/Plugins.SPEC.md). |
| P03 | Partial | Faint-cell blanking when accessories read terminal buffer tail. Native text reads lack the cell-attribute information; picker placeholder detection can therefore differ. [Terminal](src/SharpRail.UI/Terminal/SPEC.md). |
| P04 | Partial | Claude IDE `openDiff` displays proposed unsaved content and returns real accept/reject/save results. It currently opens the target file and reports `diffShown: false`, also the fork's limitation; this is an integration opportunity rather than regression against that fork. [Claude](src/SharpRail.Plugins.ClaudeCode/SPEC.md). |
| P05 | Decision | Claude IDE diagnostics/execute-code capabilities. They are not offered; compare real supported CLI/IDE protocols before adding them. They are not declared fork parity requirements. |
| P06 | Partial | Codex IDE reply lists editors per answering frontend. Current app context unions editor tabs across windows in the workspace; background-window focus selection is otherwise implemented. [Codex](src/SharpRail.Plugins.Codex/SPEC.md). |
| P07 | Partial | Codex failed-state/summary notifications. Its current hook vocabulary has blocked/done but no failed status or summary; cancellation stays silent. Claude has richer failure notifications. |
| P08 | Partial | Codex skill-file ownership across multiple hosts and shutdown. User edits are protected, but disabling and host shutdown share disposal, and a second host is not coordinated. |
| P10 | Partial | Input sealing during picker driving is missing: [ClaudeTerminal.cs](src/SharpRail.Plugins.ClaudeCode/UI/ClaudeTerminal.cs) shows a notice without an exclusive-input overlay. Model/effort, attach, usage and plan controls exist; [Pickers.cs](src/SharpRail.Plugins.ClaudeCode/UI/Pickers.cs) implements draft refusal and Esc fallback. Live terminal-facts acceptance is included in V12 below. |
| P11 | Partial | Codex Windows IDE IPC is absent: [IdeBridge.cs](src/SharpRail.Plugins.Codex/Host/IdeBridge/IdeBridge.cs) declares Windows unsupported and [Codex checks](tests/SharpRail.Checks/CodexChecks.cs) skip it there. The registered model supplier is not consumed by a reactive Start-work selector. The failed recovery check is recorded under V06; installed CLI and native lifecycle acceptance is included in V12. |

#### Notifications and platform acceptance gaps

Authorities: [UI](src/SharpRail.UI/SPEC.md), [terminal contract](src/SharpRail.UI/Terminal/SPEC.md), [browser](src/SharpRail.Browser/SPEC.md) and [browser acceptance](src/SharpRail.Browser/SPEC.md#acceptance). Implementation gaps are listed here; acceptance tasks follow separately.

| ID | Status | Gap |
| --- | --- | --- |
| V01 | Missing | Equivalent away notifications outside macOS and explicit system-permission state presentation; current denial can be silent. |
| V02 | Partial | Multiple attention notifications lead to a chooser. Current batch opens the latest terminal rather than letting the user choose among all waiting terminals. |
| V04 | Missing | Busy-close confirmation does not dismiss when another client closes the terminal. [TerminalTabs.cs](src/SharpRail.UI/TerminalTabs.cs) awaits a normal confirmation without catalog-driven invalidation; this remains an explicit [terminal contract](src/SharpRail.UI/Terminal/SPEC.md) implementation gap. |

#### Outstanding acceptance work

These tasks consolidate the remaining checks; they do not assert missing product features. Owning specs retain durable verification scope and measurements; [the checks contract](tests/SharpRail.Checks/SPEC.md#recorded-verification-scope) distinguishes source and packaged evidence. Original row IDs in parentheses identify merged entries. No unrun journey is treated as passed.

| ID | Latest result | Remaining work |
| --- | --- | --- |
| V03 | Not run | In a signed macOS app, see a real away notification and click it to focus the correct terminal/window. Delivery/activation wiring exists and recording-notifier checks pass. |
| V05 | Not run | Compare native Settings, menus, Markdown, all plugins and docking at matched content/dimensions; exercise PDF trackpad magnify. Include drop/overflow/spacer/folded-weight geometry (L13, P15–P18). Functional/package checks pass, but there is no matched capture corpus. |
| V06 | Failed/incomplete | Complete the packaged gate after resolving its 2026-10-14 settings-observer timeout, Codex recovery failure and unfinished terminal/Blueprint cases; the later 131-case aggregate source pass is not packaged acceptance. Add direct regressions for reused-tab-ID workspace focus, held Visualize render/remount and same-terminal/two-window rendering, held graph requests after detach/disable, and held PDF read/raster replacement/detach (L07, P12, P14–P16). Release, formatting, package identity/signature and 13 targeted cases pass; the full gate does not. |
| V07 | Not run on required platforms | Exercise Safari/Firefox, non-macOS keyboards/IME, real notification permission and production HTTPS/proxies. Accept the Settings fix in a normal bundle and on physical Retina; its parallel-workspace Chromium test-build acceptance already passes. |
| V09 | 2026-10-14 audit: portable checks only | Execute the Windows package and its ConPTY, Recycle Bin, native editor/Skia, clipboard/IME and client journeys. Portable macOS checks skip Windows runtime branches. This covers the platform part of H13. |
| V10 | Emulator/fixture coverage only | Exercise physical Android, camera/QR and real two-device Tailscale pairing, approval, reconnect, revoke and remote UX, including packaged deployment. Emulator and fake-identity transport checks pass. |
| V11 | 120 Hz presentation not measured | Measure physical 120 Hz browser presentation. Recorded restty GPU draw cadence is about 60/s and output throughput is 111 FPS; neither proves the presentation target. No new benchmark was run. |
| V12 | Fixture coverage only | Exercise installed Codex/Claude during sustained streaming while typing/resizing/switching controls, interruptions, process replacement, auth/model changes, editor reload/remount/title changes, remote reconnect and physical drops (A36, P10–P11). Complete Claude pane-driven composers/account/usage/MCP health, nested source navigation after unsaved edits, uninstall/marketplace removal and overrides (P09); Blueprint live-model controls, frontmatter discard, outline, alerts and Mermaid (P12); and live Discord IPC/presence (P18). Generic host/UI tests do not establish these journeys. |
| V13 | Linux: cross-built, headless gate and Xvfb launch only | Run the AppImage on real distributions (the 2026-10-19 pass ran its payload, not the image: the runtime could not execute under Rosetta here; `check:packaged -- --linux` unpacks it in CI). Cover a real window manager (title bar, resize, HiDPI), Wayland/XWayland, IME and clipboard, file drops, `notify-send` delivery, `xdg-open`/`gio trash`, a reflink filesystem for workspace clones, glibc 2.31 hosts and Git 2.36+. Mermaid has no Linux library. linux-arm64 is cross-built (2026-10-20) but nothing has run on an arm64 host or runner yet: the first CI/nightly `appimage-arm64` run is its first test. |

#### Suggested implementation batches

These are priorities inferred from the comparison, not accepted feature designs.

1. Close the remaining normal-bundle, real-platform input and notification acceptance gates; browser Settings renderer ownership is implemented.
2. Finish retained-chrome, per-project frame, placement-picker and visual-fidelity gaps; rails, compatible panes, Home visibility and density/location typography are implemented.
3. Extend implemented activity/settling with the remaining workspace attention/read and PR-driven semantics.
4. Build review records/anchors and terminal send/resolve, then evaluate CLI run-boundary receipts for Last turn changes.
5. Close CLI configuration, MCP configuration and skills/design workflow gaps through the provider plugins and shared shipped skills.
6. Close resource parity: HTML/PDF diffs, renderer capabilities, view state, rich anchors and font/syntax consistency.

The inventory records implementation dispositions and outstanding decisions. Durable verification facts and their limits belong in the owning module specs; dated task continuations remain in context-log.md.
