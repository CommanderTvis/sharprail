# Headless SharpRail for AI experiments

Status: the stdio server works and a container image is built by the Container workflow. The package is private until the owner makes it public.

SharpRail can run without a window. It serves its language tools to an agent over MCP, on stdin and stdout, for one project. The spec and Blueprint tools are off by default and you opt in (see [Spec and Blueprint tools](#spec-and-blueprint-tools)). The main use is research: does the LSP toolset help an agent do a task?

To use it in a cloud session of Claude Code on the web, see [headless-cloud.md](headless-cloud.md).

## Run it from the image

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "--init", "-v", "${PWD}:/workspace", "-v", "sharprail-cache:/cache", "ghcr.io/commandertvis/sharprail:nightly"]
    }
  }
}
```

That is the exact snippet for Docker. With the spec and Blueprint tools, add `"-e", "SHARPRAIL_TOOLS_ENABLE=spec,blueprint"` before the image name. With Apple `container` use a host directory for the cache, because it has no named volumes in this form, and make it writable by uid 1000:

```sh
mkdir -p ~/.cache/sharprail && chmod 777 ~/.cache/sharprail
```

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "container",
      "args": ["run", "-i", "--rm", "-v", "${PWD}:/workspace", "-v", "/Users/me/.cache/sharprail:/cache", "ghcr.io/commandertvis/sharprail:nightly"]
    }
  }
}
```

Apple `container` has no `--init` flag and needs absolute host paths for the cache.

### The first start is slow: run `warm` first

A cold cache downloads the pinned runtime and the language server of each language the first time it is used: for C# the .NET SDK (about 650 MB unpacked) and the Roslyn server, for the Bun-based servers about 100 MB. Until that is done, `lsp_*` calls answer `initializing`. The measured cold start inside the container (Apple `container`, arm64) was 100 s to 6.5 min, depending on the network: the SDK download is most of it. With a warm cache the server is ready in about 6 s. Install it before the first session:

```sh
container run --rm -v ~/.cache/sharprail:/cache ghcr.io/commandertvis/sharprail:nightly warm
```

`warm` takes server ids (`csharp`, `typescript`, `python`, `css`, `shell`, `php`, `clangd`, `kotlin`; default `csharp`), installs the runtimes and servers into `/cache` and exits. A second run returns at once. Progress is in `/cache/home/.sharprail/logs/code-intelligence.log` (`host.runtime-install`, `host.server-install`, `host.install-command`).

