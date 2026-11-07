# Headless SharpRail in Claude Code on the web

Status: the install script works on arm64 Linux today. It cannot work in the cloud VM yet, because the VM is x86_64 and the published image has no amd64 build. See [Limits](#limits).

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
3. Open **Setup script** and paste the short version. It downloads the script from the repo, pinned to a commit. Replace `<commit>` with a commit that contains `scripts/headless-cloud-setup.sh`.

   ```bash
   #!/bin/bash
   export SHARPRAIL_WARM=1
   curl -fsSL https://raw.githubusercontent.com/CommanderTvis/sharprail/<commit>/scripts/headless-cloud-setup.sh | bash
   ```

   Or paste the whole of [scripts/headless-cloud-setup.sh](../scripts/headless-cloud-setup.sh). It is 59 lines, far under the 64 KB limit, and needs `curl`, `jq`, `tar` and `sha256sum`. It does this:
   - picks the image layer for the machine's architecture from the manifest on `ghcr.io`, with no Docker and no login,
   - checks the layer's sha256 against the digest in the manifest,
   - installs the app in `~/.local/share/sharprail-headless` and links `~/.local/bin/sharprail-headless`,
   - installs `libicu74` with `apt-get` if the VM lacks it,
   - runs `warm csharp` if `SHARPRAIL_WARM=1` (downloads about 650 MB),
   - runs `claude mcp add --scope user sharprail ...` if the `claude` CLI is there, else prints the `.mcp.json` below.

   `SHARPRAIL_IMAGE_TAG` picks the image. It is a tag or a `sha256:` digest and defaults to `nightly`. `nightly` moves, so pin a `sha-<commit>` tag or a digest for an experiment.
4. Commit a `.mcp.json` to the repo, because the docs promise that a repo's `.mcp.json` is loaded in a one-repository session, and do not promise that a server added by `claude mcp add` in the setup script is kept.

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

- The cloud VM is Ubuntu 24.04 on x86_64. The published `:nightly` image has only linux/arm64, so the script stops with `has no linux/amd64 image (it has: arm64)`. The old `:sha-3f8b5b6` has amd64 but predates `warm` and `.sharprail/mcp.toml`.
- Docker exists in the VM, but Docker is not needed here and is not used.
- The package on ghcr.io must be public. The script uses an anonymous token.
- LSP on a huge repository may not finish. The first `lsp_*` call may answer `initializing`.

### Where the binary can come from

- Image layers from ghcr.io with plain `curl` (what the script does): no Docker, needs the two registry domains above, and needs an amd64 image to exist.
- A GitHub release asset: a `sharprail-headless-linux-x64.tar.gz` of `dotnet publish src/SharpRail.Host.Headless -c Release -r linux-x64 --self-contained -p:PublishSingleFile=false`, uploaded with `gh release upload <tag> <file>`. Only `github.com` and `release-assets.githubusercontent.com` are needed, both in the default list. It does not exist yet.
- A build from source in the VM: needs the .NET SDK (not pre-installed) and a long build, so it is not worth it.

## Verified and unverified

Verified (arm64 Linux, Apple `container`, `ubuntu:24.04`, on 2026-11-07):
- The script installs from `:nightly`, checks the layer digest, is idempotent on a second run, and `sharprail-headless --help` and `mcp` start. It prints the `.mcp.json` fallback when there is no `claude` CLI.
- The `:nightly` manifest list has an arm64 entry and no amd64 entry, so the script fails with a clear message on x86_64.
- `claude mcp add --scope user --env K=V --transport stdio sharprail -- <command> mcp` is accepted by the local CLI and writes the server into `~/.claude.json`.
- From the docs: Ubuntu 24.04 x86_64 VM, Docker present, setup script runs as root before Claude starts, 64 KB limit, five-minute cache rule, `ghcr.io`, `api.nuget.org` and `raw.githubusercontent.com` in the default list, the repo's `.mcp.json` is loaded.

Unverified:
- Everything on a real cloud VM, including x86_64 execution of the app.
- Whether `builds.dotnet.microsoft.com`, `pkg-containers.githubusercontent.com` and `globalcdn.nuget.org` are the only extra domains needed. They are what the code and the registry redirect use, and none is in the default list.
- Whether the `claude` CLI exists when the setup script runs, and whether a user-scope server added there survives into the session.
- Whether `.mcp.json` expands `${HOME}` there, and whether the server's working directory is the repo root.
- Whether `raw.githubusercontent.com` serves this repo to the VM (it needs the repo to be public).
- Whether the setup script may run `apt-get` for `libicu74` (the docs say `apt install` works), and whether the VM already has libicu.
- How to get a file out of a finished session.
