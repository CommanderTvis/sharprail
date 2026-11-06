# Headless SharpRail for AI experiments

Status: design under review. Nothing on this page works yet. The full plan is in
[Headless design](develop/headless-design.md).

SharpRail can run without a window. It serves its language, spec and Blueprint tools to an agent over MCP, from a small
container. The main use is research: does the LSP toolset help an agent do a task?

## The idea

Add one entry to your project's `.mcp.json`:

```json
{
  "mcpServers": {
    "sharprail": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "--init",
               "-v", "${PWD}:/workspace",
               "-v", "sharprail-cache:/cache",
               "ghcr.io/commandertvis/sharprail:0.1.0"]
    }
  }
}
```

Each Claude Code session starts its own container and talks to it over stdin and stdout. There are no ports and no tokens.
Language servers are installed on first use into the `sharprail-cache` volume and reused after that.

## Configure it

Settings live in your project's `.sharprail/` folder, one TOML file per area.

| File | What it sets |
| --- | --- |
| `language.toml` | Language server choices and options (exists today) |
| `host.toml` | Offline mode, language limits, which plugins run |
| `mcp.toml` | Which tools are on, named arms, the run log |
| `plugins/<id>.toml` | One plugin's settings |

## Compare two runs

Turn LSP tools off in one arm with `tools.disable` in `mcp.toml`, switch on the run log, and run the same task twice from the
same commit. The log lands in `.sharprail/runs/<session>.jsonl`. It is off unless you enable it. See the worked example and
the `jq` recipes in the design page.