Use `-i` so stdin stays open, and `--init` so SIGTERM and child processes are handled. The image is for linux/amd64 and linux/arm64 under one tag. While CI is off it is built by hand from a Mac, see [the publishing steps](headless-cloud.md#publishing-the-image).

- The `nightly` tag moves. The Container workflow overwrites it on every run. Each run also pushes an immutable `sha-<short commit>` tag.
- To pin, use the digest: `docker pull ghcr.io/commandertvis/sharprail:nightly` then `docker inspect --format '{{index .RepoDigests 0}}' ghcr.io/commandertvis/sharprail:nightly`, and put `ghcr.io/commandertvis/sharprail@sha256:...` in the config. The run summary of the workflow also lists the digest.
- The `/cache` volume holds the downloaded runtimes and language servers, and the state. The first run downloads them. Keep the volume to avoid that. A run with `--network none` works only after a run with the same volume has downloaded what the project needs.
- The workspace mount is read-write. The container user has uid 1000. Git accepts a checkout owned by another user.
- A new package on ghcr.io is private. The owner makes it public in the package settings on GitHub. A workflow cannot do that. Until then run `docker login ghcr.io` with a token that can read packages.
- The workflow needs write access for workflows in the package settings, or the push is refused.

To run it by hand: `gh workflow run container.yml -f tag=nightly`. To try the script on a local image: `scripts/mise x -- dotnet run --file scripts/container-smoke.cs -- <image>`.

## Run it from a build

```sh
dotnet run --project src/SharpRail.Host.Headless -c Release -- mcp --workspace /path/to/project
```

In a project's `.mcp.json`:

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "/path/to/sharprail-headless",
      "args": ["mcp", "--workspace", "."]
    }
  }
}
```

The server reads one JSON-RPC message per line and writes one per line. Logs go to stderr. It stops with exit code 0 when stdin ends or on SIGTERM or SIGINT. A configuration error stops it with exit code 2 and a message on stderr.

## Tool names and startup in Claude Code

The server lists its tools on `tools/list` at once (the six `lsp_*` tools by default, all 14 when the spec and Blueprint tools are opted in), on a cold start too: nothing waits for the language roster, consent, a workspace or a first `languages` call, and a language that is still installing answers `initializing` from the call, not from the list. The list never changes while the server runs, so `tools.listChanged` is declared `false` and no `notifications/tools/list_changed` is sent. The protocol version is the client's own when it is `2024-11-05`, `2025-03-26` or `2025-06-18`, else `2025-06-18`. `initialize` takes about 20 ms; the `instructions` text names every tool.

Claude Code names a tool `mcp__<server>__<tool>`, where `<server>` is the key in `mcpServers`. With the key `sharprail` they are `mcp__sharprail__lsp_navigate`, `mcp__sharprail__lsp_diagnostics`, `mcp__sharprail__lsp_status`, `mcp__sharprail__lsp_code_actions`, `mcp__sharprail__lsp_rename`, `mcp__sharprail__lsp_format`, `mcp__sharprail__spec_grep`, `mcp__sharprail__spec_get`, `mcp__sharprail__spec_graph`, `mcp__sharprail__spec_create`, `mcp__sharprail__spec_update`, `mcp__sharprail__spec_delete`, `mcp__sharprail__spec_validate` and `mcp__sharprail__blueprint_check`. A server bundled in a plugin is `mcp__plugin_<plugin>_<server>__<tool>` (the desktop app's own tools look like that), so a search for the exact `mcp__sharprail__...` name does not find it, while a search for `sharprail` does.

Claude Code starts MCP servers in the background, 3 stdio servers at a time, and a server that has not connected within `MCP_TIMEOUT` (30 s by default) is marked failed; a stdio server is not retried. Tool schemas are deferred behind `ToolSearch`, which answers "No matching deferred tools found" while a server has not delivered its list or after it failed. In a Claude Code cloud session do not use a `.mcp.json` or Docker at all: paste `scripts/headless-cloud-setup.sh` into the environment's setup script, which installs the binary and writes the user-scope entry (with `alwaysLoad`) and `MCP_TIMEOUT`, see [headless-cloud.md](headless-cloud.md). The rest of this section is the background, and applies to a `.mcp.json` on your own machine.

Two rules follow:

- `"alwaysLoad": true` puts the tools into the first prompt instead of leaving the agent to find them (startup waits for the server, capped by `MCP_CONNECT_TIMEOUT_MS`, 5 s by default). It needs a server that starts in that time: the installed binary does, `docker run` with an image pull does not.
- Do not start the server with `docker run` in a cloud session unless the image is already on the VM. The pull takes many seconds to minutes and counts against the 30 s. Use the setup script from [headless-cloud.md](headless-cloud.md) (a binary, no Docker), or set `MCP_TIMEOUT=300000` in the environment variables of the cloud environment and run `docker pull ghcr.io/commandertvis/sharprail:nightly` in the setup script, so the first start only creates the container.

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "${HOME}/.local/bin/sharprail-headless",
      "args": ["mcp"],
      "env": {"SHARPRAIL_TOOLCHAINS": "managed"},
      "alwaysLoad": true
    }
  }
}
```

The Docker variant, only with the image pulled beforehand: `"command": "docker"`, `"args": ["run", "-i", "--rm", "--init", "-v", "${PWD}:/workspace", "-v", "sharprail-cache:/cache", "ghcr.io/commandertvis/sharprail:nightly"]`, and the same `alwaysLoad`.

