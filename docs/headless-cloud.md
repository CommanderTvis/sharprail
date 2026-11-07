# Headless SharpRail in Claude Code on the web

Status: the install script works on arm64 and x86_64 Linux in tests. The published image has both linux/amd64 and linux/arm64 under one tag. It is not yet tried on a real cloud VM. See [Limits](#limits).

A cloud session of Claude Code (claude.ai/code) can use the `lsp_*`, `spec_*` and `blueprint_check` tools of [headless SharpRail](headless.md). The idea is one setup script in the cloud environment that installs `sharprail-headless` and registers it as an MCP server. The main use is research, for example whether the language tools help a cloud agent.

## Steps

1. On claude.ai/code open **New environment**, tab **Cloud**, and name it.
2. Set **Network access** to **Custom**, tick **Also include default list of common package managers** and add these domains, one per line. `ghcr.io` and `api.nuget.org` are already in the default list. The others are not.

   ```text
   pkg-containers.githubusercontent.com
   builds.dotnet.microsoft.com
   globalcdn.nuget.org
   ```

   `pkg-containers.githubusercontent.com` serves the image layers behind `ghcr.io`. The last two are for the pinned .NET SDK and the C# language server, which download on the first `lsp_*` call (or in `warm`). Without them the tools answer `initializing` and never become ready.
3. Open **Setup script** and paste the short version. It downloads the script from the repo, pinned to a commit. The commit shown contains `scripts/headless-cloud-setup.sh`; use a newer one to get later fixes.

   ```bash
   #!/bin/bash
   export SHARPRAIL_WARM=1
   curl -fsSL https://raw.githubusercontent.com/CommanderTvis/sharprail/101a12c2c7e1a1705aeef282658b4d4786b5ec17/scripts/headless-cloud-setup.sh | bash
   ```

   Or paste the whole of [scripts/headless-cloud-setup.sh](../scripts/headless-cloud-setup.sh). It is 59 lines, far under the 64 KB limit, and needs `curl`, `jq`, `tar` and `sha256sum`. It does this:
   - picks the image layer for the machine's architecture from the manifest on `ghcr.io`, with no Docker and no login,
   - checks the layer's sha256 against the digest in the manifest,
   - installs the app in `~/.local/share/sharprail-headless` and links `~/.local/bin/sharprail-headless`,
   - installs `libicu74` with `apt-get` if the VM lacks it,
   - runs `warm csharp` if `SHARPRAIL_WARM=1` (downloads about 650 MB),
   - runs `claude mcp add --scope user sharprail ...` if the `claude` CLI is there, else prints the `.mcp.json` below.

   `SHARPRAIL_IMAGE_TAG` picks the image. It is a tag or a `sha256:` digest and defaults to `nightly`. `nightly` moves, so pin a `sha-<commit>` tag or a digest for an experiment.
4. The script also writes the launcher's code-intelligence guidance, adapted to the headless server (use `lsp_navigate` before grep, run `lsp_diagnostics` on edited files, paths relative to the root, `initializing` means call again, C# only), between marker lines in `~/.claude/CLAUDE.md`. It replaces its own block on each run and keeps your text. Set `SHARPRAIL_CLAUDE_MD=0` to skip it, for example for a run that must not be told to prefer the tools. In the first real run the agent's first diagnostics call used a path relative to the wrong root, which this text now explains.
5. Optional: commit a `.mcp.json` to the repo too. The docs promise that a repo's `.mcp.json` is loaded in a one-repository session. The server added by `claude mcp add` in the setup script reached the session in the first real run (see the end of this page), but the docs do not promise that, so the committed file is the documented path and a backup.

   ```json
   {
     "mcpServers": {
       "sharprail": {
         "command": "${HOME}/.local/bin/sharprail-headless",
         "args": ["mcp"],
         "env": {"SHARPRAIL_TOOLCHAINS": "managed"}
       }
     }
   }
   ```

   Also commit `.sharprail/mcp.toml` if you want arms or the call log (see [headless.md](headless.md#tool-switches-and-the-call-log)). The server runs in the repo directory, so no `--workspace` is needed. If both the script and the file register `sharprail`, Claude Code warns about the same name in two scopes. Keep one of them.
5. Add **Environment variables** if you need them: `SHARPRAIL_ARM=nolsp`, `SHARPRAIL_LOG=1`, `SHARPRAIL_LOG_LEVEL=args`. Anyone who can use the environment can read them, so no secrets.
6. Start a session in a repository with this environment, run `/mcp` and look for `sharprail` with its tool count. A project server from `.mcp.json` may ask for approval first.

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

Verified on a real cloud VM (claude.ai/code, first run, 2026-11-07, environment without a repository, working directory `/home/user`):
- The setup script ran as root and finished with exit 0. It chose `linux/amd64`, verified the app layer (`sha256:325ee39c...`), installed `/root/.local/bin/sharprail-headless`, found the `claude` CLI and ran `claude mcp add`, which reported "Added stdio MCP server sharprail" and "File modified: /root/.claude.json". So the VM is x86_64, `ghcr.io` and its blob host are reachable with the allowlist from step 2, and `claude` exists at setup time.
- The first message in the session, sent right after start, got "no SharpRail tools". That was timing: the MCP server connects after the session starts. A later message ran `claude mcp list`, which showed `sharprail: /root/.local/bin/sharprail-headless mcp - Connected`, and the agent then called the tools: all 11 were listed (`lsp_status`, `lsp_navigate`, `lsp_diagnostics`, the seven `spec_*` tools and `blueprint_check`), and `lsp_status` returned `state: ready` with no language server configured, because the working directory `/home/user` had no project files. So a user-scope server added by the setup script does reach the session, and the amd64 binary starts on a real x86_64 machine. Tell the agent to check `/mcp` or `claude mcp list` once before the first real task.

Unverified:
- The NativeAOT binary on Linux. Everything verified above ran the earlier JIT build. The native binary has run only on macOS (arm64): `check-config`, initialize, tools/list and three tool calls. The next image built by hand is its first Linux run.
- Everything on a real cloud VM beyond the setup script above, including the amd64 binary starting there (the setup script does not start it).
- Whether `builds.dotnet.microsoft.com`, `pkg-containers.githubusercontent.com` and `globalcdn.nuget.org` are the only extra domains needed. They are what the code and the registry redirect use, and none is in the default list.
- Whether `.mcp.json` expands `${HOME}` there, and whether the server's working directory is the repo root.
- Whether `raw.githubusercontent.com` serves this repo to the VM (it needs the repo to be public).
- Whether the setup script may run `apt-get` for `libicu74` (the docs say `apt install` works), and whether the VM already has libicu.
- How to get a file out of a finished session.
