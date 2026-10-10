# SharpRail instructions

## Working in the checkout

Reuse the workspace prepared for the task. When isolation is needed, use the
available SharpRail `workspace_create` tool and work in its returned path; it does
not switch the terminal. Use `workspace_delete` for finished workspaces you
created, after preserving their work and leaving the checkout. Keep Default,
workspaces you did not create and workspaces with active agents.

Inspect Git status before editing and leave other tasks' changes alone. Do not
change Git configuration to work around a concurrent operation. Never push unless
the user asks; preserve the configured commit-signing presence check.

## Project structure

`SharpRail.slnx` contains the .NET 10 C# solution. `Directory.Build.props` enables
nullable checking, warnings as errors, optimizations and tiered PGO while keeping
NativeAOT and trimming disabled. `Directory.Packages.props` pins shared package
versions; `global.json` selects the SDK. Use `.tools/dotnet/dotnet` for this checkout
and `scripts/bootstrap.sh` if it is missing. Build with
`.tools/dotnet/dotnet build SharpRail.slnx -c Release`; this is also the compiler
and nullable/warnings-as-errors gate.

| Path | Responsibility |
| --- | --- |
| `src/SharpRail.Host.Abstractions` | Transport-independent host interfaces and domain records consumed by the UI. |
| `src/SharpRail.Host.Core` | Filesystem, project/spec discovery, Git, worktree and PTY terminal implementations (`Posix.cs` holds the libc interop). No Avalonia, Pi or AI dependency. |
| `src/SharpRail.Host.Core/Plugins` | The host's plugin runtime (`PluginRuntime` implements `IPluginService`): discovery of `<stateDir>/plugins` and `PluginPaths`, manifest intake, load contexts, registry, reconciler, dispatch, settings namespaces and MCP tools, composed from `PluginHostSeams`. |
| `src/SharpRail.Host.Protocol` | Code-first protobuf-net.Grpc service contracts and wire DTOs. |
| `src/SharpRail.Host.Remote` | Kestrel HTTP/2 host, authentication and RPC adapters delegating to Core. |
| `src/SharpRail.Host.Client` | Direct local adapters and gRPC remote proxies implementing the same host abstractions. Embedded mode uses no sockets or serialization. |
| `src/SharpRail.Plugins.Api` | The plugin contract, types and identity helpers only: manifest, contract vocabulary, roster, `PluginJson`. `SharpRail.Plugins.Api.Host` and `SharpRail.Plugins.Api.UI` carry the host and UI contexts and never reference each other. Every public symbol is documented and listed in `PublicAPI.Unshipped.txt`; `PluginApi.Generation` is pinned by the checks. |
| `src/SharpRail.Plugins.UI.Kit` | Controls shared by the app and plugin UI halves: `Ui` brushes, fonts and primitives, `DialogWindow`, Markdown, the Scintilla editor frame and Mermaid diagrams. References no `SharpRail.Host.*` project, `SharpRail.UI` or plugin API. |
| `src/SharpRail.Plugins.Agent.Skills` | The workflow skills both agent plugins ship, adapted from the fork's `packages/pi-thinkrail-workflow/skills`: one folder per skill, text only, no project. The Claude Code and Codex host projects each stage these files as their own assets. |
| `src/SharpRail.Plugins.Agent.UI` | Shared account, usage, terminal facts, manual-launch notices and attention wording for Codex and Claude Code. Depends on the UI kit and Avalonia, not a host, provider or plugin API. |
| `src/SharpRail.Plugins.*` builtin folders | SpecDialect, Blueprint, ClaudeCode, Discord, PdfPreview, BranchGraph, Visualize, FileIcons and Codex. Each owns its contract, applicable Host/UI halves, assets, license and `SPEC.md`. |
| `src/SharpRail.Scintilla` | Self-contained Avalonia editor control: Scintilla with a Skia surface, HarfBuzz shaping and SheenBidi layout. Its `README.md` documents the API, native build and limits. It references no SharpRail project. |
| `src/SharpRail.UI` | Avalonia application entry point and workbench. `WorkbenchWindow` partial files coordinate navigation, projects and Git panels. |
| `src/SharpRail.UI/Docking` | Persisted frame/workspace layout model, transitions, geometry, pointer/keyboard gestures, tab chrome and search popover. |
| `src/SharpRail.UI/Panels` | Settings and shared dialogs, including compiled XAML frames and page templates. |
| `src/SharpRail.UI/Rendering` | The theme catalogue, diffs and the bindings of the kit's Markdown views to a workspace's host (`MarkdownContexts`). |
| `src/SharpRail.UI/Resources` | Resource-renderer registry, the file pane with its view toggle, and the format views (image, SVG, table, JSON, notebook, LFS and byte cards). |
| `src/SharpRail.UI/State` | Profile persistence and migration (`ProfileStore`: app preferences and one entry per window) and the app's shared-state subscription (`SharedState`). Default user state belongs in `~/.sharprail`, not project directories. |
| `src/SharpRail.UI/Plugins` | The app's plugin runtime: the contribution registry the workbench reads, the builtin UI array, external UI assembly loading through the host, `IPluginUIContext` and the roster reconciler. Settings › Plugins is `Panels/PluginsSettings.cs`. |
| `src/SharpRail.UI/Terminal` | Terminal tab body (`TerminalView`: start failure/retry, exit notice), the Ghostty native-control bridge for local and relayed remote sessions, and the `--terminal-relay` mode. |
| `src/SharpRail.UI/Notifications` | Away notifications for terminals whose agent needs the user: the collection window and gates (`AttentionNotifications`), the injected `IDesktopNotifier` and the macOS channel over `Native/Notifications.m`. |
| `src/SharpRail.UI/Assets` | The bundled theme manifests; the icons and fonts, with their licenses, live in the kit's `Assets`. |
| `src/SharpRail.Android` | The Android client: the UI's sources compiled for Android as a client of a remote host, with the windowing layer (`Windowing/`), the connect screen and the remembered endpoint. Not in `SharpRail.slnx`; its `SPEC.md` holds the composition rules and limits. |
| `src/Ghostty.Avalonia` | Independent Ghostty controls: hosted AppKit/Metal, Metal textures composed by Avalonia, and libghostty-vt drawn by Skia; native bridges, build script and licenses. Its README documents reuse and limits. |
| `tests/SharpRail.Checks` | Executable checks for host transports, runtime extensibility, layout, UI and Git/worktree integration. `E2E/` translates upstream scenarios using real headless Avalonia input. |
| `scripts/dev.sh` | Builds and runs the app from source in one step (`SHARPRAIL_PROFILE` keeps a separate profile). |
| `scripts/bootstrap.sh` | Installs the checkout's local .NET SDK. |
| `src/Ghostty.Avalonia/build-native.sh` | Builds pinned libghostty and libghostty-vt bridges on the target macOS architecture, using checkout-local tools and caches. |
| `scripts/build-merman.sh` | Downloads Merman's pinned, checksummed macOS xcframework and links its C ABI into `.tools/merman/libSharpRailMermaid.dylib`. The kit's `Visualization/MermaidRenderer.cs` renders SVG through it off the UI thread; Svg.Skia displays it. Other platforms show the source with an unavailability message. |
| `scripts/build-notifications.sh` | Compiles the User Notifications bridge into `.tools/notifications/libSharpRailNotifications.dylib`; the UI build runs it and copies the library beside the app. |
| `scripts/check-terminal.sh` | Runs the native shell/Metal probe; the checks executable's `--native-terminal` mode exercises Avalonia integration. |
| `scripts/check-packaged.sh` | Runs the check gate from a staged copy of `artifacts/SharpRail.app`, so the files under test are the packaged ones. |
| `scripts/publish.sh` | Publishes non-composite R2R UI, remote host and checks; refreshes and signs the canonical `artifacts/SharpRail.app`. Check for a live app process before replacing it. |
| `scripts/android.sh` | Builds the Android client (`build`), installs and starts it on the connected device or emulator (`run`), or writes the Release package to `artifacts/android/SharpRail.apk` (`apk`). Needs `ANDROID_HOME` and a JDK 17 or 21 in `JAVA_HOME`; installs the `android` workload into `.tools/dotnet`. |
| `scripts/android-ndk.sh` | Installs the pinned Android NDK under `.tools/android-ndk` and prints its path; `build-android.sh` in `src/Ghostty.Avalonia` and `src/SharpRail.Scintilla` build the Android terminal and editor libraries with it. |
| `.bench` | Ignored disposable fixtures, verification logs and own-window captures. Its name does not authorize benchmarks. |

