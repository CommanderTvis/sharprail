# tereditor status

One ledger for what completion requires and what is still open. [History](docs/history.md) records where tereditor
came from.

- [Completion criteria](#completion-criteria): the original scope, the evidence for each requirement and its limits.
- [Backlog](#backlog): known gaps and outstanding acceptance work.

## Completion criteria

The goal remains active. Passing functional checks does not prove the complete
visual and docking contract. This section preserves the original scope and
identifies the evidence still needed before declaring completion.

This section keeps completion criteria and milestone evidence; module specs keep the contracts, and the
[Backlog](#backlog) keeps open gaps. `SPEC.md` is the reference authority. When prose and the current renderer
disagree, record the distinction rather than silently substituting an easier behavior.

| Requirement | Current evidence | Completion limit |
| --- | --- | --- |
| C# host independent of Pi/AI; direct local calls and typed WebSocket RPC remote | Host projects, `ProjectChecks.cs`, published local/remote parity, cancellation and Git parity checks | Functional prototype evidence; no production remote deployment claimed |
| NativeAOT app and remote host, nullable checking, single latest package | `NativeAot.props`, `scripts/mise run package:macos`, `check:packaged` requires NativeAOT payloads and runs the shipped binaries (`--packaged`) | Preserve these checks after every implementation change; iOS execution/distribution remains the documented future gate |
| Functional tabs, preview/keep, close/context actions, reorder/overflow/focus and deferred navigation | `UiChecks.cs`, `NavigationChecks.cs`, `DockInputChecks.cs`; native double-click and drag captures | Final contract audit must cover the combined interaction surface, including all drop collisions and viewport changes |
| Persistent settings and XAML where it fits | Six compiled XAML files; settings persistence/theme/font/width/preset checks and native Settings capture | Menu/control typography comparison against the reference is still incomplete |
| End-to-end scenario checks | Headless E2E checks in `tests/Tereditor.Checks/E2E` covering themes, multi-window and remote sync, workspaces, terminals, docking, Markdown, Changes and the editor | Inner chrome across workspace transitions and the full theme catalog remain open |
| Multiple windows and shared host state (settings, presets, labels, projects, workspace lifecycle) with local events and WebSocket RPC streaming | `MultiClientE2E.cs` and the synced cases in `ThemeE2E`, `LineWidthE2E`, `LayoutSettingsE2E` and `ProjectContextE2E` (`--sync` runs them); per-window profile entries and one-time `state.json` migration | Terminal sessions shared across clients arrive with host-owned sessions; separately launched local processes stay independent by design; native multi-window use (menu bar entry, window restore order) is unverified outside headless checks |
| Native Markdown preview with actual scrolling and selectable content | Markdig renderer checks; wheel/anchor/navigation tests; native scroll and rendering captures | Reference typography, spacing and complete representative-document comparison still need final visual review |
| Opening projects, workspace/window restoration, Git changes/diffs and worktree workflows | `ProjectChecks.cs`, `GitUiChecks.cs`, isolated Git fixtures, restoration/navigation checks; explicit-target merge-base/net-working-content and commit catalog local/remote regressions; commit picker, lightweight index-independent catalog parity, workspace selection and fresh-window persisted-query checks | Commit membership refresh/fallback and complete read-error fidelity remain open; visual fidelity of the corresponding panels/dialogs still needs final review |
| Exact frame geometry, docking grammar, panes, alignment, fold/show/restore and resize behavior | `LayoutChecks.cs`, pointer/menu/keyboard checks, source-verified side projection and collision regressions | Auxiliary stack projection with multiple folded/expanded groups and narrow viewports needs a source comparison; verify any resulting gaps |
| Screenshot-perfect layout and details | [Recorded native comparison limits](src/Tereditor.UI/Rendering/SPEC.md#native-visual-acceptance-limits) | Sampled matches are insufficient: native glyph/control/menu differences remain unresolved or unverified |
| Publish and show the finished app; no Pi/AI or benchmarks | Canonical `artifacts/tereditor.app`, published runtime checks, own-window native captures; implementation scope in `SPEC.md` | The current package is a working prototype, not yet proof that all preceding completion limits are resolved; macOS terminal execution and the Scintilla editor are separately in scope |
| Android client of a remote host | `src/Tereditor.Android` and its `SPEC.md`; Debug and Release builds run on an Android 16 arm64 tablet emulator against `Tereditor.Host.Remote` (connect and auto-connect, workbench, menus, dialogs, project by host path, file tree, Markdown, Scintilla editor, live terminal, Settings, Disconnect, Back), with scope recorded in the owning Android spec | Done with limits: emulator only. Physical devices, phones, IMEs other than Gboard, accessibility, plugin UI beyond the default workbench, HTTPS and CI are unverified; cleartext HTTP, an unencrypted stored token, one window, no on-device tests, untrimmed JIT package |

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
and file-icon raster checks; see the newest src/Tereditor.Host.Core/Language/SPEC.md entry for exact failures.

The 2026-10-14 LSP revisions pass focused checks for full-catalog startup, passive
Problems queries, whole-pane scrolling, status icons, context-menu usages popups and
semantic replacement. The latest full gate is incomplete at diff-opening waits in
RenderedDiffE2E and ChangeActionChecks; lanes 3 and 4 complete. See src/Tereditor.Host.Core/Language/SPEC.md.

Workspace LSP support has host/local/WebSocket and editor regression coverage, plus
real TypeScript, Pyright and clangd checks. The remaining catalog installation recipes
need live verification on their supported hosts; SDK-dependent servers require the
documented prerequisites. Shared indexes use only server-supported cache mechanisms,
with mutable analysis isolated by workspace. The full regression gate remains
incomplete at shared-terminal creation and startup timing checks (see src/Tereditor.Host.Core/Language/SPEC.md).
Android/browser compilation and native appearance of the new editor controls remain
unverified.

The LSP management pane and explicit Codex/Claude capability entries have focused
host, terminal-authenticated MCP, editor and workbench mounting coverage. The later
four-lane gate remains incomplete at rendered-diff and shared-terminal checks; its
catalog-tool fixture assumption was corrected and passes the targeted layout run.

The browser client is implemented in `src/Tereditor.Browser`, with full shared-workbench parity as its
contract and exclusively remote host adapters. Chromium acceptance covers WASM Scintilla editing,
save conflicts, live Ghostty PTYs and reconnects, PDFium text/raster/Unicode/rotation/crop, Mermaid,
modal settings, view-state persistence and external managed plugin UI/settings.
The published bundle passed a Chromium smoke check. Source and packaged results have different scopes; the [checks contract](tests/Tereditor.Checks/SPEC.md#recorded-verification-scope)
records the latest retained integration evidence without upgrading interrupted packaged runs. Safari, Firefox,
non-macOS browser keyboard/IME behavior, real notification permission and production HTTPS remain
platform verification gates; they do not define a reduced browser feature set.

Native batched text matches the shared renderer pixel for pixel. Restty is now the default;
its recorded output rate is distinct from GPU draw cadence, and 120 Hz presentation remains unverified. See the [browser measurements](src/Tereditor.Browser/SPEC.md#recorded-rendering-evidence) and [packaged-gate limits](tests/Tereditor.Checks/SPEC.md#recorded-verification-scope).

The current docking, visual comparison and final-package gates are listed in
[Outstanding acceptance work](#outstanding-acceptance-work).

MacOS Scintilla/Skia integration is now a separately authorized scope extension.
Its architecture and remaining editor limits are recorded in
`src/Tereditor.Scintilla/README.md`; this does not close the broader fidelity gates.

### Plugin API

The Plugin API and all nine original builtins are registered. Functional host,
local/remote UI contracts and acceptance limits are recorded in the owning plugin specs and
[checks contract](tests/Tereditor.Checks/SPEC.md#recorded-verification-scope), with
[acceptance tasks](#outstanding-acceptance-work) consolidated separately. Historical milestone evidence does not
close the current source-fidelity, native appearance or final-package gates.

### Tailscale pairing completion limits (2026-10-11)

The optional builtin Tailscale plugin owns tailnet discovery and pre-connection device approval. Its
injected serving bridge supplies approved-token authentication and embedded listener startup; the host
remains transport-independent below Remote. See [the plugin contract](src/Tereditor.Plugins.Tailscale/SPEC.md).

The new builtin's implementation and automated host/client/UI checks are complete;
desktop and Android Release builds pass. Full gate coverage completes across its
initial run and successful resume, with optional Git-source fixtures skipped.
Acceptance on two real Tailscale devices, physical Android UI and the packaged
application remains unverified. The owning module spec is draft pending review,
and the changes are preserved in signed commits.

### Keymap editor

Settings › Keymap with the built-in IntelliJ preset and custom presets over host state (protocol 18). Covers the window commands in `KeymapActions`; editor cursor keys, terminal keys and plugin-contributed keys are not bindable, and keymap import/export, per-workspace keymaps and key sequences are not built. The first edit of IntelliJ creates "My keymap" without a prompt; that path is not exercised headlessly.

### Verification scope

The [checks contract](tests/Tereditor.Checks/SPEC.md#recorded-verification-scope) retains the dated
source-gate and packaged-gate distinction. Earlier feature-workspace and partial-gate milestones
do not describe the current main tree or close platform acceptance.

### Document and tree rendering limits (2026-10-11)

Viewport/drawn-row behavior is implemented; its historical surface measurements are scoped evidence. The user-authorized synthetic
measurements and their tradeoffs are recorded in the [UI kit spec](src/Tereditor.Plugins.UI.Kit/SPEC.md#recorded-large-document-measurement-limits). They do not establish
native presentation latency or whole-app RAM savings. The measured Files allocation tradeoff and graph resident-memory limit do not imply whole-app savings.
Markdown virtualization is between top-level blocks and chunks of large top-level
tables, lists and code blocks, from estimated heights refined on realization, with full
content retained for selection and Find; nested large blocks and media storage remain
unbounded internally. Native screen-reader and physical Android acceptance, packaging
and a running-app review have not been performed for this change.

The browser uses Restty WebGL2 by default, with Skia only as an initialization fallback.
Its [owning spec](src/Tereditor.Browser/SPEC.md) records current capabilities and rendering limits;
headless cadence does not establish desktop performance parity.

### Workspace switching acceptance

Focused Projects retention and geometry checks do not establish native animation or physical-client
acceptance. Current cross-module gates remain in the [acceptance inventory](#outstanding-acceptance-work).

### Language intelligence acceptance

Language services and agent workspace configuration are implemented. Focused host/UI and real-server checks pass; full-gate Git-only Changes fixture failures and platform acceptance limits remain recorded in the language and checks specs.

Optional JetBrains Context has a consent-guarded, workspace-aware indexing/search
workflow and tereditor agent tools. Fixture, local/remote, UI and lifecycle checks
pass; the final broad gate completed with a terminal-focus retry. Real authenticated
cloud search is unverified. Existing vendor login/cache is shared and disclosed;
fully private vendor state is not established and that scope decision remains pending.

## Backlog

The acceptance work that remains. Module specs keep the contracts and their "Not built" sections; dated task
continuations stay in `context-log.md`.

### Known gaps

Authorities: [UI](src/Tereditor.UI/SPEC.md), [terminal contract](src/Tereditor.UI/Terminal/SPEC.md) and [browser](src/Tereditor.Browser/SPEC.md). Implementation gaps are listed here; acceptance tasks follow separately.

| ID | Status | Gap |
| --- | --- | --- |
| V01 | Missing | Equivalent away notifications outside macOS and explicit system-permission state presentation; current denial can be silent. |
| V02 | Partial | Multiple attention notifications lead to a chooser. Current batch opens the latest terminal rather than letting the user choose among all waiting terminals. |
| V04 | Missing | Busy-close confirmation does not dismiss when another client closes the terminal. [TerminalTabs.cs](src/Tereditor.UI/TerminalTabs.cs) awaits a normal confirmation without catalog-driven invalidation; this remains an explicit [terminal contract](src/Tereditor.UI/Terminal/SPEC.md) implementation gap. |

### Outstanding acceptance work

These tasks consolidate the remaining checks; they do not assert missing product features. Owning specs retain durable verification scope and measurements; [the checks contract](tests/Tereditor.Checks/SPEC.md#recorded-verification-scope) distinguishes source and packaged evidence. No unrun journey is treated as passed.

| ID | Latest result | Remaining work |
| --- | --- | --- |
| V03 | Not run | In a signed macOS app, see a real away notification and click it to focus the correct terminal/window. Delivery/activation wiring exists and recording-notifier checks pass. |
| V05 | Not run | Compare native Settings, menus, Markdown, all plugins and docking at matched content/dimensions; exercise PDF trackpad magnify. Include drop/overflow/spacer/folded-weight geometry. Functional/package checks pass, but there is no matched capture corpus. |
| V06 | Failed/incomplete | Complete the packaged gate after resolving its 2026-10-14 settings-observer timeout, Codex recovery failure and unfinished terminal/Blueprint cases; the later 131-case aggregate source pass is not packaged acceptance. Add direct regressions for reused-tab-ID workspace focus, held Visualize render/remount and same-terminal/two-window rendering, held graph requests after detach/disable, and held PDF read/raster replacement/detach. Release, formatting, package identity/signature and 13 targeted cases pass; the full gate does not. |
| V07 | Not run on required platforms | Exercise Safari/Firefox, non-macOS keyboards/IME, real notification permission and production HTTPS/proxies. Accept the Settings fix in a normal bundle and on physical Retina; its parallel-workspace Chromium test-build acceptance already passes. |
| V09 | 2026-10-14 audit: portable checks only | Execute the Windows package and its ConPTY, Recycle Bin, native editor/Skia, clipboard/IME and client journeys. Portable macOS checks skip Windows runtime branches. |
| V10 | Emulator/fixture coverage only | Exercise physical Android, camera/QR and real two-device Tailscale pairing, approval, reconnect, revoke and remote UX, including packaged deployment. Emulator and fake-identity transport checks pass. |
| V11 | 120 Hz presentation not measured | Measure physical 120 Hz browser presentation. Recorded restty GPU draw cadence is about 60/s and output throughput is 111 FPS; neither proves the presentation target. No new benchmark was run. |
| V12 | Fixture coverage only | Exercise installed Codex/Claude during sustained streaming while typing/resizing/switching controls, interruptions, process replacement, auth/model changes, editor reload/remount/title changes, remote reconnect and physical drops. Complete Claude pane-driven composers/account/usage/MCP health, nested source navigation after unsaved edits, uninstall/marketplace removal and overrides; Blueprint live-model controls, frontmatter discard, outline, alerts and Mermaid; and live Discord IPC/presence. Generic host/UI tests do not establish these journeys. |
| V13 | Linux: cross-built, headless gate and Xvfb launch only | Run the AppImage on real distributions (the 2026-10-19 pass ran its payload, not the image: the runtime could not execute under Rosetta here; `check:packaged -- --linux` unpacks it in CI). Cover a real window manager (title bar, resize, HiDPI), Wayland/XWayland, IME and clipboard, file drops, `notify-send` delivery, `xdg-open`/`gio trash`, a reflink filesystem for workspace clones, glibc 2.31 hosts and Git 2.36+. Mermaid has no Linux library. linux-arm64 is cross-built (2026-10-20) but nothing has run on an arm64 host or runner yet: the first CI/nightly `appimage-arm64` run is its first test. |
