# SharpRail instructions

## Project structure

`SharpRail.slnx` contains the .NET 10 C# solution. `Directory.Build.props` enables
nullable checking, warnings as errors, optimizations and tiered PGO while keeping
NativeAOT and trimming disabled. `Directory.Packages.props` pins shared package
versions; `global.json` selects the SDK. Use `.tools/dotnet/dotnet` for this checkout.

| Path | Responsibility |
| --- | --- |
| `src/SharpRail.Host.Abstractions` | Transport-independent host interfaces and domain records consumed by the UI. |
| `src/SharpRail.Host.Core` | Filesystem, project/spec discovery, Git, worktree and PTY terminal implementations (`Posix.cs` holds the libc interop). No Avalonia, Pi or AI dependency. |
| `src/SharpRail.Host.Protocol` | Code-first protobuf-net.Grpc service contracts and wire DTOs. |
| `src/SharpRail.Host.Remote` | Kestrel HTTP/2 host, authentication and RPC adapters delegating to Core. |
| `src/SharpRail.Host.Client` | Direct local adapters and gRPC remote proxies implementing the same host abstractions. Embedded mode uses no sockets or serialization. |
| `src/SharpRail.Scintilla` | Self-contained Avalonia editor control: Scintilla with a Skia surface, HarfBuzz shaping and SheenBidi layout. Its `README.md` documents the API, native build and limits. It references no SharpRail project. |
| `src/SharpRail.UI` | Avalonia application entry point and workbench. `WorkbenchWindow` partial files coordinate navigation, projects and Git panels. |
| `src/SharpRail.UI/Docking` | Persisted frame/workspace layout model, transitions, geometry, pointer/keyboard gestures, tab chrome and search popover. |
| `src/SharpRail.UI/Panels` | Settings and shared dialogs, including compiled XAML frames and page templates. |
| `src/SharpRail.UI/Rendering` | Native Markdown rendering, preview/source view and shared UI assets/styles/helpers. |
| `src/SharpRail.UI/State` | Profile persistence and migration (`ProfileStore`: app preferences and one entry per window) and the app's shared-state subscription (`SharedState`). Default user state belongs in `~/.sharprail`, not project directories. |
| `src/SharpRail.UI/Terminal` | Terminal tab body (`TerminalView`: start failure/retry, exit notice), the Ghostty native-control bridge for local and relayed remote sessions, and the `--terminal-relay` mode. |
| `src/SharpRail.UI/Assets` | Reference icons and bundled fonts, with their licenses. |
| `src/Ghostty.Avalonia` | Independent Ghostty controls: hosted AppKit/Metal, Metal textures composed by Avalonia, and libghostty-vt drawn by Skia; native bridges, build script and licenses. Its README documents reuse and limits. |
| `tests/SharpRail.Checks` | Executable checks for host transports, runtime extensibility, layout, UI and Git/worktree integration. `E2E/` translates upstream scenarios using real headless Avalonia input. |
| `scripts/dev.sh` | Builds and runs the app from source in one step (`SHARPRAIL_PROFILE` keeps a separate profile). |
| `scripts/bootstrap.sh` | Installs the checkout's local .NET SDK. |
| `src/Ghostty.Avalonia/build-native.sh` | Builds pinned libghostty and libghostty-vt bridges on the target macOS architecture, using checkout-local tools and caches. |
| `scripts/build-merman.sh` | Downloads Merman's pinned, checksummed macOS xcframework and links its C ABI into `.tools/merman/libSharpRailMermaid.dylib`. `Rendering/MermaidRenderer.cs` renders SVG through it off the UI thread; Svg.Skia displays it. Other platforms show the source with an unavailability message. |
| `scripts/check-terminal.sh` | Runs the native shell/Metal probe; the checks executable's `--native-terminal` mode exercises Avalonia integration. |
| `scripts/publish.sh` | Publishes non-composite R2R UI, remote host and checks; refreshes and signs the canonical `artifacts/SharpRail.app`. Check for a live app process before replacing it. |
| `.bench` | Ignored disposable fixtures, verification logs and own-window captures. Its name does not authorize benchmarks. |

