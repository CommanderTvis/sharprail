# Headless SharpRail in Claude Code on the web

Status: the install script works on arm64 and x86_64 Linux in tests. The published image has both linux/amd64 and linux/arm64 under one tag. It is not yet tried on a real cloud VM. See [Limits](#limits).

A cloud session of Claude Code (claude.ai/code) can use the `lsp_*` tools of [headless SharpRail](headless.md), and the `spec_*` and `blueprint_check` tools when you opt in with `SHARPRAIL_TOOLS_ENABLE=spec,blueprint` (add it as `env` in the `.mcp.json` below). The idea is one setup script in the cloud environment that installs `sharprail-headless` and registers it as an MCP server. The main use is research, for example whether the language tools help a cloud agent.

## Steps

1. On claude.ai/code open **New environment**, tab **Cloud**, and name it.
2. Set **Network access** to **Custom**, tick **Also include default list of common package managers** and add these domains, one per line. `ghcr.io` and `api.nuget.org` are already in the default list. The others are not.

   ```text
   pkg-containers.githubusercontent.com
   builds.dotnet.microsoft.com
   globalcdn.nuget.org
   github.com
   release-assets.githubusercontent.com
   objects.githubusercontent.com
   registry.npmjs.org
   download.jetbrains.com
   download-cdn.jetbrains.com
   ```

   `pkg-containers.githubusercontent.com` serves the image layers behind `ghcr.io`. The rest are for the runtimes and language servers, which download on the first `lsp_*` call for a language (or in `warm`): the .NET SDK and Roslyn for C#, Bun from GitHub releases and the npm packages for TypeScript, JavaScript, Python, CSS, shell and PHP, clangd from GitHub, and the Kotlin server from JetBrains. Add only the ones for the languages you use. Without them that language answers `initializing` and never becomes ready. Which languages ship, and which do not, is in [headless.md](headless.md#languages).
3. Open **Setup script** and paste the short version. It downloads the script from the repo, pinned to a commit. The commit shown contains `scripts/headless-cloud-setup.sh`; use a newer one to get later fixes.

   ```bash
   #!/bin/bash
   export SHARPRAIL_WARM=1
   curl -fsSL https://raw.githubusercontent.com/CommanderTvis/sharprail/101a12c2c7e1a1705aeef282658b4d4786b5ec17/scripts/headless-cloud-setup.sh | bash
   ```

   Or paste the whole of [scripts/headless-cloud-setup.sh](../scripts/headless-cloud-setup.sh). It is about 150 lines, far under the 64 KB limit, and needs `curl`, `jq`, `tar` and `sha256sum`. It does this:
   - picks the image layer for the machine's architecture from the manifest on `ghcr.io`, with no Docker and no login,
   - checks the layer's sha256 against the digest in the manifest,
   - installs the app in `~/.local/share/sharprail-headless` and links `~/.local/bin/sharprail-headless`,
   - installs `libicu74` with `apt-get` if the VM lacks it,
   - runs `warm csharp` if `SHARPRAIL_WARM=1` (downloads about 650 MB),
   - writes the usage notes (below) into `~/.claude/CLAUDE.md`,
   - registers the MCP server `sharprail` in Claude Code's user config, and sets `MCP_TIMEOUT` in `~/.claude/settings.json`.

   `SHARPRAIL_IMAGE_TAG` picks the image. It is a tag or a `sha256:` digest and defaults to `nightly`. `nightly` moves, so pin a `sha-<commit>` tag or a digest for an experiment.

   That is the whole setup: paste the script, nothing else. No `.mcp.json` in the repo and no Docker.
4. The script runs before Claude Code starts (the docs: "a Bash script that runs when a new cloud session starts, before Claude Code launches", and they show a setup script that writes `~/.claude/CLAUDE.md` for the session), so the files it writes in the VM's home are what the session reads. It writes:
   - The server, with the supported `claude mcp add-json --scope user sharprail ...` when the `claude` CLI exists (it accepts and validates `alwaysLoad`, tried with Claude Code 2.1.322), else by merging the entry into `~/.claude.json` with `jq`. The entry is `{"type":"stdio","command":"<home>/.local/bin/sharprail-headless","args":["mcp"],"env":{"SHARPRAIL_TOOLCHAINS":"managed"},"alwaysLoad":true}`. The key is `sharprail`, so the tools are `mcp__sharprail__lsp_navigate` and so on. `alwaysLoad` puts them into the first prompt instead of behind `ToolSearch` (startup then waits for the server, at most 5 s, `MCP_CONNECT_TIMEOUT_MS`; the binary starts in about 20 ms).
   - `MCP_TIMEOUT=300000` in the `env` block of `~/.claude/settings.json` (the default is 30 s), unless the file already sets it. `MCP_TOOL_TIMEOUT` stays at its default of about 28 hours. Other keys of both files are kept; a file that is not a JSON object is left alone. A second run changes nothing (`scripts/headless-cloud-setup.test.sh`, part of the headless check, tests this against a temporary HOME).
   - Short usage notes between marker lines in `~/.claude/CLAUDE.md`: the tools are `mcp__sharprail__*`, if a first search finds none then search again, use `lsp_navigate` before grep and run `lsp_diagnostics` on edited files, `initializing` means call again, and the languages with a server (taken from the installed binary). It replaces its own block on each run and keeps your text. Set `SHARPRAIL_CLAUDE_MD=0` to skip it, for example for a run that must not be told to prefer the tools.

   The same name in several places: Claude Code matches servers by name across local, project and user scope, connects once and uses the whole entry of the highest one (local, then project, then user). A `sharprail` entry in the repo's `.mcp.json` therefore wins over this one and is not merged with it, so either leave it out or give it the same `alwaysLoad`. The docs warn in `/mcp` when same-named entries differ.

   Confirmed by the docs: the setup script runs before Claude Code launches, user-scope servers live in `~/.claude.json`, `alwaysLoad` is a field of an `mcpServers` entry on every server type, `MCP_TIMEOUT` defaults to 30000 ms, `env` is a settings key. Not confirmed by them: that a cloud session reads `~/.claude.json` and `~/.claude/settings.json` written by the setup script. They only say the settings file of your own machine is not read, and their example covers `~/.claude/CLAUDE.md`. The first real run (below) showed that a user-scope server added by the script reached the session. For `MCP_TIMEOUT` the documented place is the environment's own variables (next step), so set it there too if it matters.
5. Add **Environment variables** if you need them: `SHARPRAIL_ARM=nolsp`, `SHARPRAIL_LOG=1`, `SHARPRAIL_LOG_LEVEL=args`, and optionally `MCP_TIMEOUT=300000`. Anyone who can use the environment can read them, so no secrets. A committed `.sharprail/mcp.toml` gives arms or the call log (see [headless.md](headless.md#tool-switches-and-the-call-log)); the server runs in the repo directory, so no `--workspace` is needed. In a session without a repository it runs in `/home/user`; a project below it is reached with paths such as `demo/Shapes.cs`, or serve it directly with `SHARPRAIL_ROOT=/home/user/demo` (or `--workspace`) in the server's environment. The server states its root in `initialize` and `lsp_status`.
6. Start a session in a repository with this environment, run `/mcp` and look for `sharprail` with its tool count.

## Cost and caching

The script runs once per environment and its result is cached as a filesystem snapshot, but only if it finishes in about five minutes. The cache is rebuilt when you edit the script or the allowed domains, and after about seven days. The `warm` download is the slow part (about 6 minutes cold on a slow link, so it may keep the environment from being cached). If that happens, leave `SHARPRAIL_WARM` unset: the first `lsp_*` call then installs the SDK in the session, answers `initializing` for a while, and a warm start takes about 6 s.

## The call log

With `SHARPRAIL_LOG=1` the server writes `.sharprail/runs/<session>.jsonl` inside the VM. The VM is thrown away, and the docs do not describe a way to download a file from it. What is realistic: ask Claude to print the file at the end, or to commit it on the session's branch (add `.sharprail/runs/` to `.git/info/exclude` first otherwise, and force-add it). Both are your own convention, not a SharpRail feature. A log is lost if the session ends without one of them.

## Limits

- The cloud VM is Ubuntu 24.04 on x86_64. The `:nightly` image has linux/amd64 and linux/arm64. CI is off, so the image is published by hand from a Mac, see [Publishing the image](#publishing-the-image). If a tag has no image for the machine, the script stops with `has no linux/<arch> image`.
- Docker exists in the VM, but Docker is not needed here and is not used.
- The package on ghcr.io must be public. The script uses an anonymous token.
- LSP on a huge repository may not finish. The first `lsp_*` call may answer `initializing`.

### Where the binary can come from

- Image layers from ghcr.io with plain `curl` (what the script does): no Docker, needs the two registry domains above.
- A GitHub release asset: a `sharprail-headless-linux-x64.tar.gz` of `sh scripts/mise x -- dotnet publish src/SharpRail.Host.Headless -c Release -r linux-x64`, uploaded with `gh release upload <tag> <file>`. Only `github.com` and `release-assets.githubusercontent.com` are needed, both in the default list. It does not exist yet.
- A build from source in the VM: needs the .NET SDK (not pre-installed) and a long build, so it is not worth it.

## Publishing the image

Done by hand on an Apple silicon Mac with Apple `container` (the user is logged in with `container registry login ghcr.io`). From a checkout of the commit to publish:

```sh
sh scripts/mise x -- dotnet publish src/SharpRail.Host.Headless -c Release -r linux-x64 -o artifacts/headless/linux-amd64
sh scripts/mise x -- dotnet publish src/SharpRail.Host.Headless -c Release -r linux-arm64 -o artifacts/headless/linux-arm64
SHA=$(git rev-parse --short=8 HEAD)
container build -f docker/headless/Dockerfile --platform linux/arm64,linux/amd64 \
  --build-arg REVISION=$(git rev-parse HEAD) --build-arg CREATED=$(date -u +%Y-%m-%dT%H:%M:%SZ) \
  -t ghcr.io/commandertvis/sharprail:nightly -t ghcr.io/commandertvis/sharprail:sha-$SHA .
# smoke test both: container run --rm --platform linux/amd64 ghcr.io/commandertvis/sharprail:nightly --help (and mcp)
container image push ghcr.io/commandertvis/sharprail:nightly
container image push ghcr.io/commandertvis/sharprail:sha-$SHA
```

The binary is NativeAOT, so the publishes run under `scripts/mise`: the pinned Zig links the Linux binaries on the Mac. The Dockerfile picks `artifacts/headless/linux-<TARGETARCH>` by itself. One build makes one manifest list. The `apt-get` step of the amd64 build runs under Rosetta and works. A push can end with a flaky `XPC connection error`: run it again.

## Verified and unverified

Verified (arm64 Linux, Apple `container`, `ubuntu:24.04`, on 2026-11-07):
- The script installs from `:nightly`, checks the layer digest, is idempotent on a second run, and `sharprail-headless --help` and `mcp` start. It prints the `.mcp.json` fallback when there is no `claude` CLI.
- The `:nightly` manifest list on ghcr.io has both amd64 and arm64 entries. The script was run in `ubuntu:24.04` for both machines (`uname -m` gave `aarch64` natively and `x86_64` with `--platform linux/amd64`, which runs under Rosetta). Both times it picked the right entry, the layer sha256 matched, and `sharprail-headless --help` ran.
- The image itself: `--help` and `mcp` (initialize, initialized, tools/list: 2 replies, 11 tools) pass on arm64 natively and on amd64 under Rosetta.
- `claude mcp add --scope user --env K=V --transport stdio sharprail -- <command> mcp` is accepted by the local CLI and writes the server into `~/.claude.json`.
- From the docs: Ubuntu 24.04 x86_64 VM, Docker present, setup script runs as root before Claude starts, 64 KB limit, five-minute cache rule, `ghcr.io`, `api.nuget.org` and `raw.githubusercontent.com` in the default list, the repo's `.mcp.json` is loaded.
- Not verified here: the JavaScript language servers download Bun from `github.com` (release assets) and their packages from `registry.npmjs.org`, neither in that default list, so an environment that limits hosts must allow both; no Node is needed.

Verified on a real cloud VM (claude.ai/code, first run, 2026-11-07, environment without a repository, working directory `/home/user`):
- The setup script ran as root and finished with exit 0. It chose `linux/amd64`, verified the app layer (`sha256:325ee39c...`), installed `/root/.local/bin/sharprail-headless`, found the `claude` CLI and ran `claude mcp add`, which reported "Added stdio MCP server sharprail" and "File modified: /root/.claude.json". So the VM is x86_64, `ghcr.io` and its blob host are reachable with the allowlist from step 2, and `claude` exists at setup time.
- The first message in the session, sent right after start, got "no SharpRail tools". That was timing: the MCP server connects after the session starts. A later message ran `claude mcp list`, which showed `sharprail: /root/.local/bin/sharprail-headless mcp - Connected`, and the agent then called the tools: all 11 were listed (`lsp_status`, `lsp_navigate`, `lsp_diagnostics`, the seven `spec_*` tools and `blueprint_check`), and `lsp_status` returned `state: ready` with no language server configured, because the working directory `/home/user` had no project files. So a user-scope server added by the setup script does reach the session, and the amd64 binary starts on a real x86_64 machine. Tell the agent to check `/mcp` or `claude mcp list` once before the first real task.

Unverified:
- The NativeAOT binary on Linux. Everything verified above ran the earlier JIT build. The native binary has run only on macOS (arm64): `check-config`, initialize, tools/list and three tool calls. The next image built by hand is its first Linux run.
- Everything on a real cloud VM beyond the setup script above, including the amd64 binary starting there (the setup script does not start it).
- Whether `builds.dotnet.microsoft.com`, `pkg-containers.githubusercontent.com` and `globalcdn.nuget.org` are the only extra domains needed. They are what the code and the registry redirect use, and none is in the default list.
- Whether a cloud session reads the `~/.claude.json` and `~/.claude/settings.json` entries (`alwaysLoad`, `MCP_TIMEOUT`) that the setup script now writes (the docs do not say; the plain user-scope server did reach the first real run), and the server's working directory (the repo root is assumed).
- Whether `raw.githubusercontent.com` serves this repo to the VM (it needs the repo to be public).
- Whether the setup script may run `apt-get` for `libicu74` (the docs say `apt install` works), and whether the VM already has libicu.
- How to get a file out of a finished session.
