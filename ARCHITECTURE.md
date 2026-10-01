---
id: architecture
type: architecture-design
status: active
title: SharpRail — top-level architecture
---

# SharpRail — top-level architecture

Upstream: architecture.md @ c44534ea
Upstream: architecture.md @ 4737df6d (CommanderTvis fork), Decision 17 and the plugin rings

## Drivers

SharpRail is a native C# port of ThinkRail's workbench: projects, Git worktree workspaces, files, specs,
changes and terminals, without the agent. One Avalonia application composes a host either in process
(direct calls, no sockets or serialization) or over authenticated gRPC to a remote host. The host is a
library; remoteness is an adapter choice, never a mandatory local daemon.

## Topology — three rings

- Host: `SharpRail.Host.Core` implements filesystem, project/spec discovery, Git, worktree, shared-state and
  PTY terminal services behind the interfaces in `SharpRail.Host.Abstractions`. `SharpRail.Host.Remote`
  serves them over Kestrel HTTP/2 (`RemoteServer.Create`, started by its `Program.cs`).
- The wire: `SharpRail.Host.Protocol` holds the code-first protobuf-net.Grpc service contracts and DTOs —
  the only coupling between a remote client and host.
- Client: `SharpRail.UI` (Avalonia workbench) consumes the abstractions through `SharpRail.Host.Client`,
  whose `Local*Adapter`s call Core directly and whose `Remote*Adapter`s proxy over gRPC.
- Plugins: `SharpRail.Plugins.Api` (root, `.Host` and `.UI` entries) is the contract a plugin is written
  against, so its methods, channels and UI contributions can live outside either ring. The host runtime
  (`SharpRail.Host.Core/Plugins`) loads plugin host halves wherever the host runs; the app runtime
  (`SharpRail.UI/Plugins`) loads UI halves. `SharpRail.Plugins.UI.Kit` is the control kit both the app and a
  plugin's UI half build on, never a wire concern of its own.

```
SharpRail.UI              Avalonia app + workbench   ── depends on ─▶ Host.Core, Host.Client, Host.Remote (terminal relay socket only), Plugins.Api.UI, Plugins.UI.Kit, Scintilla
SharpRail.Host.Client     local adapters + gRPC proxies ─ depends on ─▶ Host.Abstractions, Host.Protocol
SharpRail.Host.Remote     Kestrel host + RPC adapters ── depends on ─▶ Host.Core, Host.Protocol
SharpRail.Host.Core       host implementation        ── depends on ─▶ Host.Abstractions, Plugins.Api.Host
SharpRail.Host.Protocol   wire contracts + DTOs
SharpRail.Host.Abstractions  transport-independent interfaces + domain records ─ depends on ─▶ Plugins.Api
SharpRail.Plugins.Api     plugin contract: manifest, contract vocabulary, roster, identity helpers
SharpRail.Plugins.Api.Host  plugin host context ─ depends on ─▶ Plugins.Api
SharpRail.Plugins.Api.UI  plugin UI context and contributions ─ depends on ─▶ Plugins.Api, Avalonia
SharpRail.Plugins.UI.Kit  shared controls, Markdown, editor frame, diagrams ─ depends on ─▶ Scintilla, Avalonia
SharpRail.Scintilla       self-contained editor control (references no SharpRail project)
tests/SharpRail.Checks    executable checks; references UI and Remote to exercise both paths
```

## Decisions

1. Client/host split. The host owns domain state; the UI renders it. Host projects never reference the
   UI or Avalonia. The UI references Core only to compose the embedded host, and Remote only to serve the
   in-process terminal relay socket.
2. The host is a library; the launcher is thin. `SharpRail.UI/Program.cs` and `App.cs` compose one
   app-owned `Workbench` (profile, shared-state subscription, terminal factory, per-window project session
   factory); the remote `Program.cs` reads its root, bind address, port, token and state directory from
   environment variables. An ordinary feature changes the abstraction, the Core implementation, both
   adapters, the wire contract and the checks — never a launcher.
3. One feature path across deployments. Local and remote are the same interface; checks verify
   local/remote parity for every host operation. A second environment that cannot supply an operation earns
   a narrow port in the owning service, not a global platform adapter.
4. Transport endpoint is a parameter. Embedded mode uses direct adapters; a remote client names the
   host's endpoint and token. Remote calls carry the workspace root the client opened
   (`ProjectHeaders.Root`), so clients never share a current project and resume after reconnecting without
   server-side sessions.
5. UI = panels + shell. Panels (project rail, Files, Specs, Changes, editor/diff/Markdown documents,
   terminals) never know their arrangement. Each window owns one persisted frame (`LayoutState`, applied
   through `LayoutSession`, rendered by `DockSurface`) with a recursively split center and auxiliary
   side/bottom groups; switching workspace changes only the projected workspace view, never the frame.
   Another window never rearranges this one. Static layout/styles/templates live in compiled `.axaml`;
   dynamic docking and host wiring live in C#.
