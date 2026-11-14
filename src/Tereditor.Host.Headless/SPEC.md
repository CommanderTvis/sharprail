---
id: module-host-headless
type: module-design
status: active
title: Headless host, stdio MCP for one workspace
parent: module-host-core
depends-on: [module-host-core, module-plugin-api]
references: [module-plugin-security]
tags: [headless, mcp, research]
---

# Headless host

## Responsibility

A console executable, `tereditor-headless`, that starts the host core and the non-UI builtin plugins and serves MCP over
stdio for exactly one workspace. An agent such as Claude Code uses tereditor's language tools, and on opt-in the spec and Blueprint tools, from a
`.mcp.json` entry, with no window, terminal or agent of tereditor's own. The user page is [docs/headless.md](../../docs/headless.md).

## Boundary

- Owns `Program.cs` (arguments, exit codes, composition), `StdioMcp.cs` (the line protocol), `McpConfig.cs`, `ToolSwitches.cs` and `CallLog.cs`.
- Allowed deps: `Tereditor.Host.Core` and what it reaches. Forbidden: Avalonia, Skia, any UI project, Kestrel or any network
  listener, terminals, per-terminal tokens. `BoundaryChecks` fails on a project reference or source mention, and
  `--headless` fails on any UI or web package in the restore graph, transitive ones included.
- Tool behaviour is Core's and the plugins'. This project only frames messages. The one change to Core is the optional
  `instructions` argument of `McpServer.HandleAsync` and the `BuiltinPlugins.Headless` roster, which follows the manifests' `Headless` declaration.

## Behaviour

- Commands: `tereditor-headless mcp|check-config [--workspace <dir>]`. The workspace is `--workspace`, else `TEREDITOR_ROOT`, else the current directory.
- State (settings, language server installs and sessions) lives in `TEREDITOR_STATE_DIR`, else `~/.tereditor/headless`. A store with no
  directory would turn language services off, so there is always one. Two processes on one directory are last-writer-wins.
- Roster: the builtin plugins whose manifest says `Headless = Yes`, which are the spec graph, Blueprint and nine language servers. The language servers are on by default; the spec graph and Blueprint are off by default, as on the desktop, and a run turns them on with `TEREDITOR_TOOLS_ENABLE` (below). An explicit stored enabled state in the state directory wins over both. Every builtin states `Yes` or `No`, a check fails on `Undeclared`, and the host runs as
  `HostKind.Headless`, so a plugin without `Yes` is refused there and so is a dependent of a refused plugin. The decision per plugin is in
  [headless-design.md](../../docs/develop/headless-design.md). The language servers that declare `Yes` are those whose installation and launch need nothing preinstalled: C#, TypeScript and JavaScript, Python, CSS, shell, PHP, C and C++, Kotlin and Java / Kotlin by IntelliJ, each downloaded on first use
  from a pinned, checksummed source on the pinned .NET SDK or Bun, or as a self-contained archive. On linux/arm64 clangd/clangd publishes no build, so the image build downloads LLVM's linux-arm64 archive (pinned URL and SHA-256 in ClangdPlugin and, as build args, in the Dockerfile; a check compares them), keeps only `bin/clangd` and `lib/clang/*/include`, and puts them in `preinstalled/clangd` beside the host; `ArtifactInstaller` copies that when its marker holds the pinned SHA-256 and never downloads the archive itself. Go, Rust, Scala, Ruby, R, PowerShell, Swift and Dart need a toolchain without a pinned
  download, declare `No` and stay desktop-only until one is pinned; `languages` prints what a build serves and `initialize` names it. Tools are `lsp_*` by default, plus `spec_*` and
  `blueprint_check` when opted in, with the schemas the desktop serves. Terminal tools (`set_title`, `workspace_*`) and Air Context are absent.
- Opt-in server. The IntelliJ server is headless-capable but not in the default roster: it exists only when `TEREDITOR_PREFETCH` or `TEREDITOR_ACCEPT_TERMS` names it.
  Without that it is absent everywhere (languages, initialize text, server list, tool list, no `accept_terms`), `.java` has no server, `.kt` goes to the Kotlin
  server, and nothing is downloaded or written. The desktop is unaffected.