## Scratch files

Keep `.bench` disposable and small. Put temporary files in one task-specific
subdirectory and remove it when the task finishes, including after failed checks.
Do not accumulate historical logs, captures, copied source trees, build/publish
outputs, dependency caches or archives there. Temporary packaged-check staging is
allowed only for the duration of its run; `check-packaged.sh` cleans it on exit.
Reuse or replace current evidence instead of creating numbered copies. Record
verification outcomes in the existing
status documents; retain scratch evidence only when the user explicitly asks,
and remove it once it is no longer needed. Preserve files used by active tasks
and `.bench/checks-last-run.txt` while it is needed for `--last-failed`. Check
fixture directories also need cleanup after their processes exit; the runner does
not remove every fixture automatically. Never delete another active task's files.

## Implementation boundaries

Static UI layouts/styles/templates belong in compiled `.axaml`; dynamic docking
and host/interaction wiring belong in C#. Keep the host independent of the UI and
make remoteness an adapter choice rather than a mandatory local daemon.

For host changes, follow the operation through these files:

| Layer | Files |
| --- | --- |
| Public API | `Host.Abstractions/IWorkspaceHost.cs`, `ProjectServices.cs`, `HostState.cs`, `TerminalServices.cs` and `PluginServices.cs`. |
| Implementation | `Host.Core/WorkspaceHost.cs`, `ProjectServices.cs` and its operation-specific files, `HostStateStore.cs`, `HostStateStore.Workspaces.cs`, `GitRepository.cs` and `PtyTerminalService.cs`; plugin spec tools and the Specs panel belong to SpecDialect. |
| Wire contracts | `Host.Protocol/WorkspaceContract.cs`, `ProjectContract.cs`, `StateContract.cs`, `TerminalContract.cs`, `PluginsContract.cs` and the operation-specific contracts. |
| Client adapters | `Host.Client/HostAdapters.cs`, `ProjectAdapters.cs`, `StateAdapters.cs`, `TerminalAdapters.cs`, `PluginAdapters.cs` and the operation-specific adapters. |
| Server adapters | `Host.Remote/WorkspaceRpc.cs`, `ProjectRpc.cs` (per-call workspace from `ProjectSessions.cs`), `StateRpc.cs`, `TerminalRpc.cs`, `PluginRpc.cs` and the operation-specific RPC files; `RemoteServer.cs` configures serving, `HostListener.cs` toggles the app's listener and `Program.cs` starts the standalone host. |

