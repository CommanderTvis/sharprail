# Headless tereditor in Claude Code on the web

A cloud session of Claude Code (claude.ai/code) can use the `lsp_*` tools of [headless tereditor](headless.md), and the `spec_*` and `blueprint_check` tools when you opt in with `TEREDITOR_TOOLS_ENABLE=spec,blueprint` (add it as `env` in the `.mcp.json` below). The idea is one setup script in the cloud environment that installs `tereditor-headless` and registers it as an MCP server. The main use is research, for example whether the language tools help a cloud agent.

## Steps

1. On claude.ai/code open **New environment**, tab **Cloud**, and name it.
2. Set **Network access** to **Custom**, tick **Also include default list of common package managers** and add these domains, one per line. `api.nuget.org` is already in the default list. The others are not.

   ```text
   builds.dotnet.microsoft.com
   globalcdn.nuget.org
   github.com
   release-assets.githubusercontent.com
   objects.githubusercontent.com
   registry.npmjs.org
   download.jetbrains.com
   download-cdn.jetbrains.com
   ```

   `github.com` and the two `githubusercontent.com` hosts serve the setup script's download, a release asset of the repository. The rest are for the runtimes and language servers, which download on the first `lsp_*` call for a language (or in `warm`): the .NET SDK and Roslyn for C#, Bun from GitHub releases and the npm packages for TypeScript, JavaScript, Python, CSS, shell and PHP, clangd from GitHub (not on linux/arm64, where only the Docker image carries it), and the Kotlin server from JetBrains. `ghcr.io` and `pkg-containers.githubusercontent.com` are no longer needed: the script does not use the container image. Drop `pkg-containers.githubusercontent.com` from an older setup; `ghcr.io` is in the default list and can stay. Add only the ones for the languages you use. Without them that language answers `initializing` and never becomes ready. Which languages ship, and which do not, is in [headless.md](headless.md#languages).
3. Open **Setup script** and paste the short version. It downloads the script from the repo, pinned to a commit. The commit shown contains `scripts/headless-cloud-setup.sh`; use a newer one to get later fixes.

   ```bash
   #!/bin/bash
   export TEREDITOR_WARM=1
   curl -fsSL https://raw.githubusercontent.com/CommanderTvis/tereditor/101a12c2c7e1a1705aeef282658b4d4786b5ec17/scripts/headless-cloud-setup.sh | bash
   ```

   Or paste the whole of [scripts/headless-cloud-setup.sh](../scripts/headless-cloud-setup.sh). It is about 150 lines, far under the 64 KB limit, and needs `curl`, `jq`, `tar` and `sha256sum`. It does this:
   - downloads one archive from the `nightly` release, `tereditor-headless-nightly-linux-x64.tar.gz` or `-arm64.tar.gz` for the machine, with no Docker and no login,
   - checks the archive's sha256 against the release's `SHA256SUMS` (or against the hash you pin) and stops if it differs, with no fallback to another source,
   - installs the app in `~/.local/share/tereditor-headless` and links `~/.local/bin/tereditor-headless`,
   - installs `libicu74` with `apt-get` if the VM lacks it,
   - runs `warm csharp` if `TEREDITOR_WARM=1` (downloads about 650 MB),
   - writes the usage notes (below) into `~/.claude/CLAUDE.md`,
   - registers the MCP server `tereditor` in Claude Code's user config, and sets `MCP_TIMEOUT` in `~/.claude/settings.json`.

   `TEREDITOR_HEADLESS_SHA256` pins the archive. Set it to the sha256 of the archive you checked, and the script installs exactly that file or fails. The `nightly` release is replaced every night and old archives are not kept, so a pin works while that archive is still on the release; keep your own copy and set `TEREDITOR_HEADLESS_URL_BASE` to where it is for an experiment that must run again later. That variable defaults to `https://github.com/CommanderTvis/tereditor/releases/download/nightly` and is also for mirrors. The archive holds the same files as the Docker image (the binary, `plugins`, `shell-integration` and the helper programs) under one folder, `tereditor-headless/`. The Docker image stays for Docker users, see [headless.md](headless.md).

   That is the whole setup: paste the script, nothing else. No `.mcp.json` in the repo and no Docker.