- Terms. `TEREDITOR_PREFETCH` (also the first Java call) downloads the pinned archive in the background and checks its SHA-256; nothing in it runs before consent.
  Once downloaded, the full terms text from the archive, its hash and version are shown (a Java call, or `accept_terms` with only the server, in pages); before that only a
  summary and a "call again" text. The tool `accept_terms` records the user's yes when it gets the current hash and every answer, and not before the download has
  finished and its terms file matched the pinned hash. If the client supports elicitation the host asks directly with Accept and Decline (full text, else the first pages
  and a pointer to the tool); Decline, a failed prompt or a missing answer falls back to the text, asking at most once per run. `TEREDITOR_ACCEPT_TERMS` (`intellij`, or
  `intellij:<hash>`) accepts at start with the answers in the vendor's own variables and writes nothing; with `TEREDITOR_PREFETCH` too the server downloads and then
  starts. The acceptance record is written only after the archive is installed and its terms file matches the pinned hash. Kotlin stays with the Kotlin server and Java has
  none until then. Consent is the agent's word for the user's: nothing technical stops an agent from calling the tool, so the tool says plainly it needs the user's yes, and the
  image holds no acceptance. Records live in the state directory, so they last as long as it does.
- Supersession. The IntelliJ server takes over Java and Kotlin from the other servers (the Kotlin server, and the Java server where one is available) only while it works:
  its terms are accepted, it started, and it is licensed. When any of these fails, the previous server keeps serving, nothing regresses, and the answer's hint or
  message says why IntelliJ is not in use; `retry` for it tries again, and a reason is kept for five minutes so the servers do not flap. There is no setting to keep
  the old server once IntelliJ works. A choice left waiting for the user's acceptance in a window is not a failure.
- Licence. There is no trial and no sign-in without a window, and no variable that starts a trial. The server needs a license the user owns, given with the vendor's
  variables `INTELLIJ_SERVER_LICENSE`, `INTELLIJ_SERVER_LICENSE_FILE` or `JETBRAINS_LICENSE_SERVER`, or found by the server's own discovery, or the offline activation download mounted read-only at `/license` (the IntelliJ plugin's spec has the rules; the licence id and its state show in the server row's `mountedLicense`). Unlicensed, the
  unlicensed message says so plainly and the previous server answers.
- One workspace per process. A tool's optional `workspace` argument is dropped, so a path the agent knows from outside a
  container still works. Relative paths resolve against the workspace.
- The language tools accept a path inside the root either absolute or relative to it. A relative path that does not exist
  but exists under exactly one child directory is refused with that path as a suggestion, and a path outside the root is
  refused plainly. The root is stated in the initialize text and in the status answer, because an agent often cannot tell
  where the server was started.
- A host with the Blueprint tool serves its format to an agent that has no copy of the plugin's file: the tool description
  names the format in brief, a file with no controls is answered with a minimal valid example and the pointer, and asking
  for the reference returns the same text the plugin writes for desktop sessions.
- Protocol: one JSON-RPC 2.0 object per line on stdin, one per line on stdout. `initialize` carries headless instructions.
  Notifications get no reply. A line that is not JSON gets a `-32700` error with a null id. Replies to requests the server never sent are ignored.
- Requests run concurrently and answer in any order. Each reply is one line written under a lock and flushed.
  `notifications/cancelled` cancels the request with that id, which then gets no reply.
- stdout holds frames only. At start `Console.Out` is redirected to stderr, so a stray print cannot corrupt the stream. Logs go to stderr.
- End of input, SIGINT or SIGTERM: running calls get 5 s to finish, then are cancelled; plugins stop, then language servers; exit 0.
  A closed stdout counts as a stop.
