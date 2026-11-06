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

### Which plugins are UI-bound

Every plugin has a Host half that runs headless. UI halves are never loaded. Plugins that make no sense headless
(they only serve a window or a terminal): Discord, Tailscale, Epub, BranchGraph, Processes, the agent launchers
(ClaudeCode, Codex, Omp, Pi, Acp), Providers, AirGateway, Env* (they extend terminal shells), GitHub (pull-request UI).
Plugins useful headless: all `Lsp.*`, SpecDialect, Blueprint, and optionally AirContext and the Env plugins (they could
set `JAVA_HOME` or the Python venv for language servers; not verified how they hand that to servers).
The headless entrypoint sets the plugin roster explicitly from config and defaults to LSP + SpecDialect + Blueprint.

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

### Language server toolchains and a "minimal" image

The decision was a minimal image with servers installed on demand. The catch (verified in the `Language/SPEC.md`
table): several managed servers need a toolchain in the container, not just a download.

| Needs | Servers |
| --- | --- |
| Only a pinned archive | clangd, rust-analyzer, Kotlin LSP |
| Node/npm (npm graph pinned by lockfile) | TypeScript, Python (Pyright), PHP, CSS, Shell |
| .NET SDK | C# (Roslyn language server) |
| JDK 21+ | Java, Scala (plus Coursier) |
| The language's own SDK | Go, Ruby, R, PowerShell, Swift, Dart |

Proposal: publish tags, all from one Dockerfile with build args, none `latest` by default in docs.

| Tag | Adds to base | Covers |
| --- | --- | --- |
| `<v>` (slim) | git | archive servers |
| `<v>-node` | Node LTS | plus TypeScript, Python, Shell, CSS, PHP |
| `<v>-dotnet` | .NET SDK | plus C# |
| `<v>-jvm` | JDK 21 | plus Java, Kotlin builds |

Project build tooling (restore for Roslyn, Gradle for JDT) is also needed for good results. Researchers should build a
derived image `FROM ghcr.io/.../sharprail:<v>-node` with their project's toolchain. The doc says this plainly.

### Size estimates

Not measured. The repo has no container build, and its packaging is AppImage and app bundles, whose sizes are not recorded
in the docs I read. Rough expectations, to be replaced by slice 4 measurements:

| Layer | Estimate |
| --- | --- |
| `dotnet/runtime:10.0` base | about 80 to 90 MB compressed |
| git and certificates | about 30 MB |
| `sharprail-headless` framework-dependent, with Core and all plugin Host halves, trimmed of UI | about 25 to 40 MB |
| slim total | about 150 MB |
| Node layer | about 60 MB compressed |

The first slice reports the real numbers. Growth from the plugins in Core that headless never uses (Discord, Tailscale,
Env, agent launchers) is small compared with the base.

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
- Tags: `sha-<short>` always, `nightly` moving, `vX.Y.Z` and `latest` only for a release tag. Suffixes `-node`, `-dotnet`,
  `-jvm` as above. Research docs tell people to pin a `vX.Y.Z` or a digest.
- Version mapping: the app version equals the repo version stamped at publish. The image carries OCI labels
  `org.opencontainers.image.revision` (commit) and `.version`. Pinned server versions are in code, so one image
  digest fixes one set of server versions. `sharprail-headless manifest` prints them.
- The workflow follows the repo rules: one CI attempt, no reruns, whoever pushes it watches the run.

### Reproducibility and offline mode

- Managed servers are pinned. Archive servers have an exact version and a SHA-256 per platform artifact, verified before
  extraction (verified, `ManagedArtifacts.cs` `Pinning.Archive`). npm servers use an embedded lockfile with integrity
  hashes (`Pinning.Lockfile`), NuGet servers SHA-512 per package, Go servers `go.sum`. `Pinning.Partial` and `None`
  exist for some servers (Swift and Dart follow the SDK). The doc lists which servers are fully pinned, from the plugins' specs.
