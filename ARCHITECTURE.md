---
id: architecture
type: architecture-design
status: active
title: SharpRail — top-level architecture
---

# SharpRail — top-level architecture

Upstream: architecture.md @ 12830b08

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

```
SharpRail.UI              Avalonia app + workbench   ── depends on ─▶ Host.Core, Host.Client, Host.Remote (terminal relay socket only), Scintilla
SharpRail.Host.Client     local adapters + gRPC proxies ─ depends on ─▶ Host.Abstractions, Host.Protocol
SharpRail.Host.Remote     Kestrel host + RPC adapters ── depends on ─▶ Host.Core, Host.Protocol
SharpRail.Host.Core       host implementation        ── depends on ─▶ Host.Abstractions
SharpRail.Host.Protocol   wire contracts + DTOs
SharpRail.Host.Abstractions  transport-independent interfaces + domain records
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

## Not yet ported

- A protocol version handshake so an independently shipped client can detect host drift.
- Workspace-local review, pull-request opening and CI status.
- A mobile shell projecting the same panels.
- Revival of terminal tabs with recorded output across a host restart (restored tabs start fresh shells).
