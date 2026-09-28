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
| `src/SharpRail.UI/Assets` | Reference icons and bundled fonts, with their licenses. |
| `tests/SharpRail.Checks` | Executable checks for host transports, runtime extensibility, layout, UI and Git/worktree integration. `E2E/` translates upstream scenarios using real headless Avalonia input. |
| `scripts/bootstrap.sh` | Installs the checkout's local .NET SDK. |
| `scripts/publish.sh` | Publishes non-composite R2R UI, remote host and checks; refreshes and signs the canonical `artifacts/SharpRail.app`. Check for a live app process before replacing it. |
| `.bench` | Ignored disposable fixtures, verification logs and own-window captures. Its name does not authorize benchmarks. |

Static UI layouts/styles/templates belong in compiled `.axaml`; dynamic docking
and host/interaction wiring belong in C#. Keep the host independent of the UI and
make remoteness an adapter choice rather than a mandatory local daemon.

The application starts in `src/SharpRail.UI/Program.cs` and `App.cs`.
`WorkbenchWindow` owns the workbench; `DockSurface` renders and handles docking,
while `LayoutSession` applies transitions to `LayoutState`. `ProfileStore` owns
on-disk state. Keep these responsibilities separate when adding interactions.

The workbench is split into partial files rather than separate window classes:

| File in `src/SharpRail.UI` | Responsibility |
| --- | --- |
| `WorkbenchWindow.axaml` / `WorkbenchWindow.cs` | Static window frame, startup, workspace switching and workbench composition. |
| `DocumentNavigation.cs` / `DocumentCache.cs` | Opening/restoring documents, navigation and cached document-control lifetime. |
| `ProjectPanels.cs` | Files, Specs and Projects panel construction and project actions. |
| `GitPanels.cs` / `WorkspaceGit.cs` | Git panel controls and cancellable, workspace-scoped snapshot refreshes. |
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
