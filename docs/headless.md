# Headless SharpRail for AI experiments

Status: the stdio server works and a container image is built by the Container workflow. The package is private until the owner makes it public.

SharpRail can run without a window. It serves its language, spec and Blueprint tools to an agent over MCP, on stdin and stdout, for one project. The main use is research: does the LSP toolset help an agent do a task?

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

That is the exact snippet for Docker. With Apple `container` use a host directory for the cache, because it has no named volumes in this form, and make it writable by uid 1000:

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

To run it by hand: `gh workflow run container.yml -f tag=nightly`. To try the script on a local image: `sh scripts/container-smoke.sh <image>`.

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

## Settings

| Setting | Meaning |
| --- | --- |
| `--workspace <dir>` | The project. Defaults to `SHARPRAIL_ROOT`, then the current directory. |
| `--arm`, `--disable-tools` | Tool switches, see below. |
| `SHARPRAIL_STATE_DIR` | Where settings and language server installs live. Defaults to `~/.sharprail/headless`. |

Tools: `lsp_navigate`, `lsp_diagnostics`, `lsp_status`, `lsp_code_actions`, `lsp_rename`, `lsp_format`, the `spec_*` tools and `blueprint_check`. Paths are relative to the workspace. The optional `workspace` argument is ignored.

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
| C, C++ | clangd, a self-contained archive (linux/amd64, macOS and Windows; no linux/arm64 build exists) | github.com |
| Kotlin | JetBrains Kotlin LS, a self-contained archive | download.jetbrains.com |

The first call of a language answers `initializing` while it downloads: the network must reach the hosts above (github.com, release-assets.githubusercontent.com, registry.npmjs.org and the others in the row). Later runs on the same cache need no network. The image size is unchanged.

Not headless, because the server needs a toolchain that has no pinned download here and the machine would have to provide it:

| Language | Why |
| --- | --- |
| Java | needs a JDK 21 or newer; no JDK is pinned |
| Go | gopls is built with the Go toolchain; Go is not pinned |
| Rust | rust-analyzer needs cargo and rustc to load a project; no Rust toolchain is pinned |
| Scala | Metals is installed with Coursier, which is not pinned |
| Ruby | ruby-lsp is installed with RubyGems; Ruby is not pinned |
| R | needs R; not pinned |
| PowerShell | needs PowerShell 7; not pinned |
| Swift, Dart | no managed installation: they run the machine's own SDK |

They stay desktop-only until a runtime with a checksum is pinned for them. The IntelliJ server and Air Context are not headless either. Kotlin analyses a single file without a JDK; importing a Gradle or Maven project needs a JDK on the machine (not verified in a container).

With `SHARPRAIL_TOOLCHAINS=managed` (the image sets it) the servers use only the pinned runtimes. Outside the image the default is the machine's PATH for .NET, so set it to `managed` there too. The JavaScript-based servers need no Node or npm: they use a pinned Bun, downloaded from GitHub releases into the cache and checked against its SHA-256, and install their packages from registry.npmjs.org.

## Tool switches and the call log

Both live in one file, `.sharprail/mcp.toml` in the project. Without the file, every tool is offered and nothing is logged or written.

```toml
version = 1

[tools]
disable = ["blueprint_check"]        # these tools do not exist for the run
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