Each `Host.*` prefix in this table denotes its `src/SharpRail.Host.*` project.
Domain records belong in
Abstractions; serialized DTOs belong in Protocol. Keep both adapters consistent
with the public API and verify local/remote parity in the checks.

The bundled theme manifests live in `src/SharpRail.UI/Assets/Themes`; `Rendering/Themes.cs`
owns the catalogue and resolution, and `Ui.Apply` writes the shared brushes then raises
`Ui.ThemeChanged` for surfaces that bake colours (Mermaid, Scintilla, Ghostty).

The application starts in `src/SharpRail.UI/Program.cs` and `App.cs`, which compose
one app-owned `Workbench`: the profile, the host's shared-state subscription
(`SharedState`), the terminal factory and a factory for per-window project sessions.
Every window of the app comes from it (New window, Mod+Shift+N). Embedded operation
uses direct adapters. `Workbench.Listener` optionally serves the same host to
remote clients; starting or stopping it never moves local adapters onto RPC.
`LoopbackServer` supplies terminal-authenticated MCP routes and plugin routes,
starts lazily on first use and must be accessed off the UI thread.
`WorkbenchWindow` owns one window's workbench; `DockSurface` renders and handles docking,
while `LayoutSession` applies transitions to `LayoutState`. `ProfileStore` owns
on-disk app state. Keep these responsibilities separate when adding interactions.
Settings, custom presets, workspace labels, the project list/recents and workspace
lifecycle are host state (`IHostStateService`): change them through the host and
update UI when the broadcast arrives (`HostSync.cs`), never by writing a local copy.
Local host state is `~/.sharprail/state.json`, migrated once from older profiles; a
remote host keeps its own in `SHARPRAIL_STATE_DIR`. The default profile file is
`~/.sharprail/profile.json`; it contains app preferences (interface size, page zoom,
hidden files and terminal renderer), one `Windows` entry per window (frame, default
preset, last location and plugin companions), rail expansion, endpoint-qualified
plugin UI preferences and per-workspace Git selections. The comparison target itself is host state
(`HostState.DiffBase`); the profile's copy only serves a workspace the host has none for. Persist target,
scope and selected commit; reload commit catalogs from Git rather than saving
derived snapshots. Tests use isolated profile directories.
Commit listing is a separate host operation; do not fetch a full working-tree
snapshot merely to populate or restore the commit catalog.
Settings selects Metal texture (default) or Skia (fallback) terminal rendering. Local Metal tabs
use Ghostty's external-I/O backend with direct calls to the app-owned PTY service: no relay child,
socket, serialization or RPC in the terminal data path, including fallback to Skia.
The separate loopback serves MCP and plugin routes. Remote Metal tabs run the
`--terminal-relay` child attached to their remote host. Shells outlive their windows while the host runs.
Skia tabs attach directly to the same host service and draw through `GhosttySkiaView`.
Both native builds currently require macOS; other platforms show an availability message. `WorkbenchWindow`
takes its terminal factory from the composition root; headless checks pass one that
runs host PTY sessions as plain text. Disposing a renderer detaches its client;
closing a terminal tab ends its host session. The host owns shell lifetime and
recorded screens under the profile's `terminals` directory. Clipboard images are
stored under the active profile's `clipboard` directory. macOS text files open in
Scintilla; Markdown source uses the same dirty/save/conflict lifecycle. Codex and
Claude Code terminal integrations are in scope; pi and an embedded AI chat UI are
excluded.

