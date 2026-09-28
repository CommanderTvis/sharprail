# SharpRail

A C# Avalonia prototype of Thinkrail's workspace. Tabs, project opening,
Markdown previews, persistent settings, Git changes/diffs, and worktrees work.
The terminal and source viewer are placeholders/read-only; there is no editor,
Pi, or AI integration.

## Use the prototype

- Open a project with the Projects panel's + button or ⌘O (Ctrl+O on other platforms).
- Select Files; single-click previews a file, double-click or Enter keeps it.
  Markdown renders natively with selectable text, tables, links and images.
- Drag tabs to reorder, join panes, split the center, or create auxiliary panes.
  Context menus provide the same arrangements. Escape cancels drafts.
- Right-click the title bar to reveal hidden tools/regions; ⌘⇧J toggles the bottom.
  Ctrl+F6 visits visible panes. Separators also resize with arrow keys.
- Open Settings with ⌘, to change theme, font size, Markdown width, presets,
  group limits, bottom alignment, and hidden-file visibility.
- Changes supports staged/working/branch scopes, tree/list views, diffs and
  stage/unstage context actions. F5 refreshes files and Git.
- Projects lists actual Git worktrees. Its + button creates a new branch and
  worktree; selecting one switches workspace while keeping the shared frame.
  Removal asks for confirmation, retains the branch, and refuses dirty,
  active, main or locked worktrees. No commit or push command is provided.

The current Balanced layout is the visual reference. Frame geometry is shared
within the frontend; open documents and selections belong to each workspace.
Application state is saved under `~/.sharprail/profile.json`, shared across projects.
An existing project-local `.sharprail/profile.json` migrates on first launch when no home profile exists.
`SHARPRAIL_PROFILE` chooses another profile directory. A saved last project is
restored on launch. Opening a folder inside a Git repository uses its root.

## Architecture

```text
Avalonia → IProjectServices
             ├─ LocalProjectAdapter → C# Host.Core, direct calls
             └─ RemoteProjectAdapter → protobuf-net.Grpc / HTTP2 → C# Host.Remote → Host.Core
```

Host.Core references only domain abstractions. Protobuf DTOs and gRPC stay at
the remote boundary. Embedded mode creates no server, sockets or serializer.
The original two-method workspace API remains available for research clients.
The remote prototype serves one frontend session with a shared current root.

## Build and package

.NET SDK 10.0.401 is pinned. Install it in this checkout if needed:

```sh
scripts/bootstrap.sh
.tools/dotnet/dotnet build -c Release
SHARPRAIL_ROOT="$PWD" .tools/dotnet/dotnet run --project src/SharpRail.UI -c Release
scripts/publish.sh
open artifacts/SharpRail.app
```

`SHARPRAIL_ROOT` defaults to the working directory. Publishing defaults to
`osx-arm64`; another RID can be passed to the script. Set `DOTNET` to use an
existing SDK. UI, standalone host, and checks publish separately. macOS builds
include an ad-hoc signed app bundle.

Release uses optimized CoreCLR, non-composite ReadyToRun, tiered compilation,
dynamic PGO and workstation GC. Nullable checking and warnings-as-errors apply
to every project. Trimming, NativeAOT and single-file packaging are disabled.
IL, reflection, runtime assembly loading and JIT code generation remain usable.
Ordinary builds use IL/JIT; ReadyToRun is applied during publishing.

## Remote mode

```sh
SHARPRAIL_TOKEN=research-session-token SHARPRAIL_ROOT="$PWD" artifacts/host/SharpRail.Host.Remote
SHARPRAIL_REMOTE=http://127.0.0.1:54123 SHARPRAIL_TOKEN=research-session-token artifacts/SharpRail.app/Contents/MacOS/SharpRail.UI
```

For Tailscale, set the server's `SHARPRAIL_BIND` to its tailnet IP and the
client's `SHARPRAIL_REMOTE` to `http://machine.tailnet.ts.net:54123`.
`SHARPRAIL_PORT` changes the server port. The listener defaults to loopback.
The session token authenticates requests; cleartext HTTP/2 remote deployment
relies on Tailscale's encrypted connectivity. No Tailscale setup is installed.
Remote project selection prompts for a directory on the host.

## Verification

[E2E translation coverage](E2E.md) lists the upstream cases, translated native
tests, exclusions and remaining work.

```sh
SHARPRAIL_TEST_GIT_SOURCE=/path/to/existing/repository .tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release
.tools/dotnet/dotnet format SharpRail.slnx --verify-no-changes --no-restore
SHARPRAIL_REQUIRE_R2R=1 SHARPRAIL_TEST_GIT_SOURCE=/path/to/existing/repository artifacts/checks/SharpRail.Checks
```

Git checks use isolated shallow clones of existing commits and never commit,
push, or change signing configuration. Without `SHARPRAIL_TEST_GIT_SOURCE`,
Git mutation checks report a skip. Checks cover local/remote parity, auth,
cancellation, Git/worktrees, layout invariants, real pointer drop/cancellation,
settings persistence, project restoration, and Markdown rendering. Published
checks verify R2R headers, runtime IL emission and dynamic assembly loading.
A headless render is saved as `.bench/prototype-markdown.png`.

See [SPEC.md](SPEC.md) for the prototype contract. The existing research harness
in `scripts/benchmark.mjs` remains available for explicitly requested research;
prototype implementation and verification do not run benchmarks.
