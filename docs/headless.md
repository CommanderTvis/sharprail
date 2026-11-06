# Headless SharpRail for AI experiments

Status: the stdio server works and a container image is built by the Container workflow. The package is private until the owner makes it public.

SharpRail can run without a window. It serves its language, spec and Blueprint tools to an agent over MCP, on stdin and stdout, for one project. The main use is research: does the LSP toolset help an agent do a task?

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

Use `-i` so stdin stays open, and `--init` so SIGTERM and child processes are handled. The image is for linux/amd64 and linux/arm64.

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
| `SHARPRAIL_STATE_DIR` | Where settings and language server installs live. Defaults to `~/.sharprail/headless`. |

Tools: `lsp_navigate`, `lsp_diagnostics`, `lsp_status`, the `spec_*` tools and `blueprint_check`. Paths are relative to the workspace. The optional `workspace` argument is ignored.

Language servers are downloaded on first use into the state directory. Outside the image, runtimes they need (.NET, Node, a JDK) must already be on the PATH, unless `SHARPRAIL_TOOLCHAINS=managed` is set. The image sets it.

## Not built yet

Tool switches and a call log.
