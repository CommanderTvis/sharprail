# Headless SharpRail for AI research: design

Status: proposal for review, nothing here is built yet. Written 2026-11-06 against `main` at `8dc68277`.

Reading guide. Facts are marked where it matters. "Verified" means I read the code or spec named. "Assumed" means a
reasonable expectation that a slice must prove before it relies on it. Every assumption is also listed in section G.

The user-facing summary is [docs/headless.md](../headless.md). The module contract is
[src/SharpRail.Host.Headless/SPEC.md](../../src/SharpRail.Host.Headless/SPEC.md).

## A. Goals, non-goals and the experiment workflow

### Goals

- Run SharpRail with no window, no terminal and no agent of its own, serving MCP over stdio for one project.
- Ship it as a small container image. A Claude Code project adds one `.mcp.json` entry and it works.
- Configure everything for that run from TOML files in the project's `.sharprail/`, committed with the project.
- Answer one question with data: does the SharpRail LSP toolset make an agent better, faster or cheaper?
  That needs tool switches (same task, LSP on and off) and an opt-in per-call log.

### Non-goals

- No TCP port, no auth token, no remote protocol. stdio only, one container per Claude session.
- No new UI, no desktop changes, no change to the Remote host's wire protocol.
- No dynamic tool renaming. Renaming would confound the experiment.
- No statistics package. The comparison is a documented set of `jq` recipes first, a tiny `report` command later.
- No new language-server behaviour. This design only exposes the existing one.

### Experiment workflow, worked example

A research repo `acme-bench/` with one task, run twice from the same commit.

```
acme-bench/
  .mcp.arm-lsp.json
  .mcp.arm-nolsp.json
  .sharprail/
    host.toml
    language.toml
    mcp.toml
    plugins/air-context.toml
  src/ ...
```

`.mcp.arm-lsp.json`:

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "docker",
      "args": [
        "run", "-i", "--rm", "--init",
        "-v", "${PWD}:/workspace",
        "-v", "sharprail-cache:/cache",
        "-e", "SHARPRAIL_ARM=lsp",
        "ghcr.io/commandertvis/sharprail:0.1.0"
      ]
    }
  }
}
```

`.mcp.arm-nolsp.json` is identical except `SHARPRAIL_ARM=nolsp`. Both mount the same `sharprail-cache` volume, so the
language servers install once. The image tag is a fixed version, never `latest`, so both arms run the same bits.

`.sharprail/mcp.toml` (new):

```toml
version = 1

[log]
enabled = true            # opt-in, off by default
detail = "meta"           # meta | args | full

[arms.lsp]
# Nothing changes: every tool the plugins offer stays on.

