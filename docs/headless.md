# Headless SharpRail for AI experiments

Status: the stdio server works. It is not published as an image yet, so run it from a build for now.

SharpRail can run without a window. It serves its language, spec and Blueprint tools to an agent over MCP, on stdin and stdout, for one project. The main use is research: does the LSP toolset help an agent do a task?

## Run it

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

Language servers are downloaded on first use into the state directory. Runtimes they need (.NET, Node, a JDK) must already be on the PATH.

## Not built yet

A container image, pinned runtimes, tool switches and a call log.
