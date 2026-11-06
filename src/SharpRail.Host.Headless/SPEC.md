---
id: module-host-headless
type: module-design
status: active
title: Headless host, stdio MCP for one workspace
parent: module-host-core
depends-on: [module-host-core, module-plugin-api]
tags: [headless, mcp, research]
---

# Headless host

## Responsibility

A console executable, `sharprail-headless`, that starts the host core and the non-UI builtin plugins and serves MCP over
stdio for exactly one workspace. An agent such as Claude Code uses SharpRail's language, spec and Blueprint tools from a
`.mcp.json` entry, with no window, terminal or agent of SharpRail's own. The user page is [docs/headless.md](../../docs/headless.md).

## Boundary

- Owns `Program.cs` (arguments, configuration, exit codes, composition) and `StdioMcp.cs` (the line protocol).
- Allowed deps: `SharpRail.Host.Core` and what it reaches. Forbidden: Avalonia, Skia, any UI project, Kestrel or any network
  listener, terminals, per-terminal tokens. `BoundaryChecks` fails on a project reference or source mention, and
  `--headless` fails on any UI or web package in the restore graph, transitive ones included.
- Tool behaviour is Core's and the plugins'. This project only frames messages. The one change to Core is the optional
  `instructions` argument of `McpServer.HandleAsync` and the `BuiltinPlugins.Headless` roster.

## Behaviour

- Command: `sharprail-headless mcp [--workspace <dir>]`. The workspace is `--workspace`, else `SHARPRAIL_ROOT`, else the current directory.
- State (settings, language server installs and sessions) lives in `SHARPRAIL_STATE_DIR`, else `~/.sharprail/headless`. A store with no
  directory would turn language services off, so there is always one. Two processes on one directory are last-writer-wins.
- Roster: the spec graph, Blueprint and the language servers, enabled by default. Tools are `lsp_*`, `spec_*` and
  `blueprint_check`, with the schemas the desktop serves. Terminal tools (`set_title`, `workspace_*`) and Air Context are absent.
- One workspace per process. A tool's optional `workspace` argument is dropped, so a path the agent knows from outside a
  container still works. Relative paths resolve against the workspace.
- Protocol: one JSON-RPC 2.0 object per line on stdin, one per line on stdout. `initialize` carries headless instructions.
  Notifications get no reply. A line that is not JSON gets a `-32700` error with a null id. Replies to requests the server never sent are ignored.
- Requests run concurrently and answer in any order. Each reply is one line written under a lock and flushed.
  `notifications/cancelled` cancels the request with that id, which then gets no reply.
- stdout holds frames only. At start `Console.Out` is redirected to stderr, so a stray print cannot corrupt the stream. Logs go to stderr.
- End of input, SIGINT or SIGTERM: running calls get 5 s to finish, then are cancelled; plugins stop, then language servers; exit 0.
  A closed stdout counts as a stop.
- Exit 2 with a message on stderr, before any frame, for an unknown command or argument, a workspace that is not a directory, or a
  state directory that cannot be created.

## Not built yet

Runtime provisioning, tool switches, the call log, `warm`, `check-config`, `manifest`, and the container image.