If an agent's `ToolSearch` finds nothing, the server is still connecting or has failed: run `/mcp` (or `claude mcp list`), search again, and do not start the work without the tools. Which of these happened in the report that prompted this section is not known: it was not reproduced in a real cloud session.

## Settings

| Setting | Meaning |
| --- | --- |
| `--workspace <dir>` | The project. Defaults to `SHARPRAIL_ROOT`, then the current directory. |
| `--arm`, `--disable-tools`, `--enable-tools` | Tool switches, see below. |
| `SHARPRAIL_STATE_DIR` | Where settings and language server installs live. Defaults to `~/.sharprail/headless`. |

Tools by default: `lsp_navigate`, `lsp_diagnostics`, `lsp_status`, `lsp_code_actions`, `lsp_rename` and `lsp_format`. Paths are relative to the workspace. The optional `workspace` argument is ignored.

### Spec and Blueprint tools

The Spec plugin (the `spec_*` tools) and the Blueprint plugin (`blueprint_check`) are off by default, in headless and in the desktop app. To opt in, set `SHARPRAIL_TOOLS_ENABLE=spec,blueprint`, pass `--enable-tools spec,blueprint`, or put `enable = ["spec", "blueprint"]` under `[tools]` in `.sharprail/mcp.toml`. Use `spec` alone for the spec tools, or `blueprint` (which brings `spec` along, because Blueprint needs it). With the tools off, `tools/list` and `initialize` do not mention them, and the `initialize` text says how to turn them on. The setting applies to one run and writes nothing to the state directory.

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "/path/to/sharprail-headless",
      "args": ["mcp", "--workspace", "."],
      "env": { "SHARPRAIL_TOOLS_ENABLE": "spec,blueprint" }
    }
  }
}
```

### The workspace root and paths

The server serves one directory, its root: `--workspace`, else `SHARPRAIL_ROOT`, else the directory it was started in. If your project is a child of that directory (a cloud session starts in `/home/user`, the project is in `/home/user/demo`), either set `--workspace` or `SHARPRAIL_ROOT` to the project, or use paths from the root. The `initialize` text and the `lsp_status` answer (`workspaceRoot`) state the root.

The `lsp_*` tools take a path relative to the root (`demo/Shapes.cs`) or an absolute path inside it. A relative path that does not exist, but exists under exactly one child directory of the root, is refused with the answer `Did you mean demo/Shapes.cs?`. A path outside the root is refused. Globs and dependency URIs are passed on as they are.

### The Blueprint format

An agent in a headless session has no copy of the plugin's format file. `blueprint_check` has the format in brief in its description and returns the full format reference when called with `reference: true`. A `BLUEPRINT.md` with no controls is answered with a minimal valid example and the same pointer. The reference is the text the Blueprint plugin writes to `blueprint-format.md` on the desktop, from one source. It is a tool argument and not an MCP resource or a fifteenth tool, because agents list tools more reliably than resources and the tool table stays at its fixed set.

## Languages

Headless serves the language servers whose runtime is a pinned, checksummed download, so nothing is baked into the image and nothing must be installed on the machine. Each server and its runtime are downloaded on the first `lsp_*` call (or by `warm`) into the cache, and verified against a pinned SHA-256. `sharprail-headless languages` prints the list of the build you run.

| Language | Server | Downloaded from |
| --- | --- | --- |
| C# | Roslyn, on the pinned .NET SDK | builds.dotnet.microsoft.com, api.nuget.org, globalcdn.nuget.org |
| TypeScript, JavaScript | typescript-language-server, on the pinned Bun | github.com (Bun), registry.npmjs.org |
| Python | Pyright, on the pinned Bun | github.com (Bun), registry.npmjs.org |
| CSS, SCSS, Less | VS Code CSS server, on the pinned Bun | github.com (Bun), registry.npmjs.org |
| Shell | bash-language-server, on the pinned Bun | github.com (Bun), registry.npmjs.org |
| PHP | Intelephense, on the pinned Bun | github.com (Bun), registry.npmjs.org |
| C, C++ | clangd, a self-contained archive (linux/amd64, macOS and Windows; no linux/arm64 archive is pinned: the official one is the 1.8 GB LLVM toolchain in tar.xz) | github.com |
| Kotlin | JetBrains Kotlin LS, a self-contained archive | download.jetbrains.com |
| Java, Kotlin | IntelliJ server by JetBrains, only when you ask for it with `SHARPRAIL_PREFETCH=intellij` (see below): a self-contained archive with its own Java runtime, about 380 MB | download.jetbrains.com |

The first call of a language answers `initializing` while it downloads: the network must reach the hosts above (github.com, release-assets.githubusercontent.com, registry.npmjs.org and the others in the row). Later runs on the same cache need no network. The image size is unchanged.

Not headless, because the server needs a toolchain that has no pinned download here and the machine would have to provide it:

| Language | Why |
| --- | --- |
| Java (Eclipse JDT) | needs a JDK 21 or newer; no JDK is pinned. Java files are served by the IntelliJ server if you asked for it and agreed to its terms |
| Go | gopls is built with the Go toolchain; Go is not pinned |
| Rust | rust-analyzer needs cargo and rustc to load a project; no Rust toolchain is pinned |
| Scala | Metals is installed with Coursier, which is not pinned |
| Ruby | ruby-lsp is installed with RubyGems; Ruby is not pinned |
| R | needs R; not pinned |
| PowerShell | needs PowerShell 7; not pinned |
| Swift, Dart | no managed installation: they run the machine's own SDK |

They stay desktop-only until a runtime with a checksum is pinned for them. Air Context is not headless. Kotlin analyses a single file without a JDK; importing a Gradle or Maven project needs a JDK on the machine (not verified in a container).

With `SHARPRAIL_TOOLCHAINS=managed` (the image sets it) the servers use only the pinned runtimes. Outside the image the default is the machine's PATH for .NET, so set it to `managed` there too. The JavaScript-based servers need no Node or npm: they use a pinned Bun, downloaded from GitHub releases into the cache and checked against its SHA-256, and install their packages from registry.npmjs.org.

## The IntelliJ server (opt-in)

The IntelliJ server for Java and Kotlin is not part of the default headless roster. Without the flag below it does not exist: it is not in `languages`, the initialize text, the server list or the tool list (there is no `accept_terms` tool), no file is downloaded or written for it, `.kt` files go to the Kotlin server and `.java` files have no server. The desktop app is unaffected.

Ask for it with an environment variable passed into the container:

```json
"args": ["run", "-i", "--rm", "--init", "-v", "${PWD}:/workspace", "-v", "sharprail-cache:/cache", "-e", "SHARPRAIL_PREFETCH=intellij", "ghcr.io/commandertvis/sharprail:nightly"]
```

`SHARPRAIL_PREFETCH=intellij` (a comma list for other servers with terms) makes the host download the server in the background at start. The download fetches the pinned archive from JetBrains and checks its SHA-256; nothing in it is executed before you agreed. A Java call that arrives earlier starts the same download and answers that it is downloading and to call again. Naming `intellij` in `SHARPRAIL_ACCEPT_TERMS` also asks for the server.

It is a JetBrains product with its own terms (the JetBrains User Agreement, version 2.0) and its own licence. SharpRail never accepts for you, and the image holds no acceptance.

1. After the download, the first Java call, or `accept_terms` with only the server, shows the FULL text of the terms from the file inside the download, with its hash and version, in pages if it is long. Before the download has finished only a summary is shown, and nothing can be accepted.
2. The agent asks you in chat. After your yes it calls `accept_terms` with the hash and your answers to two questions JetBrains asks: your region, and whether usage statistics go to JetBrains. The tool only works with the exact hash of the downloaded text. Nothing technical stops an agent from calling it without asking you, so read what it reports.
3. If your MCP client supports elicitation, the host asks you directly and shows the terms with Accept and Decline; a client that cannot show a long text gets the first pages and the instruction to read the rest through `accept_terms`. A Decline, a failure or no elicitation falls back to the text, and a declined prompt is not repeated in the same run.
4. To accept without a question, also set `SHARPRAIL_ACCEPT_TERMS=intellij` and your answers: `INTELLIJ_REGION` (`europe`, `africa`, `americas`, `apac`, `china`, `middle_east`, `oceania`) and `INTELLIJ_DATA_SHARING` (`full`, `anonymous`, `none`). `intellij:<hash>` accepts only the exact text that hash names. With both `SHARPRAIL_PREFETCH=intellij` and `SHARPRAIL_ACCEPT_TERMS=intellij` the host downloads and then starts the server. A missing or unknown answer means no acceptance, and stderr says why.
5. A yes given to `accept_terms` or a prompt is stored in the state directory, so it lasts as long as the state: with the cache volume mounted it survives restarts, without a volume it lasts until the container is gone. The host writes the acceptance record only after the archive is installed and its terms file matches the pinned hash; a text that does not match fails the download and nothing runs.
6. The licence: there is no trial and no sign-in here, because there is no window. The server needs a license you own: add `-e INTELLIJ_SERVER_LICENSE=...`, `INTELLIJ_SERVER_LICENSE_FILE` or `JETBRAINS_LICENSE_SERVER` (a license the server finds itself also counts). Not verified against a real server: that these routes unlock it, and that Java analysis finds a JDK; give the server one with `lsp_status` operation `configure` and the `projectJdk` setting if the project needs one that is not in the image.
7. When IntelliJ takes over: once it works, it replaces the Kotlin server for `.kt` and is the only server for `.java`. Working means the terms are accepted, it started and it is licensed. If any of these fails (not accepted, fails to start, no license or an expired one), the previous server keeps serving, nothing regresses, and the answer carries a hint saying why IntelliJ is not in use. There is no setting to keep the old server once IntelliJ works. `retry` for `intellij` tries it again.
8. `warm intellij` downloads the server and prints the terms (exit 2) unless the consent variable is set.

## Tool switches and the call log

Both live in one file, `.sharprail/mcp.toml` in the project. Without the file, the language tools are offered and nothing is logged or written.

```toml
version = 1