- PATH override. `LanguageServerOptions.AllowPath` is per server and defaults to false (verified). For the container the
  doc requires a hard global switch: `host.toml` `[language] allow_path = false` (default in headless, and a project file
  cannot enable it unless `SHARPRAIL_ALLOW_PATH=1` is set in the container env). Needs a small change in
  `LanguageServers.Resolution.cs`. If `allow_path` is on, the run log records it in the header.
- Pre-warm: `docker run --rm -v sharprail-cache:/cache -v "$PWD":/workspace ghcr.io/.../sharprail:<v>-node warm`
  reads `host.toml` and `language.toml` and installs exactly the servers they name, plus everything npm needs. It needs
  network and exits nonzero if any hash mismatches.
- Offline rerun: `--network none` plus `SHARPRAIL_OFFLINE=1` (or `[host] offline = true`). Offline mode refuses any download
  with an error naming the missing artifact and the `warm` command. Needed for offline: the cache volume, the same image
  tag, and the project's own dependencies (restore results, `node_modules`, Gradle cache) already present in `/workspace`
  or the derived image. Servers that resolve project dependencies on start (Roslyn, JDT, Metals) will not work offline
  unless those are present. Assumed, since the specs say they need the real Gradle/Maven prerequisites.
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
4. Container environment: `SHARPRAIL_ARM`, `SHARPRAIL_OFFLINE`, `SHARPRAIL_LOG`, `SHARPRAIL_ALLOW_PATH`, and the generic
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
- The log lives in the project (`.sharprail/runs/`). Docs advise `.gitignore` for it. The host never creates a `.gitignore`
  there (existing rule in `Language/SPEC.md`: SharpRail writes nothing else into `.sharprail`). The run log is the one
  exception that is opt-in and written only when enabled. This is a change to that rule and needs the user's agreement.

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

Assumed, each proven by a named slice:

- `PluginRuntime` activates default plugins with `Terminals = null` and no loopback URL (slice 1).
- Claude Code expands `${PWD}` in `.mcp.json` args and launches with `docker run -i` as expected, and the flags
  `--mcp-config` and `--strict-mcp-config` behave as used above (slice 3, manual run by the user).
- `safe.directory` through `GIT_CONFIG_*` is enough for a bind-mounted repo (slice 4).
- A symlinked or overridden `language` directory works with the installer (slice 1 or 4).
- Sizes in section D (slice 4).
- Cross-publishing arm64 from one runner is fine (slice 4).
- Env/flag/toml layering does not clash with the existing `language.toml` precedence (slice 1 tests).

### Risks

- Toolchain gap. A slim image cannot start Roslyn, Pyright or JDT. Mitigated by the tag family, but a research image per
  language is real work for researchers. This is the largest practical risk to "it fully works".
- Cold start dominates short tasks. The first `lsp_navigate` can wait up to 60 s (`OnDemandStartWait`) and the deadline is 90 s.
  Pre-warm and the `start` event keep it visible, but the experiment must report it separately.
- stdout corruption by stray console output. Mitigated by the stdout swap and a check that runs every default plugin.
- Confounds from instructions, tool descriptions and warm indexes (section A). Documented, not eliminated.
- Air Context cannot sign in headless. Its `context_search` is a major competitor to LSP for navigation, so an LSP study that
  wants to compare against it needs a prepared login. Out of scope for v1.
- Log privacy and the `.sharprail` rule above.
- Docker on Linux uid mismatch for files the agent writes.
- Two language servers on the same workspace from two containers (parallel arms on one checkout) will collide. The workflow uses separate worktrees.

### Sliced plan (fastest path to the research loop first)

Priority order set by the user: stdio entrypoint, container, tool switches and the opt-in call log first. Documented-config
generation and the larger cleanup come after the first working end-to-end run. Sizes are rough and in focused engineer-days.