- `languages` prints one line per served language server (id, name, file extensions, and a note when the server needs the user's agreement to its terms) and exits 0.
- `warm [server ...]` installs the pinned .NET SDK and the named language servers (default `csharp`) into the state directory, without a session.
  It needs network, prints nothing on stdout, exits 0 when every server is installed and 2 for an unknown server or a failed install.
  A second `warm` on a filled cache does nothing and returns at once.
- Installs write to the lifecycle log (`code-intelligence.log`): `host.runtime-install` and `host.server-install` with outcome and duration,
  `host.install-progress` messages, and `host.install-command` with the command line, exit code, duration and the last output of each installer process.
- Exit 2 with a message on stderr, before any frame, for an unknown command or argument, a workspace that is not a directory, or a
  state directory that cannot be created.

## Container image

Built from `docker/headless/Dockerfile` by `.github/workflows/container.yml`, published as `ghcr.io/commandertvis/tereditor`.

- Contents: the NativeAOT publish of this project (`NativeAot.props`: one native binary beside the `plugins` and `shell-integration` trees and the process-group helpers) on `dotnet/runtime-deps` (Ubuntu noble), with `git`, `ca-certificates` and `tzdata`.
- Why runtime-deps: the native binary still loads libicu and libssl at run time, and the .NET SDK it installs for language servers needs those, libstdc++ and zlib. That base carries exactly this set.
- Publish: always under `scripts/mise`, since Linux links with the pinned Zig against glibc 2.31, from a Linux or a macOS host.
- User and paths: uid 1000, `HOME=/cache/home`, `WORKDIR /workspace`, `VOLUME /cache`.
- Environment: `TEREDITOR_TOOLCHAINS=managed`, `TEREDITOR_MANAGED_CACHE=/cache/managed`, `TEREDITOR_STATE_DIR=/cache/state`, `TEREDITOR_ROOT=/workspace`, `DOTNET_NOLOGO=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, and git `safe.directory=*` through `GIT_CONFIG_*`.
- Entry: `ENTRYPOINT ["tereditor-headless"]`, `CMD ["mcp"]`. No wrapper, so stdin and stdout are the process's own. Run with `-i --init`.
- Exit codes are the ones above: 0 on end of input or SIGTERM, 2 on a configuration error.
- Labels: OCI source, description, licenses (`PolyForm-Shield-1.0.0`), revision and created.
- Tags: `nightly` (overwritten each run) and `sha-<short>`, as multi-arch lists over `-amd64` and `-arm64` tags. arm64 may be missing.
- No image is pushed unless `scripts/container-smoke.cs` passes on it.

## Release archive

`.github/workflows/nightly.yml` publishes the same NativeAOT output for `linux-x64` and `linux-arm64` as release assets of the `nightly` prerelease, for `scripts/headless-cloud-setup.sh` and anyone who does not use Docker.

- Assets: `tereditor-headless-nightly-linux-x64.tar.gz` and `tereditor-headless-nightly-linux-arm64.tar.gz`, and `SHA256SUMS` (one line per release asset, `sha256sum` format).
- Contents: a gzip tar of the publish directory under one folder, `tereditor-headless/`: the files of `/opt/tereditor` in the image, with the same `a+rX` modes, owner root and no `preinstalled/clangd` (the arm64 clangd stage is image-only).
- Built on a native runner per architecture, with the same publish and the same `packaged-binaries` gate as the image; the job unpacks the archive and runs `languages` and `check-config` from it. It does not gate the release; an architecture that fails is missing and the script says so.
- The setup script installs the archive into `~/.local/share/tereditor-headless/app` and records its sha256 in `archive.sha256`, so a second run with the same release downloads nothing. `TEREDITOR_HEADLESS_SHA256` pins the hash out of band; a mismatch fails and installs nothing.

## Plugins and security

This is the third host composition, beside the app's own host and the remote host, and the
[plugin security spec](../Tereditor.Plugins.Api/Security.SPEC.md#headless-tereditor) covers it.

- It is published as NativeAOT and loads no plugin code of its own: builtin plugins are compiled in.
- External plugins in `<stateDir>/plugins` are listed and refused: the isolation seam is null here. Once the isolated host half
  exists, only those whose manifest says `Headless = Yes` run, isolated as on other hosts, host half only. No UI half activates
  and no surface opens.
- No prompt can show here. Grants come only from a grants file the user writes in the state directory. An ungranted
  permissioned call is denied and written to the audit log. Neither the file nor the log exists yet.

## Tool switches and call log

- `.tereditor/mcp.toml` (`version = 1`, strict: unknown keys, wrong types and unknown operations exit 2 with `file:line: key: message`) holds `tools.disable`, `tools.allow`, `[tools.<name>]` (`enabled`, `operations` for `lsp_navigate`, `lsp_status`, `lsp_code_actions`, `lsp_rename` and `lsp_format`; removing `apply` makes a refactoring tool read-only), `[log]` and `[arms.<name>]` tables of tools and log. `tools.enable` (`spec`, `blueprint`; Blueprint brings the spec graph along) turns the two off-by-default plugins on for the run only, without writing the state directory; the spec and Blueprint tools are absent from the list and the instructions otherwise, and the instructions say how to opt in. An arm replaces each key it sets.
- Precedence: defaults, file top level, arm, environment (`TEREDITOR_ARM`, `TEREDITOR_TOOLS_DISABLE`, `TEREDITOR_TOOLS_ALLOW`, `TEREDITOR_TOOLS_ENABLE`, `TEREDITOR_LOG`, `TEREDITOR_LOG_LEVEL`, `TEREDITOR_RUNS_DIR`), flags (`--arm`, `--disable-tools`, `--enable-tools`). A selected arm the file lacks is an error.
- One decorator over the tool table drops switched-off tools (unknown tool for list and call), narrows operations in the schema and at call time, and feeds the log. The initialize instructions are generated from the offered tools.
- The log is off by default and then nothing is created in `.tereditor`. When on, `<dir>/<session>.jsonl` (default `.tereditor/runs`) gets `start`, `call` and `end` lines from a background writer, flushed per line, bounded by `max_file_kb`. It never fails a call.
- `check-config` prints the effective configuration as JSON and exits 0, or 2.

## Not built yet

`manifest`.