[arms.nolsp]
tools.disable = ["lsp_navigate", "lsp_diagnostics", "lsp_status"]
```

Run each arm in its own checkout of the same commit, so edits do not leak between arms:

```sh
git worktree add ../bench-lsp   $COMMIT
git worktree add ../bench-nolsp $COMMIT
(cd ../bench-lsp   && claude -p "$TASK" --mcp-config .mcp.arm-lsp.json   --strict-mcp-config --output-format stream-json > ../lsp.stream.jsonl)
(cd ../bench-nolsp && claude -p "$TASK" --mcp-config .mcp.arm-nolsp.json --strict-mcp-config --output-format stream-json > ../nolsp.stream.jsonl)
```

Where results land:

- `../bench-lsp/.sharprail/runs/<session>.jsonl` and the same under `bench-nolsp`: SharpRail's per-call log.
- `../lsp.stream.jsonl`, `../nolsp.stream.jsonl`: Claude Code's own transcript with token use, turns and cost.
- The resulting diff in each worktree (`git diff $COMMIT`) and whatever test command the task defines.

Comparison starts with `jq` (section F). Claude Code's `--mcp-config` and `--strict-mcp-config` flags exist in the
Claude Code CLI. I did not run them here, so the exact flag spelling is assumed until the first end-to-end run.

Confounders the workflow must control, and the doc tells researchers:

- Tool descriptions and the MCP `instructions` text are part of the treatment. They stay as shipped in both arms.
- Claude Code's built-in Grep and Read stay on in both arms. Only SharpRail tools change.
- Cold language-server start costs wall time on the first call. Pre-warm the cache (section D) or compare after warmup.
- Model sampling varies. Run each arm several times. The log carries a `run_id` for that.

## B. Audit of the current code

### What the app is made of

All claims verified from `src/*/*.csproj` and the module specs.

| Group | Projects | Depends on UI? |
| --- | --- | --- |
| Plugin API | `SharpRail.Plugins.Api` (contract), `SharpRail.Plugins.Api.Host` (host-side seams), `SharpRail.Plugins.Api.UI` (Avalonia) | only `.Api.UI` |
| Host contract | `SharpRail.Host.Abstractions`, `SharpRail.Host.Protocol` (RPC DTOs, generated codec) | no |
| Host core | `SharpRail.Host.Core`: state, files, Git, terminals, language services, `McpServer`, `PluginRuntime`, `ProjectToml` | no. Its csproj has no Avalonia reference |
| Remote transport | `SharpRail.Host.Remote` (Web SDK): WebSocket RPC, `LoopbackServer` (terminal MCP and plugin routes), `HeadlessLanguageMcp`, standalone `Program.cs` | no |
| Client side | `SharpRail.Host.Client` (RPC client), `SharpRail.UI` (Avalonia workbench), `SharpRail.Android`, `SharpRail.Browser`, `Ghostty.Avalonia`, `SharpRail.Scintilla`, `SharpRail.Plugins.UI.Kit` | yes |
| Builtin plugins | `SharpRail.Plugins.<Name>` with a `Contract` and a `Host/` project (no Avalonia, 23 counted) and a `UI/` project (Avalonia, about 22) | Host halves no, UI halves yes |
| Language servers | `SharpRail.Plugins.Lsp.<Name>`, 18 projects, one per server, host side only | no |
| Tests | `tests/SharpRail.Checks` (executable runner), `SharpRail.Language.Integration`, `SharpRail.Browser.Checks`, plugin fixtures | n/a |

Key finding. The reorganisation the user asked for is mostly done. `SharpRail.Host.Core` already references every
plugin `Host` project and every `Lsp.*` plugin, and none of those pull Avalonia. Direct references are verified.
Transitive ones are assumed clean and slice 2 proves it with `dotnet list package --include-transitive`.
What is missing is a small executable that composes Core without Kestrel.

### How the host runs today

- Embedded in the desktop app: `App.cs` builds the services and `PluginHostSeams` in process.
- Standalone: `SharpRail.Host.Remote/Program.cs` reads `SHARPRAIL_ROOT`, `SHARPRAIL_BIND`, `SHARPRAIL_PORT`,
  `SHARPRAIL_TOKEN` (required), `SHARPRAIL_STATE_DIR`, `SHARPRAIL_BROWSER_ROOT`, `SHARPRAIL_HEADLESS_MCP=1`.
  `RemoteServer.Create` composes `HostStateStore`, `PtyTerminalService`, `PluginRuntime`, `LoopbackServer` and the RPC.
- Terminal MCP (verified in `McpRoute.cs`, `PtyTerminalService.cs`): `LoopbackServer` listens on `127.0.0.1:<random>`.
  Each terminal gets a random token and the env var `THINKRAIL_MCP_URL=http://127.0.0.1:<port>/mcp/<token>`.
  `POST /mcp/<token>` resolves the token to `(terminal, workspace)` and answers one JSON-RPC request. An unknown token is 404.
  The tool table is `workspaceTools` (4 tools) plus `PluginRuntime.McpTools(terminal, workspace)` plus `LanguageMcpTools`.
- Headless MCP route (verified in `HeadlessLanguageMcp.cs`, `LanguageChecks.cs`, `AirContextChecks.cs`): with
  `SHARPRAIL_HEADLESS_MCP=1` the standalone host also serves `POST /mcp` for one fixed root, guarded by the host bearer
  token. It offers `LanguageMcpTools` plus the enabled plugins' tools with `owner = null`. It is exercised by two checks.
  So the headless tool table already exists. Only the transport differs from what we need.
- The protocol is `McpServer.HandleAsync(JsonNode message, IReadOnlyList<McpTool> tools)`: one request in, one result out,
  stateless, no SSE. That is also the natural stdio loop body and the natural place to log and filter.

### MCP tools, registration and headless fit

| Tool | Registered in | Needs UI or terminal? | Headless |
| --- | --- | --- | --- |
| `lsp_navigate`, `lsp_diagnostics`, `lsp_status` | `LanguageMcpTools.Create` (`LanguageServices.cs`), host core, not a plugin. Empty when the catalog is empty | no | yes |
| `spec_grep`, `spec_get`, `spec_graph`, `spec_create`, `spec_update`, `spec_delete`, `spec_validate` | SpecDialect plugin, `SpecTools.cs`, `context.Tool(...)` | no | yes |
| `blueprint_check` | Blueprint plugin, `BlueprintHost.cs` | no | yes |
| `context_search`, `context_status`, `context_index` | AirContext plugin, `AirContextHost.cs` | no UI at call time, but it needs a JetBrains sign-in and accepted terms that only a terminal and the UI can grant | no, off (see below) |
| `visualize`, `visualize_read` | Visualize plugin, `VisualizeHost.cs` | the result is drawn by the UI | off |
| `workspace_create`, `workspace_delete`, `workspace_emigrate`, `set_title` | `WorkspaceMcpTools`, only added in `LoopbackServer.McpAsync` | terminal identity | not offered |
| `owned_process_report`, `owned_process_release` | Processes plugin | terminal owner | off |

Plugin tools are registered with `context.Tool(PluginTool<TParams>(...))`. `PluginRuntime.McpTools` turns them into
`McpServer.McpTool` and builds the JSON schema from the parameter type. No tool names a UI type.

Air Context stays off in the container by default. Its consent flow is user-only by design (the plugin spec says no agent
operation records consent or enables a workspace) and the sign-in opens a terminal. A research run that wants it must
mount a prepared JetBrains login and accepted state. That is out of scope for the first version and is a decision below.

### Which plugins are headless

Every plugin states the decision in its manifest: `Headless = HeadlessSupport.Yes` or `No`. The default is `Undeclared`,
which a check refuses for a builtin, so a new plugin forces a conscious choice. A headless host (`HostKind.Headless`) runs
only plugins that say `Yes`. Any other plugin is refused with "not available in headless SharpRail", and so is a plugin
that depends on it. A headless plugin still has to be enabled. `HeadlessEnabledByDefault` replaces `EnabledByDefault` on a
headless host, which keeps the spec graph and Blueprint on there while a desktop leaves them off. The roster
`BuiltinPlugins.Headless` is the builtins that declare `Yes`. No tool is added: the table stays at 11 tools.

| Plugin | Headless | Reason |
| --- | --- | --- |
| `spec-dialect` | yes, on by default | The `spec_*` tools read and edit the spec graph on disk. |
| `blueprint` | yes, on by default | `blueprint_check` renders a view without a window. It depends on `spec-dialect`, which is also headless. |
| `lsp-typescript`, `lsp-python`, `lsp-php`, `lsp-css`, `lsp-shell`, `lsp-csharp`, `lsp-clangd`, `lsp-java`, `lsp-kotlin`, `lsp-ruby`, `lsp-go`, `lsp-rust`, `lsp-scala`, `lsp-r`, `lsp-powershell`, `lsp-swift`, `lsp-dart` (17) | yes, on by default | Language servers are child processes that serve the `lsp_*` tools. Managed ones install into the cache. Swift and Dart use the toolchain of the container and need it installed. |
| `lsp-intellij` | no | It needs a JetBrains license and an agreement accepted in the UI, and it replaces the Java and Kotlin servers. See the decisions. |
| `claude-code`, `codex`, `omp`, `pi`, `acp` | no | They launch an agent in a terminal or chat pane. Headless is the tool server an agent calls. |
| `air-gateway` | no | It starts Codex and Claude Code through Air Gateway per launch. |
| `air-context` | no | Sign-in and consent are user-only and the indexing is cloud processing. |
| `providers` | no | It is a settings page for the API keys of Pi and omp. |
| `github` | no | Pull request panels and a `gh` sign-in. |
| `discord` | no | Rich Presence of a desktop session. |
| `tailscale` | no | Pairs clients of a desktop host on a tailnet. |
| `branch-graph` | no | A side tool that draws a graph. |
| `epub` | no | A file viewer. |
| `visualize` | no | A drawing surface that only a UI shows. |
| `processes` | no | A process list for a window. Its two tools would also change the tool table. See the decisions. |
| `env`, `env-mise`, `env-direnv`, `env-python`, `env-javascript`, `env-jvm` | no | They shape the shell of a terminal and agent. Language servers do not read them. See the decisions. |

### Decisions needed

- `lsp-intellij` is `no`. Flipping it needs a way to accept the JetBrains agreement without a UI, for example a prepared settings file, and a decision that the headless Java and Kotlin servers are IntelliJ.
- `processes` is `no`. Flipping it adds `owned_process_report` and `owned_process_release`, which makes 13 tools, and needs a terminal owner that headless does not have.
- The `env*` plugins are `no`. Flipping them would make language servers see `JAVA_HOME`, the venv or `mise` tools of the workspace. That needs the language server launch to read the merged environment, which it does not do today.
- `air-context` is `no`. Flipping it needs a prepared JetBrains login and accepted consent mounted into the container.
- `lsp-swift` and `lsp-dart` are `yes` but have no managed install. They work only when the container image carries the toolchain.

### Settings audit

"Documented" says where a reader finds the key today. No place lists every key.

| Setting | Stored now | Scope | Documented |
| --- | --- | --- | --- |
| Appearance: `Theme`, `ThemeMode`, `SystemLight`, `SystemDark`, `FileTypeIcons*`, line widths, `WindowDecorations`, `ActiveKeymap` | `state.json` `HostSettings` | host (shared by clients) | XML comments in `Host.Abstractions/HostState.cs`, Settings UI |
| Terminal: `TerminalReplayKb`, `TerminalKeepAfterExit`, `TerminalUndoCloseSeconds`, `ShellIntegrationEnabled`, bell, notification flags, `TerminalOptionAsAlt` | `state.json` | host | same |
| `SettleIdleDays` | `state.json` | host | same |
| `LspEnabled` | `state.json` | host | XML comment; `Language/SPEC.md` |
| `JavaKotlinProvider` | `state.json` | host | `Language/SPEC.md` |
| `LanguageServers` (per-server launcher: `command`, `arguments`, `allowPath`), JSON string | `state.json` | host | `Language/SPEC.md`, `LanguageServerOptions` comment |
| `LanguageTools` (go, node, java paths), JSON string | `state.json` | host | `Language/SPEC.md` |
| `LanguageLimits` (`idleMinutes`, `memoryBudgetMb`, `caps`), JSON string | `state.json` | host | `Language/SPEC.md` "limits" |
| Per-workspace server configuration (init options, settings, autostart) | `state.json` `WorkspaceLanguages`, `ProjectLanguageAutostart` | workspace / project | `Language/SPEC.md`, `Language/CONFIGURATION.md` |
| Shared server options (`autoStart`, `arguments`, `buildTool`, `compilationDatabasePath`, `compilerFlags`, `includePaths`, `initializationOptions`, `settings`) | `<project>/.sharprail/language.toml`, `version = 1`, `[servers.<id>]` | project | `Language/SPEC.md` "Project language file" |
| Hidden servers per project | `state.json` `ProjectLanguageHidden` | user, per project | `HostState.cs` comment |
| Instruction sections off | `<project>/.sharprail/instructions.toml`, `version = 1`, `disabled = [...]` | project | `Plugins.SPEC.md` |
| Cloned ignored paths | `<project>/.sharprail/workspace.toml`, `[clone] include` | project | `Workspaces.SPEC.md` |
| Plugin enabled and settings | `state.json` `PluginSettings[<id>]` (JSON object, `enabled` plus the plugin's own record) | host | contract XML comments only |
| Plugin records: `CodexSettings`, `ClaudeCodeSettings`, `OmpSettings`, `PiSettings`, `AcpSettings`, `AirSettings`, `DiscordSettings`, `ProcessesSettings` (the other 11 settings records are empty) | `state.json` | host | XML comments, plugin `SPEC.md` |
| Projects, recents, labels, workspaces, presets, keymaps | `state.json` | host | `HostState.SPEC.md` |
| Managed server pins (version, URL, SHA-256 per platform) | code: each `Lsp.*` plugin's spec, `ManagedArtifacts` | build | plugin `SPEC.md`, `Language/SPEC.md` |
| Env vars: `SHARPRAIL_ROOT`, `_BIND`, `_PORT`, `_TOKEN`, `_STATE_DIR`, `_BROWSER_ROOT`, `_HEADLESS_MCP`, `_LSP_DISABLE`, `_GH_OFFLINE`, `_OMP_HOME`, `_DISCORD_IPC_DIR`, `_WINDOW_DECORATIONS`, `_PERFORMANCE`, `_PROFILE`, `_REMOTE` | process env | process | `Host.Remote/SPEC.md` (first seven), `LINUX.md`; rest code only |
| Command-line flags | `tests/SharpRail.Checks` modes only. The host and app take no flags | none | `docs/develop/agent-reference.md` |

State is JSON via source-generated `HostJson`, persisted atomically. A memory-only store (no directory) turns language
services off (verified: `DirectoryPath is not null` in `LanguageServices.cs`), so a headless run needs a state directory.

Gaps this audit exposes: no key list exists anywhere, JSON-in-a-string settings (`LanguageServers`, `LanguageTools`,
`LanguageLimits`) are opaque, there are no CLI flags, and the `PATH executable` opt-in (`AllowPath`) is a per-server
host setting with no global off switch.

## C. Architecture for the headless entrypoint

### The executable

New project `src/SharpRail.Host.Headless`, assembly `sharprail-headless`, a plain console app
(`Microsoft.NET.Sdk`, not Web). It references only `SharpRail.Host.Core`. No Kestrel, no Avalonia, no Skia.
The image entrypoint is `sharprail-headless mcp`.

Subcommands:

| Command | Purpose |
| --- | --- |
| `mcp` (default) | stdio MCP server for `/workspace` |
| `warm` | install the pinned servers named in config or flags into the cache, then exit (needs network) |
| `check-config` | parse and validate `.sharprail/*.toml`, print precise errors, exit 1 on any |
| `schema` | print the JSON Schema for all config files (slice 6) |
| `manifest` | print version, pinned server versions and hashes as JSON |
| `report` | compare two run logs (slice 5, optional) |

### Composition (assumed to work, proven in slice 1)

`mcp` does what `RemoteServer.Create` does minus the web server:

1. `new HostStateStore(stateDir)` with `stateDir` on tmpfs or the cache (section D).
2. `new PluginRuntime(seams)` with `Builtins` limited to the plugins config allows. `BuiltinPlugins.LanguageServerEntries`
   already exists and `LanguageChecks.cs` uses it, which shows a roster can be restricted this way.
   `Terminals = null` and `PublicBaseUrl` unset. A plugin that asks for the loopback URL fails its activation.
   That should be checked for each default plugin. Assumed, plugins in the default roster do not ask.
3. `runtime.Start()`.
4. Tools: `LanguageMcpTools.Create(state, "/workspace")` plus `runtime.McpTools(null, "/workspace")`.
5. Loop: read one JSON-RPC message per line from stdin, `McpServer.HandleAsync`, write one line to stdout. A `null` body
   (notification) writes nothing. This matches `HeadlessLanguageMcp` exactly, with a different transport.
6. On stdin EOF or SIGTERM: stop reading, finish in-flight calls (cap 5 s), `runtime.DisposeAsync()`,
   `state.StopLanguagesAsync()`, exit 0.

Rules that keep stdio clean:

- stdout carries protocol frames only. At startup, swap `Console.Out` for stderr and keep the original stdout stream
  for frames. Any stray `Console.WriteLine` in a plugin would otherwise corrupt the stream.
- Logs go to stderr and, when enabled, to the run log.
- The MCP `initialize` reply carries `instructions`. Today it is a `private const` in `McpServer.cs` that mentions
  `set_title` and `workspace_delete`, which do not exist headless. Slice 1 adds an optional parameter to `HandleAsync`
  (additive) so the headless host can send accurate text without those names.

### Workspace and terminal degradation

- One implicit workspace: the path `/workspace`. Calls to `lsp_*` and plugin tools that accept `workspace` accept only
  that path or none. Anything else is refused with a clear message (today `AirContextHost` already refuses unknown
  workspaces and lists known ones).
- `PluginToolContext(cwd, owner?.WorkspaceId ?? cwd, owner)` is verified to accept `owner = null`, so no tool needs a
  terminal. `WorkspaceId` falls back to the path.
- No per-terminal tokens. The trust boundary is the container: whoever can write to stdin is the Claude Code process.
  There is no bearer token and no network listener. This removes the "host-level access" caveat that
  `HeadlessLanguageMcp` has over HTTP.
- The terminal-only tools (`workspace_*`, `set_title`) are simply absent because `WorkspaceMcpTools` is not composed.

### What the "deep reorganise" must be

Smaller than it sounds, because the split exists. In order of value to the main use case:

1. New `SharpRail.Host.Headless` project and its checks. No moves.
2. Config surface (section E): move the three TOML readers (`ProjectLanguageFile`, instructions, workspace clone) behind
   one `ProjectConfig` reader. They already share `ProjectToml`. Keep their files and keys.
3. Move headless-relevant composition out of `RemoteServer.Create` into a Core `HostComposition` that both Remote and
   Headless call, so the two cannot drift. Remote keeps the web parts.
4. Optional later: a `Host.Core.Plugins` split so Core stops referencing Discord, Tailscale and agent launchers. Pure size
   win, not needed for the experiment, so it comes last.

Public API boundaries. `Host.Abstractions` and `Plugins.Api*` use `PublicApiAnalyzers`, so any new public member must
be added to `PublicAPI.Unshipped.txt`. Slice 1 touches `McpServer` (Core, internal analyzer-free) and adds one public type
in Core if `HostComposition` is public. Keep it `internal` with `InternalsVisibleTo` for the new project to avoid an
API-boundary change. Check the Core csproj for the existing `InternalsVisibleTo` list when starting slice 1.

## D. Container

### Dockerfile design

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:<pinned-digest> AS base
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
# Prebuilt on the CI runner for the target RID, copied in. No SDK in the image.
ARG TARGETARCH
COPY publish/linux-${TARGETARCH}/ /opt/sharprail/
RUN useradd --uid 10001 --create-home sharprail && mkdir -p /cache /workspace && chown sharprail /cache
USER sharprail
ENV HOME=/home/sharprail \
    SHARPRAIL_ROOT=/workspace \
    SHARPRAIL_CACHE_DIR=/cache \
    SHARPRAIL_STATE_DIR=/tmp/sharprail-state \
    SHARPRAIL_OFFLINE=0 \
    SHARPRAIL_TOOLCHAINS=managed \
    GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=safe.directory GIT_CONFIG_VALUE_0=/workspace \
    DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME ["/cache"]
WORKDIR /workspace
ENTRYPOINT ["/opt/sharprail/sharprail-headless"]
CMD ["mcp"]
```

Choices and why:

- Base `dotnet/runtime` (not `aspnet`, not `sdk`): the headless app has no web dependency. Framework-dependent publish
  keeps the image small. Image build is a copy, so it is fast and the same on both architectures.
- git is needed (the repo requires Git 2.36+ on PATH per `LINUX.md`). The distro Git version in the base must be checked in
  slice 4. Assumed fine on current Debian.
- Non-root user. `safe.directory` is set through `GIT_CONFIG_*` because the bind-mounted checkout is usually owned by a
  different uid. Assumed this is enough. If the host user's uid differs and the run must write files, the example `.mcp.json`
  can add `"--user", "${SHARPRAIL_UID}:${SHARPRAIL_GID}"` and the researcher exports those. Docker on macOS maps ownership itself.
- Volumes: `/workspace` (bind mount of the project, read-write because the agent edits files, and the run log is written to
  `/workspace/.sharprail/runs/`) and `/cache` (named volume).
- `/cache` holds `language/` (managed server installs, shared caches such as gopls). The state directory (`state.json`,
  per-run) stays out of the shared volume on tmpfs, because two containers on one `state.json` are last-writer-wins
  (verified, `Host.Remote/SPEC.md` "Decisions"). Installs are safe to share: they are serialised by a filesystem lease
  across host processes (verified, `Language/SPEC.md` "Provisioning").
  Today the language directory is `<stateDir>/language`. Slice 1 adds `SHARPRAIL_CACHE_DIR` as an additive override.
  Fallback with zero code: symlink `/tmp/sharprail-state/language` to `/cache/language` in an init step. Assumed workable.
- Env vars are the container-level layer of config precedence (section E): `SHARPRAIL_ARM`, `SHARPRAIL_OFFLINE`,
  `SHARPRAIL_LOG` and any key via `SHARPRAIL_SET__host.language.allow_path=false` style overrides.
- Signals and stop. `docker run -i --init` puts `tini` as PID 1, forwards SIGTERM, and reaps language-server children.
  The app also registers `PosixSignalRegistration` for SIGTERM and SIGINT. EOF on stdin is the normal stop. On any of
  these it runs the shutdown in step 6 above, stops language servers (their own watchdog also ends them when the host dies,
  verified in the `--lsp-limits` description) and exits 0.
- Entry and exit codes: 0 clean, 2 configuration error (printed to stderr before any frame, never a half-started server),
  1 other failure.
- Healthcheck: none. A stdio server has no port. The client sees `initialize`.

### Runtimes: what the app downloads today and what is missing

Decision: one slim image. The runtimes the servers need are also provisioned on demand into the cache volume, pinned by
exact version with a SHA-256 per platform artifact, verified before extraction, like the managed servers.

What the code does today (verified by reading `LanguageServers*.cs`, `ManagedArtifacts.cs` and the plugins):

- Servers are downloaded and verified. `ProvisionAsync` calls `ArtifactInstaller.InstallAsync` for archives. It checks the
  64 hex SHA-256 first, downloads from an allow-listed host with `AllowAutoRedirect = false`, hashes the file, extracts
  to a `.partial-<guid>` directory, writes a `.sharprail-artifact.json` marker, then renames. Formats it can unpack are
  `gz`, `tar.gz` and (default branch) `zip`. NuGet and npm servers have their own pinned paths.
- Runtimes are never downloaded. Every runtime is found, not installed: `LanguageServers.ProbeTool` looks at the host-wide
  tool override (`HostSettings.LanguageTools`), then the tool's home variable (`DOTNET_ROOT`, `JAVA_HOME`), then the
  hardened `PATH` (`LanguageExecutables.FindOnPath`, absolute entries only, never inside the workspace). A missing one is an
  `IOException` that tells the user to install it ("Needs node ...").
- Per server (verified in the plugin files):

| Server | Runtime need today | How it finds it |
| --- | --- | --- |
| C# (Roslyn 5.13.0-1.26507.4) | .NET SDK. `dotnet tool install` at install time, and the server needs an SDK to load MSBuild projects | `Dependencies = dotnet`, `HomeVariable DOTNET_ROOT`; `ConfigureLaunch` sets `DOTNET_ROOT` and prepends it to `PATH` from `FindDotnet()` |
| TypeScript, Python (Pyright 1.1.414), PHP, CSS, Shell | Node.js 18+ and npm (`npm ci --ignore-scripts` from an embedded lockfile, then `node <package>/<file>`) | `RequireTool("node")`, `RequireTool("npm")` through `ProbeTool` |
| Java (JDT LS 1.61.0) | JDK 21+ to run the server and for the project's Maven/Gradle import | `RequireTool("java")`, `HomeVariable JAVA_HOME`; `ProjectJava.cs` also reads `JAVA_HOME` and SDKMAN |
| Kotlin LSP 263.6379.0, IntelliJ server 263.7084.0 | not read: whether they bundle a JRE (assumed yes, since both are JetBrains archives with no `java` dependency declared) | `Pinning.Archive` / `None` with the provider's own launcher |
| clangd, rust-analyzer | none | pinned archive |
| Go, Ruby, R, PowerShell, Scala, Swift, Dart | the language's own tool (`go`, `gem`, `Rscript`, `pwsh`, `cs`, SDK) | `RequireTool` |
| Python itself | not needed to start Pyright. Needed for import resolution of the project's packages (`python.pythonPath` setting) | the user's setting or PATH |

So the only runtime downloads are the servers' own archives. No runtime has a pinned entry today. All of them are new.

#### Proposed pinned runtimes

Same shape as a server artifact: id, exact version, SHA-256 per platform, source, allowed hosts. Only linux x64 and arm64
are needed for the container. The table is what I propose. Sources are from my knowledge of the vendors' download layouts.
I did not fetch them here, so URLs, availability on both architectures and published checksums must be confirmed at pin time.

| Runtime | Needed by | Proposed source | Publisher checksum | License | amd64 / arm64 |
| --- | --- | --- | --- | --- | --- |
| .NET SDK (a pinned 10.0.x, plus the project's `global.json` version when it differs) | C# | `builds.dotnet.microsoft.com/dotnet/Sdk/<v>/dotnet-sdk-<v>-linux-{x64,arm64}.tar.gz` | SHA-512 in the release's `releases.json` | MIT | both |
| Node.js LTS with npm | TypeScript, Python, PHP, CSS, Shell | `nodejs.org/dist/v<v>/node-v<v>-linux-{x64,arm64}.tar.gz` | `SHASUMS256.txt` (SHA-256, signed) | MIT, plus bundled third-party notices | both |
| Eclipse Temurin JDK 21 | Java, Scala, Gradle and Maven projects | `github.com/adoptium/temurin21-binaries/releases/download/jdk-<v>/OpenJDK21U-jdk_{x64,aarch64}_linux_hotspot_<v>.tar.gz` | `.sha256.txt` beside each file | GPLv2 with Classpath Exception | both |
| Python (python-build-standalone, `install_only`) | Pyright import resolution, project tests | `github.com/astral-sh/python-build-standalone/releases/download/<date>/cpython-<v>+<date>-{x86_64,aarch64}-unknown-linux-gnu-install_only.tar.gz` | `SHA256SUMS` | PSF license, with bundled components under their own | both |

All four ship as `tar.gz`, which `ArtifactInstaller` already unpacks. A `tar.xz` or `tar.zst` source would need new
extraction code, so the table picks `tar.gz` variants. Pin script: a small repo script reads the publisher checksum, checks
it, and writes our SHA-256 into the table with `Source` recording where the publisher hash came from, the way server
artifacts do today. A .NET SDK for every `global.json` is not possible. The catalog holds a small set of exact SDK versions
and a project asks for one by name. A project whose `global.json` names another version gets a clear error that lists the
pinned ones (and `rollForward` in `global.json` often helps). This is a decision for the user below.

#### Code needed

1. `ManagedRuntimes`, next to `ManagedArtifacts`: the pin table above and `Find(executable)`. It reuses
   `ArtifactInstaller.InstallAsync` and the per-server install lock, with `runtimes/<id>/<version>-<platform>` under the cache,
   a `.ready` marker, and a host allow-list per runtime (`nodejs.org`, `builds.dotnet.microsoft.com`, `github.com` and
   `objects.githubusercontent.com` for the redirect hop). Estimated 300 to 400 lines plus the pins.
2. Resolution order in `ProbeTool`: tool override, then managed runtime (new), then home variable, then PATH. A policy
   `[toolchains] source = "managed" | "path" | "managed-then-path"` (host.toml). The headless default is `managed`: the host PATH
   and home variables are not consulted at all. The desktop default stays `path` so nothing changes for users.
3. `Needs(...)` stays the error for `path` mode. In `managed` mode a missing runtime is installed on demand during the first
   tool call (inside the 60 s start wait, so the first call may answer `initializing`) or by `warm`.
4. A per-run environment for every server process: `PATH` is the managed `bin` directories followed by a fixed minimal
   system path (`/usr/bin:/bin` for `git`), never the inherited one. Plus `DOTNET_ROOT`, `DOTNET_CLI_HOME`,
   `DOTNET_NOLOGO`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `NUGET_PACKAGES=/cache/nuget`, `JAVA_HOME`, `npm_config_cache`,
   `npm_config_update_notifier=false`, `PYTHONNOUSERSITE`, all under `/cache`. Today `ConfigureLaunch` in the C# plugin sets
   `DOTNET_ROOT` and `PATH` from `FindDotnet()`, so it needs no change once `FindDotnet` can return the managed SDK.
   The JDT plugin and `ProjectJava.cs` get the same managed `JAVA_HOME` hook. A small `ILanguageServerInstallation` addition
   (`ManagedRuntimeEnvironment`) would carry this to plugins, and `PublicAPI.Unshipped.txt` in `Plugins.Api.Host` records it.
5. `warm` also installs the runtimes the chosen servers need (sections below).

#### What a project's own build needs

Roslyn loads the project's solution and needs restore results (`project.assets.json`). `lsp_diagnostics` and cross-project
`references` answer `initializing` or wrong until restore finishes (verified in the `--lsp-on-demand` description).
In the container, `dotnet restore` uses the managed SDK and the cache volume (`NUGET_PACKAGES=/cache/nuget`), so a warm cache
makes restores offline-capable. Roslyn already asks for restores itself (`OnMessage` in the C# plugin). `warm --restore`
runs `dotnet restore` for the solution named in `language.toml` or found at the root. It executes the researcher's own
MSBuild targets, so it is opt-in and documented as such.

### Size estimates

Not measured. The repo has no container build and the Linux packaging is an AppImage whose size is not in the docs I read.
Rough expectations, replaced by measurements in the container slice:

| Part | Where it lives | Estimate |
| --- | --- | --- |
| `dotnet/runtime:10.0` base | image | about 80 to 90 MB compressed |
| git and certificates | image | about 30 MB |
| `sharprail-headless`, framework-dependent, Core plus all plugin Host halves, no UI | image | about 25 to 40 MB |
| Image total (one slim image) | registry | about 150 MB |
| .NET SDK | cache volume | about 220 MB download, about 500 MB on disk |
| Node.js with npm | cache volume | about 30 MB download, about 200 MB on disk |
| Temurin JDK 21 | cache volume | about 200 MB download, about 330 MB on disk |
| Python standalone | cache volume | about 40 MB download, about 120 MB on disk |
| Roslyn server (runtime pack) | cache volume | about 100 MB (not measured) |
| npm servers | cache volume | about 30 to 100 MB each (not measured) |

The image stays small because nothing heavy ships in it. The cache volume reaches about 1 GB for a C# run and can reach
2 GB with all four runtimes. This is a one-time cost per volume, which is why the volume is shared across arms.

### Multi-arch and CI publishing

- Build `dotnet publish -r linux-x64` and `-r linux-arm64` on CI. The repo already has native arm64 CI runners
  (`LINUX.md`), so each architecture is published on its own runner and no emulation is needed. The headless app has no
  native libraries, so cross-publishing from one runner is also possible. Assumed true, verify in slice 4.
- New workflow `.github/workflows/container.yml` (outline, not final):
  1. Trigger: `workflow_dispatch`, tag `v*`, and nightly after CI is green on `main`.
  2. Job `publish` on each of `ubuntu-latest` and the arm64 runner: checkout, `scripts/mise run` publish step, build the
     image with `docker buildx build --platform linux/<arch> --push --provenance=true --sbom=true -t ghcr.io/commandertvis/sharprail:sha-<short>-<arch>`.
  3. Job `manifest`: `docker buildx imagetools create` joins `-amd64` and `-arm64` into one multi-arch tag.
  4. Login uses `GITHUB_TOKEN` with `packages: write`. No other secret.
  5. Smoke test before tagging (headless, no GUI): run the image with a fixture workspace, send `initialize` and
     `tools/list`, assert the tool names. Runs on both architectures.
- Tags: `sha-<short>` always, `nightly` moving, `vX.Y.Z` and `latest` only for a release tag. There is one image and no variant tags.
  Research docs tell people to pin a `vX.Y.Z` or a digest.
- Version mapping: the app version equals the repo version stamped at publish. The image carries OCI labels
  `org.opencontainers.image.revision` (commit) and `.version`. Pinned server versions are in code, so one image
  digest fixes one set of server versions. `sharprail-headless manifest` prints them.
- The workflow follows the repo rules: one CI attempt, no reruns, whoever pushes it watches the run.

### Reproducibility and offline mode

- Managed servers are pinned. Archive servers have an exact version and a SHA-256 per platform artifact, verified before
  extraction (verified, `ManagedArtifacts.cs` `Pinning.Archive`). npm servers use an embedded lockfile with integrity
  hashes (`Pinning.Lockfile`), NuGet servers SHA-512 per package, Go servers `go.sum`. `Pinning.Partial` and `None`
  exist for some servers (Swift and Dart follow the SDK). The doc lists which servers are fully pinned, from the plugins' specs.
- Managed runtimes (new, section above) are pinned the same way. With `source = "managed"` the whole chain from the image
  digest to the language server and its runtime is pinned. `sharprail-headless manifest` prints every server and runtime
  version with its SHA-256, and the run log's `start` event records it.
- PATH override. `LanguageServerOptions.AllowPath` is per server and defaults to false (verified). For the container the
  doc requires a hard global switch: `host.toml` `[language] allow_path = false` (default in headless, and a project file
  cannot enable it unless `SHARPRAIL_ALLOW_PATH=1` is set in the container env). Needs a small change in
  `LanguageServers.Resolution.cs`. If `allow_path` is on, the run log records it in the header. `source = "managed"` also
  stops PATH and home variable lookups for runtimes.
- Pre-warm: `docker run --rm -v sharprail-cache:/cache -v "$PWD":/workspace ghcr.io/commandertvis/sharprail:<v> warm`
  reads `host.toml` and `language.toml`, resolves which servers will run, and downloads those servers and the runtimes
  they need into `/cache`. It needs network and exits nonzero on any hash mismatch. `warm --restore` also fills
  `/cache/nuget` for a C# solution.
- Offline rerun: `--network none` plus `SHARPRAIL_OFFLINE=1` (or `[host] offline = true`). Offline mode refuses any download
  with an error naming the missing artifact and the `warm` command. Needed for offline: the cache volume warmed by the same
  image version, and the project's own dependencies (NuGet cache, `node_modules`, Gradle cache) present in `/workspace`
  or `/cache`. Servers that resolve project dependencies on start (Roslyn, JDT, Metals) do not work offline without
  them. Assumed, since the specs say they need the real prerequisites.
- Mutable per-workspace server state lives under `workspaces/<root-hash>` in the cache. For clean runs the arm scripts
  start from a fresh copy of the cache, or run `docker volume` snapshots. The doc says this. Indexes (gopls, clangd) can
  make a second run faster than the first, which is itself a confound the log header records (`cache_warm = true`).

## E. Configuration surface

### Files and precedence

One TOML file per area, in the project's `.sharprail/`. Existing files keep every key and meaning.

| File | Status | Area |
| --- | --- | --- |
| `language.toml` | exists, `version = 1` | server choices and options |
| `instructions.toml` | exists, `version = 1` | instruction sections off |
| `workspace.toml` | exists, `version = 1` | cloned ignored paths |
| `host.toml` | new | host behaviour and language limits |
| `mcp.toml` | new | tool switches, arms, run log |
| `plugins/<plugin-id>.toml` | new | each plugin's enabled flag and settings |

Precedence, lowest to highest:

1. Built-in defaults.
2. State file (`state.json`). In the container it is per-run and starts empty, so this layer is inert. Desktop users keep it.
3. Project files in `.sharprail/` (read from `/workspace`).
4. Container environment: `SHARPRAIL_ARM`, `SHARPRAIL_OFFLINE`, `SHARPRAIL_TOOLCHAINS`, `SHARPRAIL_LOG`, `SHARPRAIL_ALLOW_PATH`, and the generic
   `SHARPRAIL_SET__<file>.<dotted.key>=<toml value>`.
5. Command-line flags to `mcp`: `--arm`, `--offline`, `--log`, `--set file.key=value`.

Why env beats the project file: the harness owner pins experiment variables from outside without editing committed files.

This is one deliberate difference from today's rule for `language.toml` ("a key stored in host state for the workspace wins
over the file"). With layer 2 empty in the container, that rule holds unchanged and no existing behaviour moves.
There is no user-level layer in the container. A desktop `~/.sharprail/config/*.toml` layer is possible later and is not
part of this design.

### Validation

- Every file has `version = 1` (integer, required, as `ProjectToml.VersionOf` already reads it). A different version is an error.
- Parse and type errors name the file, key path and line: `host.toml:7: language.idle_minutes must be an integer from 0 to 1440, got "ten"`.
- Unknown keys: an error in `check-config` and a warning on stderr plus a log header entry in `mcp`, never a startup failure.
  This matches the existing tolerance: today unknown keys in `workspace.toml` and `instructions.toml` are kept and
  unknown servers in `language.toml` are ignored with a diagnostic (verified in the specs). One new rule: with
  `[strict] = true` in `host.toml`, unknown keys fail the start. Research runs should set it.
- Invalid value in a file leaves that file's contribution out and says so, never half-applies it. The same as `language.toml` today.
- Size limit 64 KiB per file (`ProjectToml.MaximumBytes`, verified), applied to all files.
- `mcp` prints all diagnostics to stderr before serving, and exits 2 only for `strict = true` or an unreadable `/workspace`.

### Schema: `host.toml`

```toml
version = 1
strict = false

[host]
offline = false              # refuse downloads
state_dir = ""               # advanced; default from env

[language]
enabled = true               # HostSettings.LspEnabled
allow_path = false           # global off switch for PATH executables

[toolchains]
source = "managed"           # managed | path | managed-then-path (headless default: managed)
dotnet = "10.0.x"            # exact pinned SDK version; must be in the image's catalog
node = ""                    # "" = the catalog's LTS pin
java = ""
python = ""
java_kotlin_provider = ""    # "", "standard", "intellij"
idle_minutes = 10            # LanguageLimits.idleMinutes
memory_budget_mb = 0         # 0 = 30% of memory
server_caps = { "*" = 4 }    # LanguageLimits.caps

[language.tools]             # LanguageTools: executable path overrides
node = ""
java = ""
go = ""

[language.launchers.pyright] # LanguageServers[id]: only valid when allow_path or command set
command = ""
arguments = []
allow_path = false           # overridden to false when [language].allow_path is false

[plugins]
roster = "research"          # "research" (default headless), "all", or an explicit list in plugins = [...]
```

`[ui]` keys (theme, line widths, terminal, bell, keymap, settle days and the other `HostSettings` UI members) are accepted
and ignored headless. They are listed in the schema with `scope = "ui"` so that the same file configures the desktop
later. Their key names follow snake_case of the property.

### Schema: `mcp.toml`

```toml
version = 1

[log]
enabled = false              # opt-in
detail = "meta"              # meta | args | full
dir = ".sharprail/runs"      # relative to /workspace
run_id = ""                  # free text label copied to every event

[tools]
disable = []                 # tool names removed from tools/list and calls
enable_only = []             # if non-empty, only these remain (allowlist)
groups = { lsp = ["lsp_navigate", "lsp_diagnostics", "lsp_status"] }

[tools.lsp_navigate]
operations = []              # if non-empty, only these operations; schema enum narrows
[tools.lsp_diagnostics]
scopes = []

[arms.<name>]                # selected by --arm / SHARPRAIL_ARM; overlays [tools] and [log]
tools.disable = ["@lsp"]     # "@group" expands a group
```

An unknown tool name in `disable` is a warning, not an error, because plugins can be off.

### Schema: `plugins/<id>.toml`

Every plugin accepts `enabled = true|false` (the `explicit PluginSettings[id].enabled` flag the reconciler reads, verified in
`Plugins.SPEC.md`). Other keys are the fields of the plugin's settings record, snake_case. Proposed key names from the
records I read:

| Plugin id | Keys |
| --- | --- |
| `lsp-<server>` (18 files) | `enabled` |
| `spec-dialect`, `blueprint` | `enabled` |
| `air-context` | `enabled` (default false). Consent and sign-in are not settable here, by design |
| `visualize`, `processes` | `enabled`; `processes`: `stop_agent_children`, `show_all_projects` |
| `claude-code` | `enabled`, `command`, `model`, `effort`, `disable_agent_view`, `append_system_prompt`, `permission_mode`, `advisor`, `chrome`, `mode` |
| `codex` | `enabled`, `command`, `model`, `effort`, `append_system_prompt`, `permission_mode`, `ide_context`, `mcp`, `shared_daemon` |
| `omp` | `enabled`, `command`, `model`, `approval_mode`, `advisor`, `mcp`, `own_code_intelligence` |
| `pi` | `enabled`, `command`, `model`, `thinking` |
| `acp` | `enabled`, `default_agent`, plus `[[agents]]` for `AcpAgentEntry` (`id`, `name`, `command`, `args`, `env`, `cwd`, `origin`) |
| `air-gateway` | `enabled`, `source`, `claude_code`, `codex`, `omp` |
| `discord` | `enabled`, `application_id`, `blocked_project_ids`, `share_file_name` |
| `providers`, `github`, `tailscale`, `epub`, `branch-graph`, `env*`, | `enabled` only (their settings records are empty) |

Exact plugin ids come from each plugin's `*Plugin.Id` constant. The ids above are from file names and the
`BuiltinPlugins` list, and the generator in slice 6 reads the real ones.
Settings of agent-launcher plugins make no sense headless. They are in the schema so the desktop can use the same files.

### Full list of settings that must be reachable

- Host: all of `HostSettings` (section B). Headless-relevant: `language.enabled`, `java_kotlin_provider`,
  `language.launchers`, `language.tools`, `language.idle_minutes`, `language.memory_budget_mb`, `language.server_caps`.
  UI-only members are accepted but ignored.
- Language: every `language.toml` key (unchanged). Not reachable from files today, and kept host-only on purpose:
  `installationPath`, `projectJdk`, `useForJavaAndKotlin`, launcher command overrides in `language.toml` (they stay in
  `host.toml`), terms consent. IntelliJ's terms are never settable from config.
- Plugins: the table above, all of them.
- Run control: `mcp.toml` and the env/flags layer.

### Publishing the schema and generating the docs

Goal: a setting that has no documentation cannot exist. Mechanism:

1. A `[ConfigKey]` attribute (Core, internal) on settings properties and on the typed `language.toml` model:
   `[ConfigKey("language.idle_minutes", Doc = "...", Default = "10", Range = "0..1440", Scope = ConfigScope.Host)]`.
   For existing records with XML comments, the source generator can read the `<summary>` text as the doc. That keeps
   one source of truth and avoids duplicating prose. Plugin settings records get the attribute in their contract projects.
2. A registry `ConfigRegistry` built from the attributes by a source generator (reflection-free, same style as `HostJson`).
   It feeds three consumers: the TOML loader (validation, types, ranges), `sharprail-headless schema` (JSON Schema, draft 2020-12, one per file), and
   a Markdown generator that writes `docs/reference/config.md`.
3. A Checks mode `--config-docs` that fails when: a property in a settings record has no `[ConfigKey]` and is not marked
   `[ConfigIgnore("reason")]`, a key has no doc text, the generated Markdown or JSON Schema differs from the committed copy
   (so a PR must regenerate), or a plugin in `BuiltinPlugins.All` lacks a `plugins/<id>.toml` schema entry.
4. The JSON Schema is committed under `docs/reference/schema/*.json` and published in the image at `/opt/sharprail/schema/`,
   so editors get completion with the Even Better TOML `#:schema` comment.

Existing `language.toml` options are already a typed catalog (`configurationSchema` per server, verified), so the language
file reuses that catalog for its schema and only the outer shape is new.

## F. Instrumentation (opt-in)

### Where it hooks

One decorator over the tool list in the headless app. For each `McpTool` it wraps `Call` and handles `tools/list` filtering. This needs no
change to Core's `McpServer`. Because every tool call from Claude Code goes through the table the host builds, nothing is missed.
Off means the decorator is not installed at all (no timing, no I/O).

### Events (JSON Lines, one object per line, UTF-8, append only, flushed per event)

Common fields: `v` (schema version 1), `t` (UTC ISO-8601 with ms), `seq`, `session` (random id per process), `run_id` (from config).

| `ev` | Extra fields |
| --- | --- |
| `start` | `image_version`, `revision`, `arm`, `config_hash` (SHA-256 of the merged effective config), `config_files` (names), `allow_path`, `offline`, `cache_warm`, `servers` (id, version, pin kind per active server), `tools` (final exposed tool names), `disabled` (tools removed by config), `platform` |
| `rpc` | `method` (`initialize`, `tools/list`, `ping`), `ms` |
| `call` | `tool`, `operation` (lsp tools), `args` (per detail level), `ms`, `ok`, `error_kind` (`tool`, `timeout`, `invalid`, `unknown_tool`, `cancelled`), `result_bytes`, `result_lines`, `truncated` (bool), `server` (language server id when known), `state` (`ready`, `initializing`, `unavailable`...) , `servers_started` (count started by this call) |
| `server` | `id`, `phase` (`install`, `start`, `ready`, `exit`), `ms`, `ok`, `memory_mb` when cheap |
| `end` | `reason` (`stdin-eof`, `sigterm`, `error`), `calls`, `errors`, `total_ms` |

`ms` is wall time inside the host. Time-to-first-useful-answer for LSP shows up as `servers_started > 0` and large `ms` on the first
call, and as `server` events.

### Privacy

- Default `detail = "meta"`: tool, operation, durations, sizes, outcome. No argument values, no result text, no file paths, no file contents.
- `detail = "args"` adds the call arguments (queries, workspace-relative paths, positions). Absolute paths are rewritten to workspace-relative.
- `detail = "full"` adds result text, truncated to 4 KiB each. Explicit opt-in only, and `start` records the level so a log can
  never be mistaken for a lighter one.
- Never logged at any level: environment variables, tokens, anything under `/cache`.
- The log lives in the project at `.sharprail/runs/` (decided). It is the one documented exception to the rule that SharpRail
  writes nothing but shareable configuration into `.sharprail/`, and it applies only when `[log] enabled = true`. The
  rule text in `Language/SPEC.md` and `Workspaces.SPEC.md` is updated in the same commit as this design. The host still never
  creates a `.gitignore`. The docs tell researchers to add `.sharprail/runs/` to `.git/info/exclude`, the same advice
  `Workspaces.SPEC.md` gives for the directory.

### How switches work

- `tools.disable` and `enable_only` filter the table before any request, so `tools/list` omits them and `tools/call` answers
  exactly like an unknown tool (`Unknown tool: <name>`). Claude Code then cannot call them.
- `tools.<name>.operations` removes operations. The `lsp_navigate` schema already builds its `operation` enum from a list per
  tool (verified in `LanguageMcpTools`), so narrowing is a filter on that list. The call also rejects a removed operation with a message naming the allowed ones.
- No rename, by decision.
- Plugin level: `plugins/<id>.toml` `enabled = false` removes that plugin's tools through the existing reconciler.
- Existing switches stay: `SHARPRAIL_LSP_DISABLE=1` and `[language] enabled = false` (turn language off at the source). The doc
  recommends tool-level switches for experiments because the tool list the model sees is then explicit and logged.
- An arm is just a named overlay (`[arms.<name>]`). `start` logs the effective result.

### Comparing two arms

Minimum useful, first as documented `jq` in `docs/headless.md`, with these questions:

```sh
# calls per tool
jq -r 'select(.ev=="call") | .tool' runs/*.jsonl | sort | uniq -c
# time spent in tools, errors
jq -s '[.[]|select(.ev=="call")] | {calls:length, ms:(map(.ms)|add), errors:(map(select(.ok|not))|length)}' runs/*.jsonl
# first-call cost of language servers
jq -s '[.[]|select(.ev=="call" and .servers_started>0)] | map(.ms)' runs/*.jsonl
```

Outcomes (tokens, turns, cost, pass or fail) come from Claude Code's stream-json and the task's test command. A `report` command
(`sharprail-headless report lsp.jsonl nolsp.jsonl`) that prints one two-column table of those numbers is slice 5b and is
optional. It does not compute significance.

## G. Risks, open questions and the sliced plan

### Verified versus assumed

Verified by reading code or specs:

- Core and plugin Host projects have no Avalonia reference (direct references).
- A headless MCP table already exists (`HeadlessLanguageMcp`) and is checked by `LanguageChecks.cs` and `AirContextChecks.cs`.
- `McpServer.HandleAsync` is stateless request/response and is the one choke point.
- Tool registration points: `LanguageMcpTools.Create`, `context.Tool(...)` in plugins, `WorkspaceMcpTools` in the loopback only.
- Server pinning kinds, the install lease, `AllowPath` default false, TOML helper and 64 KiB limit, `version = 1` convention.
- A memory-only state store turns language services off.
- No runtime (.NET, Node, JDK, Python) is downloaded today: servers find them through the tool override, home variable and hardened PATH. `ArtifactInstaller` already verifies, unpacks `gz`, `tar.gz` and `zip`, and installs atomically.
- `Language/SPEC.md` says the app does not install global SDKs. Managed runtimes live in the private cache and are not global, so this stays true, and the spec gets one sentence when slice 2 lands.

Assumed, each proven by a named slice:

- `PluginRuntime` activates default plugins with `Terminals = null` and no loopback URL (slice 1).
- Claude Code expands `${PWD}` in `.mcp.json` args and launches with `docker run -i` as expected, and the flags
  `--mcp-config` and `--strict-mcp-config` behave as used above (slice 6, manual run by the user).
- `safe.directory` through `GIT_CONFIG_*` is enough for a bind-mounted repo (slice 4).
- A symlinked or overridden `language` directory works with the installer (slice 1 or 4).
- Sizes in section D (slice 4 for the image, slice 2 and 7 for runtimes).
- Cross-publishing arm64 from one runner is fine (slice 5).
- The runtime download URLs, publisher checksums and arm64 artifacts in the runtime table exist as described (slice 2, at pin time).
- Roslyn runs on the managed SDK with `DOTNET_ROOT` set, and the SDK is enough for MSBuild project loading (slice 2).
- Kotlin and IntelliJ bundle a JRE (question 6).
- Env/flag/toml layering does not clash with the existing `language.toml` precedence (slice 1 tests).

### Risks

- Runtime provisioning is new code on the critical path. A wrong pin, a vendor URL that moves or a missing arm64 artifact stops a
  server from starting. Mitigation: pins are checked by `warm` in CI on both architectures, and `manifest` lists them.
- SDK versions. A project's `global.json` can name an SDK we do not pin. The error lists the pinned ones. The first C# target
  must use a pinned SDK (this repo pins one in `mise.toml` and `global.json`; the catalog should include it).
- Cache size reaches 1 to 2 GB and the first run downloads it. Pre-warm hides this from experiments, `start` records cold or warm.
- Cold start dominates short tasks. The first `lsp_navigate` can wait up to 60 s (`OnDemandStartWait`) and the deadline is 90 s.
  Roslyn also needs a restore. Pre-warm, `warm --restore` and the `start` event keep it visible.
- Disk writes as a different uid. The cache volume is created by the image user. Bind-mounted `/workspace` can be owned by
  another uid on Linux hosts. The example uses `--user` when needed.
- stdout corruption by stray console output. Mitigated by the stdout swap and a check that runs every default plugin.
- Confounds from instructions, tool descriptions and warm indexes (section A). Documented, not eliminated.
- Log privacy. `meta` is the default and full text needs an explicit level. The `.sharprail/runs/` exception to the directory rule is documented.
- Two containers on one checkout will collide on server state. The workflow uses separate worktrees.
- Licensing of redistributed runtimes: nothing is bundled in the image. The runtimes are downloaded by the user's container
  from the vendors, like the servers. The licenses are listed in `manifest` output and in the docs page.

### Sliced plan (fastest path to the C# experiment first)

Order set by the user: get the first end-to-end loop working for C# (this repository or a small C# repo) as soon as possible.
That needs, in order, the stdio entrypoint, runtime provisioning for the .NET SDK, the tool switches and call log, and a
slim image. Node/TypeScript and then Python follow. Config generation and the cleanup come after the first loop.
Sizes are rough focused engineer-days. The NativeAOT work is out of scope by the user's instruction.

| # | Slice | Scope | Touches | Tested headlessly by | Size |
| --- | --- | --- | --- | --- | --- |
| 1 | Headless stdio MCP | New `SharpRail.Host.Headless` project, `mcp` and `check-config` stubs, composition as in C, stdout discipline, `instructions` override, `SHARPRAIL_CACHE_DIR`, hard `allow_path=false` default, roster limited to LSP + SpecDialect + Blueprint, Air Context off | new project, `McpServer.HandleAsync` (additive parameter), `LanguageServers.Resolution.cs` (global switch), language dir override | `--headless`: spawn the built exe, send `initialize`, `tools/list`, `lsp_status` and `spec_grep` over pipes against a fixture workspace; EOF exits 0; a plugin writing to stdout does not break frames | 3 to 4 |
| 2 | Runtime provisioner, .NET SDK first | `ManagedRuntimes` with the pinned .NET SDK for linux x64 and arm64, `[toolchains] source`, `ProbeTool` order, clean per-run PATH and cache env, `FindDotnet` returns the managed SDK, `warm` for C#, `NUGET_PACKAGES` under the cache, offline flag | `Host.Core/Language` (new file, `LanguageServers.Dependencies.cs`), `Plugins.Api.Host` (small, `PublicAPI.Unshipped.txt`), pin script | `--runtimes` with a local fake HTTP server serving a tiny fake archive: hash match installs, mismatch installs nothing, redirect to an unlisted host is refused, offline refuses, PATH does not leak (a fake `dotnet` on PATH is ignored). Real download only in the container smoke job | 4 to 5 |
| 3 | Tool switches and run log | `mcp.toml` (`[tools]`, `[arms]`, `[log]`), decorator, JSONL events and detail levels, env and flag layer for `arm`, `log`, the documented `.sharprail/runs/` exception in the two specs | Headless project, a small TOML reader reusing `ProjectToml`, `Language/SPEC.md`, `Workspaces.SPEC.md` | `--headless-switches`: disabled tool absent from `tools/list` and the call is unknown; narrowed `operations`; log off writes no file; `meta` never leaks args | 3 |
| 4 | Slim image, local | Dockerfile, `.dockerignore`, `manifest`, example `.mcp.json`, first `docs/headless.md`, a `container:build` task | new files, `scripts/` | Build locally, `docker run -i` with piped JSON, assert tools list and clean stop on EOF and SIGTERM, then `warm` and an `lsp_navigate` on a tiny C# fixture | 3 |
| 5 | CI publish to ghcr.io | `container.yml`, multi-arch manifest, tags, labels, smoke on amd64 (blocking) and arm64 (non-blocking at first), size measurement | `.github/workflows/container.yml` | the workflow's own smoke job | 2 |
| 6 | First C# experiment (user) | The user runs the worked example from section A on this repo or a small C# repo, arms LSP on and off, jq comparison. Fixes what hurts | `docs/headless.md`, optional `report` | Manual: Claude Code against the container. Validates the `${PWD}` and flag assumptions | 1 plus iteration |
| 7 | Node runtime, TypeScript | Pin Node LTS with npm for both architectures, wire npm servers to the managed Node, `warm` for TypeScript | `ManagedRuntimes`, pins | `--runtimes` extended; smoke `lsp_navigate` on a tiny TS fixture | 2 |
| 8 | Python and JDK runtimes | Pin python-build-standalone (for import resolution and the interpreter setting) and Temurin 21 (Java, Gradle/Maven), `JAVA_HOME` hook for the Java plugin | `ManagedRuntimes`, `JavaPlugin`, `ProjectJava.cs` | same | 3 |
| 9 | Config schema, host.toml, plugins/*.toml | `ConfigKey` attribute, registry generator, loaders, strict mode, precise errors | Core, plugin contract projects (attributes only), Headless | `--config`: parse errors with file and line, unknown key, bad version, precedence | 5 |
| 10 | Docs and schema generation | Markdown and JSON Schema generator, `--config-docs` check that fails on undocumented settings, committed `docs/reference/` | Core, docs, Checks | `--config-docs` | 3 |
| 11 | Reorganise and cleanup | `ProjectConfig` reader replaces three readers, shared `HostComposition` for Remote and Headless, optional Core split of unused plugins | Core, Remote | existing transport checks plus `--headless` unchanged | 4 to 6 |

Dependencies: 2 and 3 need 1 and can proceed in parallel after it. 4 needs 1 and 2. 5 needs 4. 6 needs 3 and 5, or 3 and a locally
built image. 7 and 8 need 2. 9 can start after 3 but waits for the first loop so key names come from real use. 10 needs 9, 11 is last.
Slices 1 to 5 land independently with CI green. No slice touches UI code. Slices 2, 7 and 8 touch language resolution, which the
desktop also uses, so the default `source = "path"` keeps desktop behaviour byte-identical, proven by the existing `--lsp-dependencies` check.

### Decided by the user

- One slim image, no variant tags. Runtimes are provisioned on demand into the cache volume, pinned with SHA-256.
- Run log at `.sharprail/runs/` in the project, opt-in only, as a documented exception to the "nothing else" rule.
- Air Context is off in version 1. No mounted login.
- First experiment language is C#, then TypeScript, then Python.
- Env vars and flags override committed project files.
- Arms are `[arms.<name>]` in `mcp.toml`, selected by `SHARPRAIL_ARM`. Two `.mcp.json` files that differ only in env overrides also work.
- `mcp.toml` holds the tool switches and the call log together.
- Registry name `ghcr.io/commandertvis/sharprail`.
- The arm64 smoke job is non-blocking at first.

### Open questions

1. .NET SDK catalog. Which exact SDK versions go in the first pins? Proposal: the version `global.json` pins in this repo, plus the
   current .NET 10 LTS patch. A project that needs another version fails with the list.
2. Should `warm --restore` exist in version 1, given it runs the project's MSBuild targets? Proposal: yes, opt-in flag.
3. Is Python needed for the first Python study, or is Pyright without an interpreter acceptable (no third-party import resolution)? Proposal: provision it in slice 8.
4. Does a `[toolchains]` pin set per image version satisfy "reproducible", or must a project be able to pin its own runtime hashes in `host.toml`? Proposal: image-pinned only.
5. What task and repository for the first C# run? This repository is large (a solution with many projects) and restore-heavy. A small C# repo gives faster iteration.
6. Kotlin LSP and the IntelliJ server: please confirm they bundle their own JRE (I assumed so, not verified).
