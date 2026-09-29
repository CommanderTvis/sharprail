# SharpRail instructions

## Project structure

`SharpRail.slnx` contains the .NET 10 C# solution. `Directory.Build.props` enables
nullable checking, warnings as errors, optimizations and tiered PGO while keeping
NativeAOT and trimming disabled. `Directory.Packages.props` pins shared package
versions; `global.json` selects the SDK. Use `.tools/dotnet/dotnet` for this checkout.

| Path | Responsibility |
| --- | --- |
| `src/SharpRail.Host.Abstractions` | Transport-independent host interfaces and domain records consumed by the UI. |
| `src/SharpRail.Host.Core` | Filesystem, project/spec discovery, Git and worktree implementations. No Avalonia, Pi or AI dependency. |
| `src/SharpRail.Host.Protocol` | Code-first protobuf-net.Grpc service contracts and wire DTOs. |
| `src/SharpRail.Host.Remote` | Kestrel HTTP/2 host, authentication and RPC adapters delegating to Core. |
| `src/SharpRail.Host.Client` | Direct local adapters and gRPC remote proxies implementing the same host abstractions. Embedded mode uses no sockets or serialization. |
| `src/SharpRail.UI` | Avalonia application entry point and workbench. `WorkbenchWindow` partial files coordinate navigation, projects and Git panels. |
| `src/SharpRail.UI/Docking` | Persisted frame/workspace layout model, transitions, geometry, pointer/keyboard gestures, tab chrome and search popover. |
| `src/SharpRail.UI/Panels` | Settings and shared dialogs, including compiled XAML frames and page templates. |
| `src/SharpRail.UI/Rendering` | Native Markdown rendering, preview/source view and shared UI assets/styles/helpers. |
| `src/SharpRail.UI/State` | Profile/preferences persistence and migration. Default user state belongs in `~/.sharprail`, not project directories. |
| `src/SharpRail.UI/Terminal` | Avalonia native-control bridge to embedded Ghostty terminals; owns focus, theme updates and session disposal. |
| `src/SharpRail.UI/Assets` | Reference icons and bundled fonts, with their licenses. |
| `native/ghostty` | Objective-C AppKit/Metal bridge, Ghostty configuration shim and native terminal probe. |
| `tests/SharpRail.Checks` | Executable checks for host transports, runtime extensibility, layout, UI and Git/worktree integration. `E2E/` translates upstream scenarios using real headless Avalonia input. |
| `scripts/bootstrap.sh` | Installs the checkout's local .NET SDK. |
| `scripts/build-ghostty.sh` | Builds pinned libghostty and the native bridge on the target macOS architecture, using checkout-local tools and caches. |
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
| Public API | `Host.Abstractions/IWorkspaceHost.cs` and `ProjectServices.cs`. |
| Implementation | `Host.Core/WorkspaceHost.cs`, `ProjectServices.cs`, `SpecCatalog.cs` and `GitRepository.cs`. |
| Wire contracts | `Host.Protocol/WorkspaceContract.cs` and `ProjectContract.cs`. |
| Client adapters | `Host.Client/HostAdapters.cs` and `ProjectAdapters.cs`. |
| Server adapters | `Host.Remote/WorkspaceRpc.cs` and `ProjectRpc.cs`; `RemoteServer.cs` configures the server and `Program.cs` starts it. |

Each `Host.*` prefix in this table denotes its `src/SharpRail.Host.*` project.
Domain records belong in
Abstractions; serialized DTOs belong in Protocol. Keep both adapters consistent
with the public API and verify local/remote parity in the checks.

The application starts in `src/SharpRail.UI/Program.cs` and `App.cs`.
`WorkbenchWindow` owns the workbench; `DockSurface` renders and handles docking,
while `LayoutSession` applies transitions to `LayoutState`. `ProfileStore` owns
on-disk state. Keep these responsibilities separate when adding interactions.
The default profile file is `~/.sharprail/profile.json`; it contains preferences,
layout, remembered projects and per-workspace Git selections. Persist target,
scope and selected commit; reload commit catalogs from Git rather than saving
derived snapshots. Tests use isolated profile directories.
Commit listing is a separate host operation; do not fetch a full working-tree
snapshot merely to populate or restore the commit catalog.
Local macOS terminal tabs embed Ghostty directly; remote and other-platform tabs
show availability messages. `DocumentCache.cs` retains shells across appearance
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

Host dependencies flow toward abstractions: Core references Abstractions; Client
references Abstractions and Protocol; Remote references Core and Protocol. The UI
references Core and Client to compose either direct local calls or remote proxies.
Checks reference the UI and Remote to exercise both paths. Do not introduce a UI
dependency into the host projects.

`SPEC.md` defines the product contract; `COMPLETION.md` records unfinished gates;
`E2E.md` inventories upstream translations; `VALIDATION.md` records verified
evidence. Read `gotchas.md` for lessons and `context-log.md` for continuation state.
The authoritative upstream checkout is `/Users/commandertvis/IdeaProjects/thinkrail`.

Run checks with `.tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release`.
Set `SHARPRAIL_TEST_GIT_SOURCE` to an existing upstream clone to include Git fixtures.
`tests/SharpRail.Checks/Program.cs` is the check runner, not an xUnit test project.
`ProjectChecks.cs` covers project/Git host parity; `LayoutChecks.cs` covers layout
transitions; `UiChecks.cs` runs the headless UI checks and upstream translations.
Use `E2E/E2eWorkspace.cs` for shared input/fixture helpers. Keep translated upstream
coverage in `E2E.md` separate from additional SharpRail regression checks.
For published checks, run `artifacts/checks/SharpRail.Checks` with
`SHARPRAIL_REQUIRE_R2R=1` to require ReadyToRun output as well as open-world checks.
Verify formatting with `.tools/dotnet/dotnet format SharpRail.slnx --verify-no-changes --no-restore`.
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