6. Workspaces are Git worktrees. Project (Git repository) → workspace (a worktree on its own branch) →
   {files, changes, terminals}. Every project has one Default workspace whose root is the project's main
   working tree; it is never removable. Created worktrees go to `<project>-worktrees/workspace-N`.
7. Auth is a bearer token. The remote host requires `Authorization: Bearer <token>` on every call,
   compared in constant time; network reach (for example Tailscale) is the deployment's concern.
8. Hydrate-then-stream. A client never relies on having witnessed events: it reads current state, then
   subscribes. `IHostStateService.WatchAsync` yields a complete snapshot first and after every change, and
   remote clients resubscribe after a dropped transport, so a new window, a second client or a reconnect
   rebuilds the same settings, presets, projects, recents, labels and workspace lists. Clients apply the
   broadcast; they never write a local copy of host state.
9. Domain state vs frontend-local frame. Domain state (shared settings, custom presets, project list and
   recents, workspace labels and lifecycle, terminal sessions, Git) belongs to the host. The frame,
   per-workspace document placement, selection, default preset, group limits, bottom alignment and last
   location are per-window view state in `~/.sharprail/profile.json` (one `Windows` entry per window) and
   never cross the wire. Only resource-free custom preset definitions are host-persisted. Closing a
   document placement is local; closing a terminal tab ends its shell on the host.
10. Dependencies pin exact versions, once. `Directory.Packages.props` pins shared package versions and
    `global.json` selects the SDK; specs name the behavior they rely on rather than restating versions.
11. Terminal = Ghostty. macOS terminal tabs embed libghostty in native AppKit/Metal views hosted by
    Avalonia; other platforms show an availability message. The terminal child is the SharpRail executable
    in `--terminal-relay` mode, attached to a host-owned PTY session, so emulation stays native while
    shells stay on the host.
12. A shell belongs to a tab, and the host owns it. `PtyTerminalService` keys sessions by a stable id
    derived from workspace and tab; attach is get-or-create and exclusive with takeover. Shells outlive
    windows and client connections while the host runs, and end on tab close, natural exit or host stop.
    Lifetime is bounded by reference, never by idle timers. tmux is not used.
13. Plugins are the extension boundary, builtin and external at parity. A feature that would otherwise reach
    through every layer (a host method, its wire contract and adapters, a state slice, a panel switch arm)
    instead lives behind `SharpRail.Plugins.Api`: a manifest plus a host half, a UI half, or both, each a .NET
    assembly, loaded by `Host.Core/Plugins` and `UI/Plugins`. A builtin plugin is a project referenced by the
    host and the app and listed in their builtin arrays; an external plugin is a directory a user installs
    under `<stateDir>/plugins`, loaded into its own `AssemblyLoadContext` that shares the framework, Avalonia,
    the API and the kit with the default context. Both declare the same manifest, get the same capabilities
    and appear in the same roster. Every plugin can be turned on and off while the app runs, with no restart,
    through one serialised reconciler per host and the same reconcile in the app. A host half runs wherever
    the host runs, in process for the app's own host and inside a remote host otherwise; a UI half always
    runs in the app, an external one read through the host. Plugin methods and channels are one generic call
    and one generic stream, direct in process and JSON over gRPC. The API carries no compatibility promise; a
    single generation integer, declared in the manifest and checked before load, is the whole contract, and a
    mismatch is refused with a reason naming both generations. There is no sandbox: plugin code runs with the
    privileges of the host or the app. Full contract: `src/SharpRail.Plugins.Api/SPEC.md`.

## Invariants

- Dependencies flow toward abstractions (Core → Abstractions; Client → Abstractions + Protocol; Remote →
  Core + Protocol). No host project depends on the UI.
- Domain records live in Abstractions; serialized DTOs live in Protocol. Both adapters stay consistent
  with the public interface.
- The host holds the truth and clients hydrate from reads, then stream changes; the UI holds only view
  state of its own.
- Blocking I/O and Git run off the UI thread; startup mounts the routed workspace before loading
  deferred panel data (see the startup rules in `AGENTS.md`).
- Separate app processes do not coordinate: each composes its own host. Multi-client means the windows of
  one process plus the clients of one remote host.
- The host's session token never reaches user shells.
- Plugins reference only the API assemblies, the kit and their declared dependencies' contracts, never a
  `SharpRail.Host.*` project or `SharpRail.UI`; the host and UI API entries do not reference each other.

## Not yet ported

- A protocol version handshake so an independently shipped client can detect host drift.
- Workspace-local review, pull-request opening and CI status.
- Host-owned revert of a hunk or of a file's whole change, with undo: the host derives what to write and
  guards it by compare-and-swap, so a client never sends the bytes.
- Byte-only content (images, PDFs) on a diff side, served at an immutable commit, and active content
  (HTML, SVG) confined so that repository script never runs.
- A mobile shell projecting the same panels.
- Revival of terminal tabs with recorded output across a host restart (restored tabs start fresh shells).