The workbench is split into partial files rather than separate window classes:

| File in `src/SharpRail.UI` | Responsibility |
| --- | --- |
| `WorkbenchWindow.axaml` / `WorkbenchWindow.cs` | Static window frame, startup, workspace switching and workbench composition. |
| `DocumentNavigation.cs` / `DocumentCache.cs` | Opening/restoring documents, navigation and cached document-control lifetime. |
| `CodeDocuments.cs` / `Editor/CodeDocumentView.axaml.cs` | Editable code and Markdown source documents, save/conflict handling and editor wiring. |
| `ResourceDocuments.cs` / `RenderedDiffs.cs` | File and diff bodies from the resource registry, per-tab renderer choice and view state, and the window's code and Markdown renderers. |
| `ProjectPanels.cs` | Files, Specs and Projects panel construction and project actions. |
| `ProjectHome.cs` | Startup routing, Welcome/Project Home, project context actions, the Create workspace flow and workspace row actions. |
| `ProjectRail.cs` / `CenterTabsInProjects.cs` | Project/workspace rail reconciliation and its center-tab projection. |
| `FileTree.cs` / `WorkspaceWatcher.cs` | File-tree reconciliation and workspace file-change subscriptions. |
| `GitPanels.cs` / `ChangesTree.cs` / `WorkspaceGit.cs` | Git panel controls, compact change-tree projection and cancellable, workspace-scoped snapshot refreshes. |
| `GitPanelReconciliation.cs` / `ChangeReverts.cs` / `ReviewPull.cs` | Updating Git controls, revert/undo actions and pull-request actions in Review. |
| `LocationBar.cs` | The header's captioned Project, Workspace and Branch segments, their switchers and the branch card. |
| `WindowNavigation.cs` | Window locations, their serialized links and the Back/Forward list. |
| `ApplicationMenu.cs` / `WindowChrome.cs` | The macOS menu bar (Edit, Window) over the app's commands; the title-bar double-click preference and pinch zoom. |
| `AppCommands.cs` / `QuitConfirmation.cs` | App-wide commands and confirmation for busy terminals before quitting. |
| `PluginSurfaces.cs` / `ToolLifetime.cs` | Plugin contributions in the workbench and their control lifetime. |
| `GestureNotification.cs` | Feedback when layout transitions cancel an active gesture. |
| `HostSync.cs` | Applying shared-state broadcasts and reconnects to the window. |
| `Workbench.cs` | App-owned composition shared by windows and the per-window profile entries. |
| `TerminalTabs.cs` | Confirmation before closing terminals that run a foreground process, and keeping the window's terminal tabs those of the host's catalog. |
| `Drawers.cs` | The phone-sized workbench: one page at a time from a bar at the bottom (Projects, the current tab, Tools, Settings). |
| `BranchList.cs` | The title bar branch's popover: local branches with their worktrees, deletion and Fetch. |

