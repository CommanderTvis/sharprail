# Agent reference

Detail moved out of `AGENTS.md`. Read the section you need; do not load it whole.

## Project map

| Path | Responsibility |
| --- | --- |
| `src/SharpRail.Host.Abstractions` | Transport-independent host interfaces and domain records consumed by the UI. |
| `src/SharpRail.Host.Core` | Filesystem, project/spec discovery, Git, worktree and PTY terminal implementations (`Posix.cs` holds the libc interop). No Avalonia, Pi or AI dependency. |
| `src/SharpRail.Host.Core/Plugins` | Plugin runtime (`PluginRuntime` implements `IPluginService`): discovery of `<stateDir>/plugins` and `PluginPaths`, manifest intake, load contexts, registry, reconciler, dispatch, settings namespaces and MCP tools, composed from `PluginHostSeams`. |
| `src/SharpRail.Host.Protocol` | Typed service contracts, protobuf wire DTOs and reconnectable WebSocket RPC peers; generated client services come from `scripts/generate-rpc.py`. |
| `src/SharpRail.Host.Remote` | Kestrel HTTP/WebSocket host, authentication and RPC adapters delegating to Core; `WebSocketRpc.cs` and generated dispatch route typed host calls. |
| `src/SharpRail.Host.Client` | Direct local adapters and WebSocket RPC remote proxies implementing the same host abstractions. Embedded mode uses no sockets or serialization. |
| `src/SharpRail.Plugins.Api` | Plugin contract, types and identity helpers only. `.Api.Host` and `.Api.UI` carry the host and UI contexts and never reference each other. Every public symbol is documented and listed in `PublicAPI.Unshipped.txt`; `PluginApi.Generation` is pinned by the checks. |
| `src/SharpRail.Plugins.UI.Kit` | Controls shared by the app and plugin UI halves: `Ui` brushes, fonts and primitives, `DialogWindow`, Markdown, the Scintilla editor frame and Mermaid diagrams. References no `SharpRail.Host.*` project, `SharpRail.UI` or plugin API. |
| `src/SharpRail.Plugins.Agent.Skills` | Workflow skills both agent plugins ship, adapted from the fork's `packages/pi-thinkrail-workflow/skills`: text only, no project. The Claude Code and Codex host projects each stage them as assets. |
| `src/SharpRail.Plugins.Agent.UI` | Shared account, usage, terminal facts, manual-launch notices and attention wording for Codex and Claude Code. Depends on the UI kit, the plugin API (instruction-section contracts) and Avalonia only. |
| `src/SharpRail.Plugins.*` | Builtin plugins: SpecDialect, Blueprint, ClaudeCode, Discord, BranchGraph, Visualize, Codex, omp (early support), Air Governance, Air Context, Tailscale, EPUB Reader, GitHub (the `gh` repo hosting provider), `GitHub` (pull requests and sign-in through the repo hosting provider hooks H24/W27; git itself stays in the host), `Env` (workspace environment policy and terminal chips) with its dependents (mise, direnv, Python, JavaScript, JVM), and one `Lsp.<Name>` plugin per language server (Plugin API H21). Each owns its contract, Host/UI halves, assets, license and `SPEC.md`. |
| `src/SharpRail.Scintilla` | Self-contained Avalonia editor control: a managed port of Scintilla (`Core`, `View`, `Editing`) drawn with Skia, shaped with HarfBuzz, with a managed UAX #9 on Avalonia's Codepoint data; no native build. Its `README.md` documents API, architecture and limits. References no SharpRail project. |
| `src/SharpRail.UI` | Avalonia application entry point and workbench. |
| `src/SharpRail.UI/Docking` | Persisted frame/workspace layout model, transitions, geometry, gestures, tab chrome and search popover. |
| `src/SharpRail.UI/Panels` | Settings (an overlay card shown in the workbench `OverlayHost`) and shared dialogs, compiled XAML frames and page templates. |
| `src/SharpRail.UI/Rendering` | Theme catalogue, diffs, bindings of the kit's Markdown views to a workspace's host (`MarkdownContexts`). |
| `src/SharpRail.UI/Resources` | Resource-renderer registry, file pane with view toggle, format views (image, SVG, table, JSON, notebook, LFS, byte cards). |
| `src/SharpRail.UI/State` | Profile persistence/migration (`ProfileStore`) and the shared-state subscription (`SharedState`). |
| `src/SharpRail.UI/Plugins` | The app's plugin runtime: contribution registry, builtin UI array, external UI assembly loading, `IPluginUIContext`, roster reconciler. Settings > Plugins is `Panels/PluginsSettings.cs`. |
| `src/SharpRail.UI/Terminal` | Terminal tab body (`TerminalView`), the Ghostty native-control bridge for local and relayed remote sessions, the `--terminal-relay` mode. |
| `src/SharpRail.UI/Notifications` | Away notifications for terminals whose agent needs the user (`AttentionNotifications`, `IDesktopNotifier`, macOS channel over `Native/Notifications.m`). |
| `src/SharpRail.UI/Assets` | Bundled theme manifests; icons and fonts with licenses live in the kit's `Assets`. |
| `src/SharpRail.Android` | Android client (remote only); compiles the UI's sources. Not in `SharpRail.slnx`; `SPEC.md` holds composition rules and limits. |
| `src/SharpRail.Browser` | Browser WASM client compiling the shared UI sources with `BROWSER`; `SPEC.md` and `README.md` describe client and serving requirements. Built separately from `SharpRail.slnx`. |
| `src/Ghostty.Avalonia` | Independent Ghostty controls (AppKit/Metal, Metal textures, libghostty-vt on Skia); native bridges, build script, licenses. Its README documents reuse and limits. |
| `tests/SharpRail.Checks` | Executable checks (host transports, extensibility, layout, UI, Git/worktree). `E2E/` translates upstream scenarios with real headless Avalonia input. |
| `mise.toml`, `mise.android.toml`, `mise.browser.toml` (+ `.lock`) | The single toolchain manifest and every task. `scripts/mise run targets` prints the artifact matrix. |
| `scripts/mise*`, `scripts/mise-pinned*` | Entry points for toolchain commands: a mise already on the machine runs as is (its own config and default data directory); without one, the checksum-pinned mise with a tool store shared by all checkouts that ignores the user's mise config. |
| `scripts/tasks` | What the mise tasks run (`dev` runs a Release build unless `SHARPRAIL_CONFIGURATION=Debug`, `host`, `package-macos`, `dmg`, `package-windows`, `android`, `browser`, `check-packaged`, `check-terminal`, `check-fsevents`, `workload`). Never run directly. |
| `scripts/tool` / `tool.cmd`, `scripts/fetch-git.sh` | Print where mise keeps a pinned tool (installing it first when missing); fetch a source pinned by Git commit. |
| `scripts/build-merman.sh` | Links the xcframework mise installs (`scripts/tool merman`) into `src/SharpRail.UI/obj/native/libSharpRailMermaid.dylib`; the kit's `Visualization/MermaidRenderer.cs` renders SVG through it. Android packages Merman's prebuilt `.so` from `scripts/tool merman-android` under the same name; Browser renders through `@mermanjs/web` WASM. Windows and Linux show source with an unavailability message. |
| `scripts/build-notifications.sh` | Compiles the notifications bridge into `src/SharpRail.UI/obj/native/libSharpRailNotifications.dylib` when `Native/Notifications.m` changes. |
| `src/Ghostty.Avalonia/build-native.sh` | Builds pinned libghostty and libghostty-vt bridges on the target macOS architecture. |

