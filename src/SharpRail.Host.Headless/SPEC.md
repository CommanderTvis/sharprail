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

- Owns `Program.cs` (arguments, exit codes, composition), `StdioMcp.cs` (the line protocol), `McpConfig.cs`, `ToolSwitches.cs` and `CallLog.cs`.
- Allowed deps: `SharpRail.Host.Core` and what it reaches. Forbidden: Avalonia, Skia, any UI project, Kestrel or any network
  listener, terminals, per-terminal tokens. `BoundaryChecks` fails on a project reference or source mention, and
  `--headless` fails on any UI or web package in the restore graph, transitive ones included.
- Tool behaviour is Core's and the plugins'. This project only frames messages. The one change to Core is the optional
  `instructions` argument of `McpServer.HandleAsync` and the `BuiltinPlugins.Headless` roster.

## Behaviour

- Commands: `sharprail-headless mcp|check-config [--workspace <dir>]`. The workspace is `--workspace`, else `SHARPRAIL_ROOT`, else the current directory.
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
- `warm [server ...]` installs the pinned .NET SDK and the named language servers (default `csharp`) into the state directory, without a session.
  It needs network, prints nothing on stdout, exits 0 when every server is installed and 2 for an unknown server or a failed install.
  A second `warm` on a filled cache does nothing and returns at once.
- Installs write to the lifecycle log (`code-intelligence.log`): `host.runtime-install` and `host.server-install` with outcome and duration,
  `host.install-progress` messages, and `host.install-command` with the command line, exit code, duration and the last output of each installer process.
- Exit 2 with a message on stderr, before any frame, for an unknown command or argument, a workspace that is not a directory, or a
  state directory that cannot be created.

## Container image

Built from `docker/headless/Dockerfile` by `.github/workflows/container.yml`, published as `ghcr.io/commandertvis/sharprail`.

- Contents: the self-contained, untrimmed publish of this project on `dotnet/runtime-deps` (Ubuntu noble), with `git`, `ca-certificates` and `tzdata`.
- User and paths: uid 1000, `HOME=/cache/home`, `WORKDIR /workspace`, `VOLUME /cache`.
- Environment: `SHARPRAIL_TOOLCHAINS=managed`, `SHARPRAIL_MANAGED_CACHE=/cache/managed`, `SHARPRAIL_STATE_DIR=/cache/state`, `SHARPRAIL_ROOT=/workspace`, `DOTNET_NOLOGO=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, and git `safe.directory=*` through `GIT_CONFIG_*`.
- Entry: `ENTRYPOINT ["sharprail-headless"]`, `CMD ["mcp"]`. No wrapper, so stdin and stdout are the process's own. Run with `-i --init`.
- Exit codes are the ones above: 0 on end of input or SIGTERM, 2 on a configuration error.
- Labels: OCI source, description, licenses (`PolyForm-Shield-1.0.0`), revision and created.
- Tags: `nightly` (overwritten each run) and `sha-<short>`, as multi-arch lists over `-amd64` and `-arm64` tags. arm64 may be missing.
- No image is pushed unless `scripts/container-smoke.sh` passes on it.

## Tool switches and call log

- `.sharprail/mcp.toml` (`version = 1`, strict: unknown keys, wrong types and unknown operations exit 2 with `file:line: key: message`) holds `tools.disable`, `tools.allow`, `[tools.<name>]` (`enabled`, `operations` for `lsp_navigate` and `lsp_status`), `[log]` and `[arms.<name>]` tables of tools and log. An arm replaces each key it sets.
- Precedence: defaults, file top level, arm, environment (`SHARPRAIL_ARM`, `SHARPRAIL_TOOLS_DISABLE`, `SHARPRAIL_TOOLS_ALLOW`, `SHARPRAIL_LOG`, `SHARPRAIL_LOG_LEVEL`, `SHARPRAIL_RUNS_DIR`), flags (`--arm`, `--disable-tools`). A selected arm the file lacks is an error.
- One decorator over the tool table drops switched-off tools (unknown tool for list and call), narrows operations in the schema and at call time, and feeds the log. The initialize instructions are generated from the offered tools.
- The log is off by default and then nothing is created in `.sharprail`. When on, `<dir>/<session>.jsonl` (default `.sharprail/runs`) gets `start`, `call` and `end` lines from a background writer, flushed per line, bounded by `max_file_kb`. It never fails a call.
- `check-config` prints the effective configuration as JSON and exits 0, or 2.

## Not built yet

`manifest`.