Host dependencies flow toward abstractions: Abstractions references only the plugin API's root;
Core references Abstractions and the plugin API's host entry; Client
references Abstractions and Protocol; Remote references Core and Protocol. The UI
references Core and Client to compose either direct local calls or remote proxies,
the Scintilla editor control, which it supplies with theme colours and fonts, and
Ghostty.Avalonia. The UI also references Remote for its MCP/plugin loopback and
optional gRPC listener; embedded workspace operations still use direct adapters.
Checks reference the UI and Remote to exercise both paths. Do not introduce a UI
dependency into the host projects. Plugins (`src/SharpRail.Plugins.Api/SPEC.md`) reference only the API assemblies, the
kit and their dependencies' contracts; external plugins install under `<stateDir>/plugins/<id>/` with a
`sharprail-plugin.json` manifest.

The Android client (`src/SharpRail.Android`) is a client only: no embedded host, PTY or relay, just the
remote adapters against a host's gRPC endpoint. It compiles the sources of `src/SharpRail.UI` under the same
assembly name instead of referencing the project, because the UI's host-serving half needs Remote and
ASP.NET Core. Every shared UI source must therefore stay compilable for Android: desktop-only host-serving
code goes under `#if !ANDROID` or into a file the Android project excludes (`Program.cs`,
`Terminal/LoopbackTerminals.cs`, `Panels/HostSettings.cs`). Windows are real `Window`s there too, supplied
by the project's `Windowing/` over Avalonia 12.1.3 internals: a window sized to its content shows as a
bottom sheet and any other owned window as a full-screen page. Shared code keeps opening windows as on
desktop; only their Android look (the kit's sheet card, Settings without its card frame) is special-cased.
Recheck that layer on any Avalonia version change.

`SPEC.md` defines the product contract; `COMPLETION.md` records unfinished gates;
`E2E.md` inventories upstream translations; `VALIDATION.md` records verified
evidence. Read the relevant module specs and the latest relevant entries in
`context-log.md` for continuation state; read `gotchas.md` if it is present.
SharpRail tracks two upstreams, one per branch, both in the checkout at
`/Users/commandertvis/IdeaProjects/thinkrail`. `main` ports CommanderTvis's fork
(remote `origin`, branch `claude-code-integration-plugin-api`): JetBrains plus the Plugin
API, its builtin plugins and the fork's general improvements. The `upstream` branch ports
JetBrains ThinkRail (remote `upstream`, branch `main`) and is maintained separately in its
own worktree; do not carry changes between the two lines by hand. On `main`, port the
fork's Plugin API and plugin UI almost verbatim, changing only what the transport (gRPC
instead of WebSocket) and the framework (Avalonia/.NET instead of React/Bun) force;
provider-specific IDE, hook and MCP protocols remain plugin-owned. pi and embedded
AI chat stay out of scope. Commits follow the fork's shape; see "Commit organization".
Module `SPEC.md`/`*.SPEC.md` files and `ARCHITECTURE.md` are adapted from upstream specs;
`UPSTREAM.md` records each branch's synced commit, the spec mapping and the fork port log,
and `.claude/skills/sync-upstream-specs/SKILL.md` documents later syncs for the current
branch. Use only skills and tools available in the current session. Update the
owning spec when changing its module.

## Commit organization

`main` is a chain on top of the shared history, in the fork's order, so each part can be
reviewed, ported or extracted on its own:

1. General improvements: changes that do not need the plugin API.
2. The Plugin API: the contract assemblies, the host plugin runtime, the UI registry and the kit,
   with no plugins. Where general and API changes reference each other too closely to build
   apart, they share one commit and its message says so.
3. One commit per builtin plugin, in the fork's order: spec dialect, Blueprint, Claude Code,
   Discord, PDF Preview, Branch Graph, Visualize, File Icons, Codex. A plugin's commit carries
   its project folder, its checks, its license and package entries, and its own lines in shared
   registration files (`SharpRail.slnx`, project references, both `BuiltinPlugins.cs`, the
   check runner).

Status records (`COMPLETION.md`, `VALIDATION.md`, `E2E.md`, `UPSTREAM.md`, `context-log.md`,
notices) sit in one commit at the tip.

- Amend, don't append. A fix to something that already exists goes into the commit that owns
  it: `git commit --fixup=<sha>`, then
  `GIT_SEQUENCE_EDITOR=true git rebase -i --autosquash <base>`. A change touching two parts is
  two fixups. Rewrite only commits not yet on `origin/main` unless the user asks for a
  force-push.