Script invocations, prerequisites and environment variables: `scripts/README.md`.

## Host change walk-through

Each `Host.*` prefix denotes `src/SharpRail.Host.*`.

| Layer | Files |
| --- | --- |
| Public API | `Host.Abstractions/IWorkspaceHost.cs`, `ProjectServices.cs`, `HostState.cs`, `TerminalServices.cs`, `PluginServices.cs`. |
| Implementation | `Host.Core/WorkspaceHost.cs`, `ProjectServices.cs` and its operation files, `HostStateStore.cs`, `HostStateStore.Workspaces.cs`, `GitRepository.cs`, `PtyTerminalService.cs`; plugin spec tools and the Specs panel belong to SpecDialect. |
| Wire contracts | `Host.Protocol/WorkspaceContract.cs`, `ProjectContract.cs`, `StateContract.cs`, `TerminalContract.cs`, `PluginsContract.cs` and operation contracts. |
| Client adapters | `Host.Client/HostAdapters.cs`, `ProjectAdapters.cs`, `StateAdapters.cs`, `TerminalAdapters.cs`, `PluginAdapters.cs` and operation adapters. |
| Server adapters | `Host.Remote/WorkspaceRpc.cs`, `ProjectRpc.cs` (per-call workspace from `ProjectSessions.cs`), `StateRpc.cs`, `TerminalRpc.cs`, `PluginRpc.cs` and operation RPC files; `RemoteServer.cs` configures serving, `HostListener.cs` toggles the app's listener, `Program.cs` starts the standalone host. |