[tools]
disable = ["blueprint_check"]        # these tools do not exist for the run
# enable = ["spec", "blueprint"]     # opt in to the spec_* tools and blueprint_check (off by default)
# allow = ["lsp_navigate"]           # if set, only these exist (disable still wins)

[tools.lsp_navigate]
operations = ["definition", "references"]   # lsp_navigate (also incomingCalls, outgoingCalls, request), lsp_status (also capabilities), lsp_code_actions, lsp_rename, lsp_format
[tools.lsp_code_actions]
operations = ["list", "preview"]            # read-only: apply is removed
[tools.lsp_diagnostics]
enabled = false

[log]
enabled = false                      # opt in
level = "meta"                       # meta | args | full
max_result_chars = 4000              # full level only
max_file_kb = 51200                  # the log stops here
dir = ".sharprail/runs"              # relative to the project

[arms.nolsp]                         # same keys as the top level: tools and log
tools.disable = ["lsp_navigate", "lsp_diagnostics", "lsp_status"]
```

Unknown keys, wrong types and unknown operations are errors. The server exits with code 2 and a message such as `.sharprail/mcp.toml:4: tools.disabel: unknown key`. An unknown tool name only gives a warning.

An arm replaces the top-level value of every key it sets. Precedence, lowest to highest: defaults, the top level of the file, the selected arm, environment variables, flags.

| Setting | Environment | Flag |
| --- | --- | --- |
| Arm | `SHARPRAIL_ARM` | `--arm <name>` |
| `tools.disable` | `SHARPRAIL_TOOLS_DISABLE` (comma list) | `--disable-tools <a,b>` |
| `tools.allow` | `SHARPRAIL_TOOLS_ALLOW` (comma list) | |
| `tools.enable` | `SHARPRAIL_TOOLS_ENABLE` (`spec`, `blueprint`) | `--enable-tools <a,b>` |
| `log.enabled` | `SHARPRAIL_LOG` (`0` or `1`) | |
| `log.level` | `SHARPRAIL_LOG_LEVEL` | |
| `log.dir` | `SHARPRAIL_RUNS_DIR` | |

An empty variable counts as unset. A selected arm that the file does not define is an error.

A switched-off tool is missing from `tools/list` and a call to it fails as an unknown tool. The `initialize` instructions are generated from the tools that exist. Narrowed operations are removed from the schema enum and refused at call time. Tool descriptions are not rewritten, so the `lsp_navigate` text still names operations you removed.

Show the result without starting anything: `sharprail-headless check-config --workspace . --arm nolsp` prints the effective configuration as JSON and exits 0, or 2 for an invalid one. The tool list in it assumes the full roster.

### The call log

With `log.enabled`, each run writes `<dir>/<session>.jsonl`. The session is a UTC timestamp and six hex digits, announced on stderr. This is the one case where SharpRail writes more than shareable configuration into `.sharprail`. Add `.sharprail/runs/` to `.git/info/exclude`, or set `SHARPRAIL_RUNS_DIR` to a separate mount.

Lines have `ev` of `start`, `call` or `end`. Every line has `ts`, `session` and `arm`. A `call` line has `seq`, `tool`, `operation` (language tools), `durationMs`, `isError`, `resultChars`, `resultLines` and `error`. Level `args` adds `args`, each value cut at 500 characters. Level `full` adds `result`, cut at `max_result_chars`. The `start` line has `version`, `configHash`, `toolsOffered` and the effective `config`. The `end` line has `calls`, `errors` and `totalMs`. Lines are queued and written by a background task, so a slow disk never delays a tool. At `max_file_kb` the log stops with one warning on stderr, and the `end` line is still written.

### A/B experiment: language tools on and off

Commit one `.sharprail/mcp.toml` with `[arms.nolsp]` as above, then give the agent two server entries on the same image. Each run uses one of them.

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "--init", "-v", "${PWD}:/workspace", "-v", "sharprail-cache:/cache",
               "-e", "SHARPRAIL_ARM=lsp", "-e", "SHARPRAIL_LOG=1", "ghcr.io/commandertvis/sharprail:nightly"]
    }
  }
}
```