- New commits only for new things: a new general improvement (inserted into part 1, never
  appended at the tip), a new plugin (part 3), or a core API capability the API commit would be
  incomplete without. A capability a plugin introduced stays in that plugin's commit until it
  is generalised. A feature and its follow-up fixes end as one commit.
- Every commit is green on its own: the solution builds in Release with warnings as errors, and
  tests land with the code they test, never ahead of it. Build each step in a separate
  worktree so the working tree is never disturbed, and compare the final tree with the
  pre-rebase tree before replacing the branch.

## Verification budget

The full suite is expensive; duration and case counts vary with the branch and
lane count. It executes from build output, which must stay unchanged while it
runs. Spend it deliberately:

- While iterating, run only the focused mode for what changed (`--plugins`, `--codex`, `--specs`,
  `--claude-code`, `--branch-graph`, `--vertical-tabs`, `--terminals`, `--notifications`,
  `--sync`, `--welcome`, `--workspaces`, `--scratch`, `--startup`, `--ui-smoke`);
  choose modes from `tests/SharpRail.Checks/Program.cs` and add one when a change has
  none. Format-check the touched files.
- Run the full suite once per batch of related changes, after the focused modes pass — never after each
  small fix, and never to "see what breaks".
- Never build, format, check out or rebase in a tree whose suite is running. Run long suites from a
  separate SharpRail workspace (or wait for them), so the working tree stays free;
  check `pgrep -fl SharpRail.Checks` before building and identify which checkout
  each process uses. Never mutate that checkout's build output during its run.
- A failure ends a run early: fix the cause, rerun the focused mode that covers it, then resume with one
  full run — not a full run per attempt.
- Rewriting commits needs a build of each rewritten commit, not a full suite per commit; the full suite runs
  on the final tip only.
- Fixture git repositories run unsigned under a private configuration (`IsolatedGit.ForProcess`); a
  signing prompt during checks is a harness bug, not a reason to wait for the developer.