Static UI layouts/styles/templates belong in compiled `.axaml`; dynamic docking
and host/interaction wiring belong in C#. Keep the host independent of the UI and
make remoteness an adapter choice rather than a mandatory local daemon.

For host changes, follow the operation through these files:

| Layer | Files |
| --- | --- |
| Public API | `Host.Abstractions/IWorkspaceHost.cs`, `ProjectServices.cs`, `HostState.cs` and `TerminalServices.cs`. |
| Implementation | `Host.Core/WorkspaceHost.cs`, `ProjectServices.cs`, `HostStateStore.cs`, `SpecCatalog.cs`, `GitRepository.cs` and `PtyTerminalService.cs`. |
| Wire contracts | `Host.Protocol/WorkspaceContract.cs`, `ProjectContract.cs`, `StateContract.cs` and `TerminalContract.cs`. |
| Client adapters | `Host.Client/HostAdapters.cs`, `ProjectAdapters.cs`, `StateAdapters.cs` and `TerminalAdapters.cs`. |
| Server adapters | `Host.Remote/WorkspaceRpc.cs`, `ProjectRpc.cs` (per-call workspace from `ProjectSessions.cs`), `StateRpc.cs` and `TerminalRpc.cs`; `RemoteServer.cs` configures the server and `Program.cs` starts it. |

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
Every window of the app comes from it (New window, Mod+Shift+N); there is no daemon.
`WorkbenchWindow` owns one window's workbench; `DockSurface` renders and handles docking,
while `LayoutSession` applies transitions to `LayoutState`. `ProfileStore` owns
on-disk app state. Keep these responsibilities separate when adding interactions.
Settings, custom presets, workspace labels, the project list/recents and workspace
lifecycle are host state (`IHostStateService`): change them through the host and
update UI when the broadcast arrives (`HostSync.cs`), never by writing a local copy.
Local host state is `~/.sharprail/state.json`, migrated once from older profiles; a
remote host keeps its own in `SHARPRAIL_STATE_DIR`. The default profile file is
`~/.sharprail/profile.json`; it contains app preferences (interface size, hidden
files), one `Windows` entry per window (frame, default preset, last location), rail
expansion and per-workspace Git selections. Persist target,
scope and selected commit; reload commit catalogs from Git rather than saving
derived snapshots. Tests use isolated profile directories.
Commit listing is a separate host operation; do not fetch a full working-tree
snapshot merely to populate or restore the commit catalog.
Settings selects Metal texture (default) or Skia (fallback) terminal rendering. Local Metal tabs
use Ghostty's external-I/O backend with direct calls to the app-owned PTY service: no relay child,
socket, serialization or RPC, including fallback to Skia. Remote Metal tabs run the
`--terminal-relay` child attached to their remote host. Shells outlive their windows while the host runs.
Skia tabs attach directly to the same host service and draw through `GhosttySkiaView`.
Both native builds currently require macOS; other platforms show an availability message. `WorkbenchWindow`
takes its terminal factory from the composition root; headless checks pass one that
runs host PTY sessions as plain text. `DocumentCache.cs` retains shells across appearance
changes and disposes them when their tabs or window close. Clipboard images are
stored under the active profile's `clipboard` directory. Terminal functionality
is in scope following integration of the `ghostty` worktree, and macOS text files
open in the Scintilla editor from the `scintilla` branch; AI functionality remains
excluded.

The workbench is split into partial files rather than separate window classes:

| File in `src/SharpRail.UI` | Responsibility |
| --- | --- |
| `WorkbenchWindow.axaml` / `WorkbenchWindow.cs` | Static window frame, startup, workspace switching and workbench composition. |
| `DocumentNavigation.cs` / `DocumentCache.cs` | Opening/restoring documents, navigation and cached document-control lifetime. |
| `ProjectPanels.cs` | Files, Specs and Projects panel construction and project actions. |
| `ProjectHome.cs` | Startup routing, Welcome/Project Home, project context actions, the Create workspace flow and workspace row actions. |
| `GitPanels.cs` / `ChangesTree.cs` / `WorkspaceGit.cs` | Git panel controls, compact change-tree projection and cancellable, workspace-scoped snapshot refreshes. |
| `GestureNotification.cs` | Feedback when layout transitions cancel an active gesture. |
| `HostSync.cs` | Applying shared-state broadcasts and reconnects to the window. |
| `Workbench.cs` | App-owned composition shared by windows and the per-window profile entries. |
| `TerminalTabs.cs` | Confirmation before closing terminals that run a foreground process. |

Host dependencies flow toward abstractions: Core references Abstractions; Client
references Abstractions and Protocol; Remote references Core and Protocol. The UI
references Core and Client to compose either direct local calls or remote proxies,
the Scintilla editor control, which it supplies with theme colours and fonts, and
Ghostty.Avalonia. The UI does not reference Remote or start a local RPC server.
Checks reference the UI and Remote to exercise both paths. Do not introduce a UI
dependency into the host projects.

`SPEC.md` defines the product contract; `COMPLETION.md` records unfinished gates;
`E2E.md` inventories upstream translations; `VALIDATION.md` records verified
evidence. Read `gotchas.md` for lessons and `context-log.md` for continuation state.
The authoritative upstream checkout is `/Users/commandertvis/IdeaProjects/thinkrail`.
Module `SPEC.md`/`*.SPEC.md` files and `ARCHITECTURE.md` are adapted from upstream specs;
`UPSTREAM.md` is the single source of truth for the synced upstream commit and mapping;
the [sync-upstream-specs skill](.claude/skills/sync-upstream-specs/SKILL.md) pulls later
upstream changes. Update the owning spec when changing its module.

Run checks with `.tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release`;
`-- --terminals` runs host terminals, terminal/bottom-panel translations and Skia renderer checks.
`-- --ghostty-skia` runs focused Skia input, pixel, PTY and renderer Settings checks.
`-- --native-texture` checks GPU texture composition, input and local/remote renderer switching in a real macOS window.
`-- --sync` runs the multi-window and multi-client translations.
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
with `.tools/dotnet/dotnet format SharpRail.slnx --no-restore`; verify it with
`.tools/dotnet/dotnet format SharpRail.slnx --verify-no-changes --no-restore`, as CI does.
Generated packages live under `artifacts/`; keep one latest canonical app package.
`artifacts/ui`, `artifacts/host` and `artifacts/checks` contain published executables;
`artifacts/SharpRail.app` is the macOS bundle. `.tools`, `bin` and `obj` are local
tooling/build output, not source. `licenses/` and `THIRD-PARTY-NOTICES.md` document
redistributed dependencies and assets.

## Startup performance sanity

Minimize launch-to-interactive-workspace time. Resolve only the identity needed for routing before mounting and restoring documents; load independent panel data progressively.

- Defer full Git snapshots (status, diffs, line counts, untracked reads, branches, worktrees), recursive scans, spec indexing, network requests, nonessential profile writes, hidden panels, and inactive documents. Enumerate only files needed by visible UI.
- Run blocking I/O and substantial parsing in the background; update controls/layout on the dispatcher. Async alone does not move work off the UI thread, and awaiting it before mounting still blocks startup.
- Keep project-switch synchronization, but release its gate before optional Git/indexing refreshes. Cancel superseded work and reject stale workspace results.
- Reuse the initial control tree, restore visible documents first, and update only affected panel contents. Preserve dock chrome, tab instances, focus, and pointer targets during deferred refreshes.
- Measure window creation, first presented frame, and usable content separately; layout callbacks/readiness logs do not prove presentation or restored-content readiness. Verify rendering backend and publish settings before tuning Skia/JIT/GC; tie improvements to measured stages. Benchmark only when requested.

Verify fresh/restored profiles, project-switch races, and usability during deferred loading. Git/indexing failures must not block opening accessible workspace files.