Save the second file with `SHARPRAIL_ARM=nolsp` and pass each to its own run with `claude --mcp-config <file> --strict-mcp-config`. Define an `[arms.lsp]` table too, even an empty one, because an undefined arm is an error. Logs land in `.sharprail/runs/` of the project.

Verified offline with the local `claude` CLI (2.1.319): the flags `--mcp-config` and `--strict-mcp-config` exist. Not verified: `${PWD}` expansion in an `--mcp-config` file, and how the agent behaves without the tools.

Comparing the arms with `jq`, one directory of logs per arm:

```sh
# calls per tool
jq -r 'select(.ev=="call") | .tool' runs/*.jsonl | sort | uniq -c
# totals: calls, tool time, errors, bytes returned
jq -s '[.[]|select(.ev=="call")] | {calls:length, ms:(map(.durationMs)|add), errors:(map(select(.isError))|length), chars:(map(.resultChars)|add)}' runs/*.jsonl
# error rate per tool
jq -s '[.[]|select(.ev=="call")] | group_by(.tool) | map({tool:.[0].tool, calls:length, errors:(map(select(.isError))|length)})' runs/*.jsonl
# slowest ten calls
jq -s '[.[]|select(.ev=="call")] | sort_by(-.durationMs) | .[:10] | map({tool,operation,durationMs})' runs/*.jsonl
```