4. The script runs before Claude Code starts, so the session picks up what it writes in the VM's home:
   - The server, with the supported `claude mcp add-json --scope user tereditor ...` when the `claude` CLI exists, else by merging the entry into `~/.claude.json` with `jq`. The entry is `{"type":"stdio","command":"<home>/.local/bin/tereditor-headless","args":["mcp"],"env":{"TEREDITOR_TOOLCHAINS":"managed"},"alwaysLoad":true}`. The key is `tereditor`, so the tools are `mcp__tereditor__lsp_navigate` and so on. `alwaysLoad` puts them into the first prompt instead of behind `ToolSearch` (startup then waits for the server, at most 5 s, `MCP_CONNECT_TIMEOUT_MS`; the binary starts in about 20 ms).
   - `MCP_TIMEOUT=300000` in the `env` block of `~/.claude/settings.json` (the default is 30 s), unless the file already sets it. `MCP_TOOL_TIMEOUT` stays at its default of about 28 hours. Other keys of both files are kept; a file that is not a JSON object is left alone. A second run changes nothing.
   - Short usage notes between marker lines in `~/.claude/CLAUDE.md`: the tools are `mcp__tereditor__*`, if a first search finds none then search again, use `lsp_navigate` before grep and run `lsp_diagnostics` on edited files, `initializing` means call again, and the languages with a server (taken from the installed binary). It replaces its own block on each run and keeps your text. Set `TEREDITOR_CLAUDE_MD=0` to skip it, for example for a run that must not be told to prefer the tools.

   The same name in several places: Claude Code matches servers by name across local, project and user scope, connects once and uses the whole entry of the highest one (local, then project, then user). A `tereditor` entry in the repo's `.mcp.json` therefore wins over this one and is not merged with it, so either leave it out or give it the same `alwaysLoad`. `/mcp` warns when same-named entries differ.


   If `MCP_TIMEOUT` matters to you, also set it in the environment's own variables (next step).
5. Add **Environment variables** if you need them: `TEREDITOR_ARM=nolsp`, `TEREDITOR_LOG=1`, `TEREDITOR_LOG_LEVEL=args`, and optionally `MCP_TIMEOUT=300000`. Anyone who can use the environment can read them, so no secrets. A committed `.tereditor/mcp.toml` gives arms or the call log (see [headless.md](headless.md#tool-switches-and-the-call-log)); the server runs in the repo directory, so no `--workspace` is needed. In a session without a repository it runs in `/home/user`; a project below it is reached with paths such as `demo/Shapes.cs`, or serve it directly with `TEREDITOR_ROOT=/home/user/demo` (or `--workspace`) in the server's environment. The server states its root in `initialize` and `lsp_status`.
6. Start a session in a repository with this environment, run `/mcp` and look for `tereditor` with its tool count. The server connects a moment after the session starts, so a first message sent right away may see no tools; check `/mcp` or `claude mcp list` once before the first real task.

## Cost and caching

The script runs once per environment and its result is cached as a filesystem snapshot, but only if it finishes in about five minutes. The cache is rebuilt when you edit the script or the allowed domains, and after about seven days. The `warm` download is the slow part (about 6 minutes cold on a slow link, so it may keep the environment from being cached). If that happens, leave `TEREDITOR_WARM` unset: the first `lsp_*` call then installs the SDK in the session, answers `initializing` for a while, and a warm start takes about 6 s.

## The call log

With `TEREDITOR_LOG=1` the server writes `.tereditor/runs/<session>.jsonl` inside the VM. The VM is discarded when the session ends, so to keep the log, ask Claude to print it at the end or to commit it on the session's branch.

## Limits

- The nightly release must be public. The archive is the last night's build, and `SHA256SUMS` lists every file of the release. If the arm64 build failed that night, the release has no arm64 archive and the script says so.
- LSP on a huge repository may not finish. The first `lsp_*` call may answer `initializing`.