| # | Slice | Scope | Touches | Tested headlessly by | Size |
| --- | --- | --- | --- | --- | --- |
| 1 | Headless stdio MCP | New `SharpRail.Host.Headless` project, `mcp` and `check-config` stubs, composition as in C, stdout discipline, `instructions` override, `SHARPRAIL_CACHE_DIR`, hard `allow_path=false` default, roster limited to LSP + SpecDialect + Blueprint | new project, `McpServer.HandleAsync` (additive parameter), `LanguageServers.Resolution.cs` (global switch), `HostStateStore` language dir | New Checks mode `--headless`: spawn the built exe, send `initialize`, `tools/list`, an `lsp_status` and a `spec_grep` over pipes against a fixture workspace; EOF exits 0; a plugin writing to stdout does not break frames | 3 to 4 |
| 2 | Tool switches and run log | `mcp.toml` (`[tools]`, `[arms]`, `[log]`), decorator, JSONL events and detail levels, env and flag layer for `arm`, `log` | Headless project only, plus a tiny TOML reader reusing `ProjectToml` | `--headless-switches`: disabled tool absent from `tools/list` and call is unknown; narrowed `operations`; log off writes no file; levels never leak args at `meta` | 3 |
| 3 | Dockerfile and local image | Dockerfile, `.dockerignore`, `warm` and `manifest`, offline flag, example `.mcp.json` and `docs/headless.md` first version | new files, `scripts/` task `container:build` | Build locally, run `docker run -i` against a fixture with piped JSON, assert tools list and clean stop on stdin close and SIGTERM. Not in the `ci.yml` gate until slice 4 | 3 |
| 4 | CI publish to ghcr.io | `container.yml`, multi-arch manifest, tags, labels, smoke test on both arms, size measurement | `.github/workflows/container.yml`, a mise task | The workflow's own smoke job (the container check above, no GUI) | 2 |
| 5 | First end-to-end research run (user) | The user runs the worked example from section A against a real task. `report` command and `jq` recipes polished from what hurts | `docs/headless.md`, optional `report` | Manual by the user: Claude Code against the container, two arms. This validates the `${PWD}` and flag assumptions | 1 plus iteration |
| 6 | Config schema, host.toml, plugins/*.toml | `ConfigKey` attribute, registry generator, loaders for `host.toml` and `plugins/<id>.toml`, strict mode, precise errors | Core, plugin contract projects (attributes only), Headless | `--config` (parse errors with file and line, unknown key, bad version, precedence), unit-level, no window | 5 |
| 7 | Docs and schema generation | Markdown and JSON Schema generator, `--config-docs` check that fails on undocumented settings, committed `docs/reference/` | Core, docs, Checks | `--config-docs` | 3 |
| 8 | Reorganise and cleanup | `ProjectConfig` reader replaces three readers, shared `HostComposition` used by Remote and Headless, optional Core split of unused plugins, remove duplicated headless HTTP route if no longer needed | Core, Remote | existing transport checks plus `--headless` unchanged | 4 to 6 |

Dependencies: 2 needs 1, 3 needs 1, 4 needs 3, 5 needs 2 and 4 (or 3 with a locally built image), 6 can start after 2 but waits for
the first run so the key names are informed by real use, 7 needs 6, 8 is last. Slices 1 to 4 land independently with CI green.
No slice touches UI code, so the existing UI checks do not apply.

### Decisions needed from the user

1. Tag family: is slim plus `-node`, `-dotnet`, `-jvm` right, or one fat image? Which languages matter first for the study?
2. Run log location: allow SharpRail to write `.sharprail/runs/` (a new write into `.sharprail`, against the current "writes nothing else there" rule), or default to a separate mount `/runs`?
3. Env and flags above the project file. OK, so that harness variables win over committed config?
4. Arm names via `[arms.<name>]` plus `SHARPRAIL_ARM`, versus two plain `.mcp.json` files that only differ in `SHARPRAIL_SET__...` overrides. I propose arms.
5. Air Context headless: out of scope for v1 (proposed), or support a mounted prepared login?
6. Registry name and org: `ghcr.io/commandertvis/sharprail` (assumed from the repo URL in `LINUX.md`)?
7. Should `mcp.toml` hold tool switches and the log together (proposed), or a separate `research.toml` for the log?
8. Is a second image architecture gate acceptable, i.e. the arm64 smoke job blocking the release tag?
9. Does the first end-to-end run (slice 5) use a repo of your choice? Which language and which task?