Domain records belong in Abstractions; serialized DTOs in Protocol. Keep both adapters consistent with the public API and verify local/remote parity in the checks.

## Workbench partials (`src/SharpRail.UI`)

| File | Responsibility |
| --- | --- |
| `WorkbenchWindow.axaml` / `.cs` | Window frame, startup, workspace switching, composition. |
| `DocumentNavigation.cs` / `DocumentCache.cs` | Opening/restoring documents, navigation, cached control lifetime. |
| `CodeDocuments.cs` / `Editor/CodeDocumentView.axaml.cs` | Editable code and Markdown source, save/conflict handling, editor wiring. |
| `ResourceDocuments.cs` / `RenderedDiffs.cs` | File and diff bodies from the resource registry, per-tab renderer choice, the window's code and Markdown renderers. |
| `ProjectPanels.cs` | Files, Specs and Projects panels and project actions. |
| `ProjectHome.cs` | Startup routing, Welcome/Project Home, project context actions, Create workspace flow, workspace row actions. |
| `ProjectRail.cs` / `CenterTabsInProjects.cs` | Project/workspace rail reconciliation and its center-tab projection. |
| `FileTree.cs` / `WorkspaceWatcher.cs` | File-tree reconciliation and file-change subscriptions. |
| `GitPanels.cs` / `ChangesTree.cs` / `WorkspaceGit.cs` | Git panel controls, change-tree projection, cancellable workspace-scoped snapshot refreshes. |
| `GitPanelReconciliation.cs` / `ChangeReverts.cs` / `ReviewPull.cs` | Updating Git controls, revert/undo, pull-request actions in Review. |
| `LocationBar.cs` | Header Project/Workspace/Branch segments, switchers, branch card. |
| `WindowNavigation.cs` | Window locations, serialized links, Back/Forward. |
| `ApplicationMenu.cs` / `WindowChrome.cs` | macOS menu bar; title-bar double-click preference, pinch zoom. |
| `AppCommands.cs` / `QuitConfirmation.cs` | App-wide commands; busy-terminal quit confirmation. |
| `AboutInfo.cs` / `Panels/AboutFields.axaml` | About screen content (version stamp, runtime lines) and view; macOS application menu item, Settings › About elsewhere. |
| `PluginSurfaces.cs` / `ToolLifetime.cs` | Plugin contributions in the workbench and control lifetime. |
| `GestureNotification.cs` | Feedback when layout transitions cancel a gesture. |
| `HostSync.cs` | Applying shared-state broadcasts and reconnects. |
| `Workbench.cs` | App-owned composition shared by windows; per-window profile entries. |
| `TerminalTabs.cs` | Confirmation before closing foreground-process terminals; keeping tabs equal to the host's catalog. |
| `Drawers.cs` | Phone-sized workbench: one page at a time from a bottom bar. |
| `BranchList.cs` | Title-bar branch popover: local branches, worktrees, deletion, Fetch. |

## Composition and state details