Run checks with `.tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release`;
`-- --terminals` runs host terminals, terminal/bottom-panel translations and Skia renderer checks.
`-- --ghostty-skia` runs focused Skia input, pixel, PTY and renderer Settings checks.
`-- --native-texture` checks GPU texture composition, input and local/remote renderer switching in a real macOS window.
`-- --sync` runs the multi-window and multi-client translations.
`-- --shared-terminals` runs the two-client terminal sharing, watching and take-over checks alone.
`-- --notifications` runs the away-notification checks with a recording notifier; no check posts a real one.
`-- --registry` runs the workspace registry host parity checks and the external-workspace rail checks.
`-- --documents` runs the resource-renderer checks and the translations that open file and diff bodies.
`-- --editor` runs editor integration and source editing checks; `-- --markdown`
and `-- --markdown-find` cover Markdown rendering and search.
`-- --agent-marks` covers agent decorations through workspace switches;
`-- --agent-launches` covers host and UI launch behavior; `-- --codex-terminals`
covers Codex terminal integration; `-- --claude-code` covers Claude Code.
`-- --terminal-replay` checks filtering of terminal queries in recorded screens.
`-- --workspace-tools` covers the host MCP workspace tools and UI integration;
`-- --serving` covers embedded-host serving; `-- --project-close` covers teardown.
`-- --pane-retention` checks retained controls; `-- --startup` checks progressive startup;
`-- --live-diffs` checks refreshing open rendered diffs.
`-- --change-actions` runs the toast and diff revert/undo checks; `-- --review` runs the Review panel checks.
`-- --shell` runs the window shell checks (header location bar, application menu, window chrome, region errors,
arrangement isolation, locations and links, the workspace dialog's project picker, commit menus and inert links).
`-- --ui` runs the headless UI checks and upstream translations without the host suites.
`-- --design` runs the design-system guards over the UI sources and the theme
translations; `-- --design --write` regenerates `src/SharpRail.Plugins.UI.Kit/Generated`
from `src/SharpRail.UI/Rendering/Design`. Edit the authored design sources, not
generated C#.
`-- --conformance` runs the dependency-boundary and public-surface checks; `-- --runner` checks the runner itself.
`-- --android` checks the Android client's host address parsing and remembered endpoint; the client itself needs a device (`scripts/android.sh run`).
`-- --lanes N` (or `auto`) splits the argument-free gate across processes, `-- --last-failed` reruns what the last
run left unfinished, `-- --list-cases` lists the gate and `-- --case A,B` runs named
cases. Lane/shard options apply to the argument-free gate (optionally narrowed by
named cases), not focused modes; `--last-failed` is serial.
`tests/SharpRail.Checks/Runner.cs` owns these, the
idle-sleep assertion and interrupt cleanup. A suite joins the gate through `Gate.Case` in `Program.cs` or `UiChecks.cs`.
Set `SHARPRAIL_TEST_GIT_SOURCE` to an existing upstream clone to include Git fixtures.
`tests/SharpRail.Checks/Program.cs` is the check runner, not an xUnit test project.
`ProjectChecks.cs` covers project/Git host parity; `LayoutChecks.cs` covers layout
transitions; `UiChecks.cs` runs the headless UI checks and upstream translations.
Use `E2E/E2eWorkspace.cs` for shared input/fixture helpers; its `NewWindow()` opens a second
window of the same app, and `E2E/CutProxy.cs` drops and restores one remote client's connection. Keep translated upstream
coverage in `E2E.md` separate from additional SharpRail regression checks.
For published checks, run `artifacts/checks/SharpRail.Checks` with
`SHARPRAIL_REQUIRE_R2R=1` to require ReadyToRun output as well as open-world checks.
C# formatting follows the checked-in `.editorconfig`, generated by the pinned SDK's
`dotnet new editorconfig` template with its default rule severities. Apply formatting
with `.tools/dotnet/dotnet format SharpRail.slnx --no-restore`; use `--include`
with touched C# files to keep changes surgical. Verify with
`.tools/dotnet/dotnet format SharpRail.slnx --verify-no-changes --no-restore`, as CI
does. Documentation-only edits need whitespace/path verification rather than a
solution build or the executable gate. Report checks actually run and anything
blocked; do not describe unrun checks as passed.
Generated packages live under `artifacts/`; keep one latest canonical app package.
`artifacts/ui`, `artifacts/host` and `artifacts/checks` contain published executables;
`artifacts/SharpRail.app` is the macOS bundle. `.tools`, `bin` and `obj` are local
tooling/build output, not source. `licenses/` and `THIRD-PARTY-NOTICES.md` document
redistributed dependencies and assets.

## UI thread hygiene

The dispatcher thread only builds controls, applies results and handles input. It never waits.

- No Git, process, network or filesystem work on the UI thread, at startup or after: status, diffs,
  branches, worktrees, fetch, clone, spec indexing, plugin requests and profile reads all run off it.
  The embedded host is in-process, so an `await` on a local adapter runs Core on the caller's thread
  until its first real asynchronous step; wrap such calls in `Task.Run` (or make the adapter hop) rather
  than awaiting Core directly from a handler.
- Never block on asynchronous work from the UI thread: no `.Result`, `.Wait()`, `GetAwaiter().GetResult()`,
  `Thread.Sleep`, `WaitForExit`, or a `lock` held across I/O.
- Return to the dispatcher only to apply results; check the result still belongs to the current
  workspace and was not superseded or cancelled before applying it.
- Long operations show progress and stay cancellable; the window stays responsive throughout (typing,
  resizing, switching tabs).
- Event handlers, constructors and `Build*`/render methods may read in-memory state only.

## Startup performance sanity

Minimize launch-to-interactive-workspace time. Resolve only the identity needed for routing before mounting and restoring documents; load independent panel data progressively.

- Defer full Git snapshots (status, diffs, line counts, untracked reads, branches, worktrees), recursive scans, spec indexing, network requests, nonessential profile writes, hidden panels, and inactive documents. Enumerate only files needed by visible UI.
- Run blocking I/O and substantial parsing in the background; update controls/layout on the dispatcher. Async alone does not move work off the UI thread, and awaiting it before mounting still blocks startup.
- Keep project-switch synchronization, but release its gate before optional Git/indexing refreshes. Cancel superseded work and reject stale workspace results.
- Reuse the initial control tree, restore visible documents first, and update only affected panel contents. Preserve dock chrome, tab instances, focus, and pointer targets during deferred refreshes.
- Measure window creation, first presented frame, and usable content separately; layout callbacks/readiness logs do not prove presentation or restored-content readiness. Verify rendering backend and publish settings before tuning Skia/JIT/GC; tie improvements to measured stages. Benchmark only when requested.

Verify fresh/restored profiles, project-switch races, and usability during deferred loading. Git/indexing failures must not block opening accessible workspace files.