- Entry: `src/SharpRail.UI/Program.cs` and `App.cs` compose one app-owned `Workbench` (profile, `SharedState`, terminal factory, per-window project-session factory). Every window comes from it (New window, Mod+Shift+N). `WorkbenchWindow` owns one window; `DockSurface` renders docking; `LayoutSession` applies transitions to `LayoutState`.
- Embedded mode uses direct adapters. `Workbench.Listener` optionally serves the same host remotely; starting or stopping it never moves local adapters onto RPC. `LoopbackServer` serves terminal-authenticated MCP and plugin routes, starts lazily, and must be accessed off the UI thread.
- Center document and terminal tabs live in Projects on desktop, Android and browser; left/right tool strips remain. No bottom docking zone or alternate center tab strips. Legacy profile/preset migration drops bottom terminal/tool placements and moves nonterminal bottom documents into a surviving center group. Old tab-mode settings stay readable but no longer choose placement. New workspaces create no terminals implicitly.
- Android tab labels leave touch gestures to the Projects scroller; each tab's Open button selects it and reveals content. Keep those actions separate from scrolling.
- Host state (`IHostStateService`): settings, custom presets, workspace labels, project list/recents, workspace lifecycle. Change it through the host and update the UI on broadcast (`HostSync.cs`); never write a local copy. Local state is `~/.sharprail/state.json` (migrated once from older profiles); a remote host keeps its own in `SHARPRAIL_STATE_DIR`.
- `ProfileStore` owns `~/.sharprail/profile.json`: app preferences (interface size, page zoom, hidden files, terminal renderer), one `Windows` entry per window (frame, default preset, last location, plugin companions), rail expansion, endpoint-qualified plugin UI preferences, per-workspace Git selections. The diff comparison target is host state (`HostState.DiffBase`); the profile copy only serves a workspace the host has none for. Persist target, scope and selected commit; reload commit catalogs from Git. Commit listing is its own host operation: never fetch a full working-tree snapshot to populate it. Tests use isolated profile directories.
- Terminals: Settings selects Metal texture (default, macOS) or Skia (fallback). Local Metal tabs use Ghostty's external-I/O backend with direct calls to the app-owned PTY service (no relay child, socket, serialization or RPC in the data path, including Skia fallback). Remote Metal tabs run the `--terminal-relay` child. Skia tabs attach directly to the host service and draw through `GhosttySkiaView`; read-only Skia views apply the holder's grid before initial replay. Android watched fonts fit 10 to 13 DIP without resizing or owning the terminal. Windows and Android use native libghostty-vt with Skia; browser uses Restty. Windows native builds use `scripts/windows-native.ps1` and the terminal/editor projects' `build-windows.ps1`. The host owns shell lifetime and recorded screens under the profile's `terminals` directory; shells outlive windows while the host runs; closing a terminal tab ends its host session. Headless checks pass a terminal factory running PTY sessions as plain text. Disposing a renderer detaches its client. Clipboard images go under the profile's `clipboard` directory.
- macOS text files open in Scintilla; Markdown source shares the dirty/save/conflict lifecycle.
- Themes: manifests in `src/SharpRail.UI/Assets/Themes`; `Rendering/Themes.cs` owns catalogue and resolution; `Ui.Apply` writes shared brushes then raises `Ui.ThemeChanged` for surfaces that bake colours (Mermaid, Scintilla, Ghostty).
- Dependency flow: Abstractions references only the plugin API root; Core references Abstractions and the plugin API host entry; Client references Abstractions and Protocol; Remote references Core and Protocol. UI references Core, Client, Remote (MCP/plugin loopback, optional WebSocket RPC listener), Scintilla and Ghostty.Avalonia. Checks reference UI and Remote. No UI dependency in host projects. Plugins reference only the API assemblies, the kit and their dependencies' contracts; external plugins install under `<stateDir>/plugins/<id>/` with a `sharprail-plugin.json` manifest.
- Android compiles `src/SharpRail.UI` sources under the same assembly name (no project reference; the UI's host-serving half needs Remote and ASP.NET Core). Desktop-only host-serving code goes under `#if !ANDROID` or in a file the Android project excludes (`Program.cs`, `Terminal/LoopbackTerminals.cs`, `Panels/HostSettings.cs`). Android windows are real `Window`s from the project's `Windowing/` over Avalonia 12.1.4 internals (content-sized window = bottom sheet, other owned window = full-screen page); shared code opens windows as on desktop. Recheck that layer on any Avalonia version change.
- Browser compiles the same sources with `BROWSER`, WebSocket RPC, no embedded host or PTY. Use `#if !ANDROID && !BROWSER` for desktop-only code. `scripts/mise run browser` provisions the threaded WASM workload and native WASM libraries and browser assets.
- Upstreams: both in the checkout `/Users/commandertvis/IdeaProjects/thinkrail`. `main` ports CommanderTvis's fork (remote `origin`, branch `claude-code-integration-plugin-api`); the `upstream` branch ports JetBrains ThinkRail (remote `upstream`, branch `main`) in its own worktree. Do not carry changes between lines by hand. On `main`, port the fork's Plugin API and plugin UI almost verbatim, changing only what the typed host transport and Avalonia/.NET force; provider-specific IDE, hook and MCP protocols stay plugin-owned. Module `SPEC.md`/`*.SPEC.md` and `ARCHITECTURE.md` are adapted from upstream specs; `STATUS.md` (Upstream tracking) records synced commits and the port log; `.claude/skills/sync-upstream-specs/SKILL.md` documents syncs.

## Check modes

Run: `dotnet run --project tests/SharpRail.Checks -c Release -- <mode>`. Modes are defined in `tests/SharpRail.Checks/Program.cs`.

| Mode | Covers |
| --- | --- |
| `--plugins-settings` | Settings › Plugins with a long roster: virtualized, recycled cards; broadcasts update in place; toggles; every builtin plugin draws its icon. |
| `--plugins`, `--codex`, `--codex-terminals`, `--omp`, `--claude-code`, `--claude-chat-ui`, `--claude-chat-host`, `--claude-chat-e2e`, `--specs`, `--branch-graph` | Plugin runtime and each builtin plugin. |
| `--github` (planned with the plugin) | `GitHub` against a fake `gh`: recognition, lookup, open/update, merge, sign-in page, and the off case (no segment, no `gh`, push still works). |
| `--environment` | `Env` and its dependents (mise, direnv, Python, JavaScript, JVM) through the host's pipeline with fake `mise` and `direnv` on PATH: step order by id (unknown ids last), the cascade and the plain environment while `Env` is off, trust inherited only for identical configuration, token never forwarded, slow-tool budget and last-good fallback, and a real shell started with the result (`--case environment` in the gate). |
| `--github` | The GitHub plugin's provider (`SharpRail.Plugins.GitHub.Host`) and pull request lookup and opening through a fake `gh` and bare origins, locally and over WebSocket RPC: recognition of github.com remotes only, the 60 s cache, failures not cached, hung `gh` stopped, and a GitHub remote with no provider behaving as a plain remote (push only, no `gh` spawned). |
| `--epub` | EPUB Reader plugin: generated EPUB 3 (nav) and EPUB 2 (NCX) conversion, disabled by default, enable then open, TOC jumps, in-book and external links, images and cover, scroll and paged modes (page count, next/previous, keys, clean page edges, resize keeps location), Find across chapters, theme contrast, remote-host parity, large-book UI stall budget. `--case epub-reader` runs it in the gate. |
| `--lsp-narrow` | Code Intelligence rows and pane headers at 160–480px: one-line grid header aligned across rows, trimmed names, reachable actions, titles clear of buttons. |
| `--lsp-ui` | Language editor overlay and the LSP pane, including a dead server row with Details and Copy details, the lifetime note and the "started by / Agent calls" line. |
| `--lsp-dependencies` | Language server system dependencies: per-tool probe with a fake PATH, "Needs go" on the row before any start, and the host-wide tool path (persisted, broadcast, probed, used by launch, cleared back to PATH). |
| `--lsp-catalog` | The language server catalog is one snapshot shared by every caller, rebuilt only when the plugin roster changes (rebuild counter), indexed detection matches the roster-order scan, and catalog queries stay within an allocation budget per query. `--case language-catalog` runs it in the gate. |
| `--lsp-on-demand` | The lsp tools start the owning servers and never hang (deadline), path-free symbol search, usage counts and hints, workspace-lifetime servers (project `autoStart` in every workspace, removal stops only its own, `workspace_create` `languageServers`). It also covers project-graph freshness (references and file diagnostics wait while a restore or reload runs and answer `initializing`, outlines answer at once, a new file is found and diagnosed without a restart through watched-file notifications, a type with its partial parts and constructor resolves to the type, `symbolBody` by simple and `Type/Member` names). `--case language-on-demand` runs it in the gate; `--case language-exit` covers exit records including "workspace removed". |
| (planned) `--lsp-edit-apply`, `--lsp-refactor` | WorkspaceEdit applier atomicity and safety; refactoring tools and editor entries. Listed in `src/SharpRail.Host.Core/Language/Refactoring.SPEC.md`. |
| `--lsp-limits` | Language-server limits with fake servers: idle stop and transparent restart, per-kind cap evicting the least recently used idle server (never one with an open editor), queueing with "waiting for a free language server slot" when all are busy, the memory budget, memory pressure, Settings stop, project close, orphan sweep at start, death with the host (watchdog), the `language-limits` setting round trip and the Settings › Code Intelligence running list. `--case language-limits` and `--case language-limits-ui` run it in the gate. |
| `--vertical-tabs` | Projects-only tab projection and interactions. |
| `--rail` | Retained Projects controls and geometry. |
| `--state` | Host state and profile/preset migration. |
| `--rename-follows` | Open editor and Markdown preview tabs follow a rename from the Files tree and an outside `mv`: unsaved edits, selection and focus kept, title and highlighting path updated, saves go to the new path; deleting still marks the tab. |
| `--file-drop` | Workspace file drag representations, transfers, attachment gestures. |
| `--about` | About screen: version fallback, runtime and copy text, links, application menu (About, Settings), Escape. |
| `--connection-errors` | Connection failure details and retry. |
| `--windows` | Windows host, desktop and native renderer checks (Windows only). |
| `--terminals` | Host terminals, terminal translations, Skia renderer, secure keyboard entry. |
| `--secure-input` | Secure keyboard entry against a counting fake of the Carbon call: coordinator balance, focused password prompts in two terminals, focus/activation/setting/exit/close, both toggles, a remote host's flag. |
| `--ghostty-skia` | Skia input, pixel, PTY and renderer Settings. |
| `--native-texture` | GPU texture composition, input and local/remote renderer switching in a real macOS window. |
| `--native-frame-pump` | Benchmark: Metal frame-notification cost with two streaming terminals, one hidden, in-process and through an embedded host (`SHARPRAIL_FRAME_PUMP_SECONDS`, default 20). |
| `--sync` | Multi-window and multi-client translations. |
| `--shared-terminals` | Two-client terminal sharing, watching, take-over. |
| `--notifications` | Away notifications with a recording notifier; none posts a real one. |
| `--registry` | Workspace registry host parity and external-workspace rail. |
| `--scintilla-core` | Managed Scintilla core: ports of upstream test/unit suites plus bidi, shaping, view and editor-model checks; no Avalonia setup. |
| `--workspace-delete` | Force-deleting another dirty workspace with a large build tree: UI heartbeat, the open workspace stays mounted, a switch during the deletion is prompt. |
| `--syntax` | TextMate coverage, incremental edits, Unicode, theme changes, attachment/disposal, diff syntax styling. |
| `--documents` | Resource renderers and file/diff body translations. |
| `--editor`, `--markdown`, `--markdown-find`, `--markdown-images`, `--markdown-chunks` | Editor integration, source editing, Markdown rendering and search; image loading; chunked large Markdown blocks. |
| `--agent-marks`, `--agent-launches` | Agent decorations through workspace switches and the terminal tab menu's Copy session ID; host and UI launch behavior. |
| `--terminal-replay` | Filtering of terminal queries in recorded screens. |
| `--workspace-tools` | Host MCP workspace tools and UI integration. |
| `--workspace-emigrate` | `workspace_emigrate` with the fake claude: refusals, a denied prompt, and an approved move resuming the preassigned session in the target (POSIX only). |
| `--workspace-create-stall` | Agent `workspace_create` with a cloned ignored tree while the window runs: longest dispatcher wait under 50 ms (`SHARPRAIL_CREATE_STALL_BUDGET_MS`; `_OTHERS`, `_LSP` add workspaces and language servers). |
| `--changes-branch-switch` | Changes target switch from no changes to 4000 files: longest dispatcher wait under 250 ms (`SHARPRAIL_BRANCH_SWITCH_STALL_BUDGET_MS`); a later pick supersedes an earlier one. |
| `--serving`, `--project-close` | Embedded-host serving; teardown. |
| `--pane-retention`, `--startup`, `--live-diffs` | Retained controls; progressive startup; refreshing open diffs. |
| `--pane-retarget` | The omp, Claude Code, Codex and Language panes stay one instance over 21 switches across worktrees and projects, show only the current workspace's content (equal to their first visit on return) and add no handlers to the shared events. |
| `--workspace-switch` | Workspace-switch profile on a realistic project: click to mounted, first frame and usable content, cold and switching back; fails if a switch issues more than one host call before mounting. |
| `--files-modes` | Files All \| Changed: Git marks, ignored dimming, remembered expanded folders across switches and reloads (local Git fixture). |
| `--dock-input` | Dock pointer and keyboard input, tab menus including file-path copies. |
| `--change-actions`, `--review` | Toast and diff revert/undo; Review panel. |
| `--workspace-views` | Layout keeps a workspace view only for workspaces the host lists (create/remove loop), never dropping the active one. |
| `--drawers` | Phone pages: the compact page bar, Settings as a page, and the persistent page hosts and their slide. |
| `--shell` | Window shell: location bar, application menu and the macOS workbench menu bar, chrome, region errors, arrangement isolation, locations and links, workspace dialog project picker, commit menus, inert links. |
| `--ui` | Headless UI checks and upstream translations without host suites. |
| `--design` | Design-system guards and theme translations; `--design --write` regenerates `src/SharpRail.Plugins.UI.Kit/Generated` from `src/SharpRail.UI/Rendering/Design`. Edit the authored sources, not generated C#. |
| `--zig-deps` | `scripts/zig-deps.lock` matches the Ghostty pins in `mise.toml` and every native build path restores the mirror. |
| `--lock-script` | `scripts/lock.sh` (the native build scripts' directory lock) takes over a lock whose owner is gone, waits for a live holder and releases on exit. |
| `--conformance` | Dependency-boundary checks; public API analyzers validate against `PublicAPI.*.txt` baselines. |
| `--runner` | The runner itself. |
| `--headscale` | Opt-in and slow, never in the argument-free suite: a real host and client on two tailnet nodes of a throwaway Headscale. Skips with a message unless `sh scripts/build-headscale-test.sh` has run (`SHARPRAIL_REQUIRE_HEADSCALE=1` fails instead). |
| `--android` | Android host address parsing and remembered endpoint (the client needs a device: `scripts/mise run android run`). |
| `--android-package [apk]` | Not in the argument-free suite: the built APK (default `artifacts/android/SharpRail.apk`, from `scripts/mise run package:android`) carries `libSharpRailMermaid.so` for arm64-v8a and x86_64, for the right machine, exporting Merman's C ABI with 16 KB aligned segments. |

Gate options (argument-free gate only; not focused modes): `--lanes N|auto` splits across processes, `--last-failed` reruns unfinished work (serial), `--list-cases`, `--case A,B` (for example `--case language-exit` for server exit records, `--case language-services` for the language host). `tests/SharpRail.Checks/Runner.cs` owns these, the idle-sleep assertion and interrupt cleanup. A suite joins the gate through `Gate.Case` in `Program.cs` or `UiChecks.cs`. Set `SHARPRAIL_TEST_GIT_SOURCE` to an existing upstream clone to include Git fixtures.

Under GitHub Actions (`GITHUB_ACTIONS=true`) the runner (`tests/SharpRail.Checks/CiReport.cs`) wraps each gate case in `::group::`/`::endgroup::` (not in lanes), and a failure emits an `::error file=,line=,title=<case>::<message>` annotation (first stack frame under `tests/`, assertion helpers skipped) and appends a failed-case table, pass/skip counts and the last 30 output lines to `$GITHUB_STEP_SUMMARY`. Lane output keeps `::` lines unprefixed so annotations survive. With `SHARPRAIL_CHECKS_KEEP_GOING=1` (set in `ci.yml` and `nightly.yml`; local default off; ignored by focused modes) the gate runs every selected case instead of stopping at the first failure: a throwing case is recorded (name, message, file:line, duration, last 30 lines of its own output), the working directory and environment are restored, and the run ends with a failure table and a slowest-cases table on the console and in the step summary, one `::error` per failed case, and exit 1. Cases are independent (shared setup sits outside them), so none is skipped for an earlier failure. `--last-failed` then reruns exactly the failed cases (`!ordinal shard name` records); lanes and shards each keep going and merge. Local output is unchanged without it. To see it locally: `GITHUB_ACTIONS=true GITHUB_STEP_SUMMARY=<file> SHARPRAIL_CHECKS_FIXTURE_CASES=3 SHARPRAIL_CHECKS_FIXTURE_FAIL=c1 dotnet run --project tests/SharpRail.Checks -c Release`.

Other check facts:

- `Program.cs` is the runner, not an xUnit project. `ProjectChecks.cs` covers project/Git host parity; `LayoutChecks.cs` layout transitions; `UiChecks.cs` the headless UI checks. Shared input/fixture helpers: `E2E/E2eWorkspace.cs` (`NewWindow()` opens a second window of the same app); `E2E/CutProxy.cs` drops and restores one remote client's connection. Keep translated upstream coverage in `STATUS.md` separate from additional regression checks.
- Browser acceptance: `scripts/mise run browser check` (Playwright tests in `tests/SharpRail.Browser.Checks`). `node tests/SharpRail.Browser.Checks/restty-lifecycle.mjs --require-hardware --require-memory-return` checks repeated Restty teardown and Chromium-reported GPU allocations against a warmed baseline; it needs built browser vendor assets and is not a physical GPU residency measurement.
- Published checks: run `artifacts/checks/SharpRail.Checks` with `SHARPRAIL_REQUIRE_R2R=1` to require ReadyToRun and open-world checks.
- Fixture location and cleanup: every check creates fixtures, screenshots and scratch files through `CheckRoot` (`tests/SharpRail.Checks/CheckRoot.cs`), one `sharprail-checks-<pid>-<id>` directory per process under the OS temp directory (`SHARPRAIL_CHECKS_ROOT` names another parent), never in the checkout. It is deleted when the process exits, including after failure and Ctrl-C; `SHARPRAIL_CHECKS_KEEP=1` keeps it. A failed gate keeps its root (path printed, listed in `.bench/checks-failed-fixtures.txt`) until the next successful run in the checkout. Each run first deletes roots of dead processes older than an hour (kill -9, power loss) and old `.bench/check-fixture-*` folders and reports the bytes freed. `-- --check-root` proves this. Keep `.bench/checks-last-run.txt` while `--last-failed` needs it.

## Outputs

`artifacts/ui`, `artifacts/host`, `artifacts/checks`: published executables. `artifacts/SharpRail.app`: macOS bundle. Windows: `artifacts/windows/{client,host,checks}` and `artifacts/SharpRail-win-x64.zip` (ARM64: `artifacts/windows-arm64`, `artifacts/SharpRail-win-arm64.zip`). Browser: `artifacts/browser`. Android: `artifacts/android/SharpRail.apk`. Keep one latest canonical app package. `bin`, `obj` are local build output; build caches live in the machine-wide `SHARPRAIL_CACHE_DIR` (`scripts/README.md`), never in a repo-local `.tools`. `licenses/` and `THIRD-PARTY-NOTICES.md` document redistributed dependencies and assets.

The nightly workflow builds the macOS DMG, Android APK and Windows x64/ARM64 ZIPs at 03:00 UTC or on manual dispatch; each Windows job (`windows-2025`, `windows-11-arm`) requires the packaged ReadyToRun `--windows` gate before the four assets publish together. Verify the actual workflow run and release assets before reporting a nightly deployed or runtime-verified.

Every workflow step that builds, packages or checks runs through `scripts/ci-run.sh` (`scripts/ci-run.ps1` on Windows): it keeps the output in `$RUNNER_TEMP/sharprail-ci/`, and on failure `scripts/ci-diagnose.sh` annotates the first error lines (MSBuild `file(line,col): error` lines become file annotations; step titles must not contain `:` or `,`) and writes them with the log tail to the step summary. Each job ends with `if: failure()` `.github/actions/diagnostics`, which uploads `diagnostics-<job>` (step logs, `.bench/checks-last-run.txt`, any `*.binlog`) for 7 days. Add new steps through the wrapper.

## UI convention

New or touched static panels, cards and dialogs go in `.axaml` with `x:DataType` so `AvaloniaUseCompiledBindingsByDefault` checks their bindings at build time (`Rendering/ToastCard.axaml` is the model: compiled XAML plus a small code-behind for wiring). Imperative C# stays for dynamic docking and host/interaction wiring. Do not convert untouched panels wholesale.
