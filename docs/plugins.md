# Plugins

Plugins add tools to the workbench and connect terminal agents to the workspace. Turn them on or off in Settings ›
Plugins without restarting the app, where they are grouped by the categories below.

## Builtin plugins

### Agents

| Plugin | Default | What it does |
| --- | --- | --- |
| Claude Code | Off | Runs Claude Code in the terminal with an IDE bridge, live status and a pane for its context, settings, capabilities and account. |
| Codex | Off | Runs OpenAI Codex in the terminal with live status and SharpRail's MCP tools. |
| omp | Off | Very early support. Runs [omp](https://omp.sh) in the terminal with SharpRail's MCP tools and shows its context files. |
| Air Context | Off | Semantic code search through [JetBrains Air Context](https://www.jetbrains.com/air/context/). Indexing is cloud processing that you enable per project. |
| Air Gateway | Off | Starts Claude Code, Codex and omp through [JetBrains Air Gateway](https://www.jetbrains.com/air/gateway/) with the Central CLI, or leaves them on their own accounts. |

### Environment

| Plugin | Default | What it does |
| --- | --- | --- |
| mise | On | Gives every terminal and agent the tools and variables of the workspace's mise configuration, including a new worktree's. |
| direnv | On | Gives every terminal and agent the environment of the workspace's `.envrc`, including a new worktree's. |
| Python | On | Activates the workspace's own `.venv` in every terminal and agent. |
| JavaScript | On | Puts the workspace's `node_modules/.bin` on PATH. |
| JVM | On | Points `JAVA_HOME` and PATH at the JDK that `.sdkmanrc` or `.java-version` chooses. |
| Environment chips | On | Shows the environments applied to a workspace's terminals as chips under each terminal. |

### Specs and documents

| Plugin | Default | What it does |
| --- | --- | --- |
| Visualize | On | Gives the terminal agent a live drawing surface for Mermaid diagrams and option comparisons. |
| Specs | Off | Reads the project's spec graph, shows it in a Specs panel and gives agents tools to maintain it. |
| Blueprint | Off | Authors interactive Blueprint specs and reacts to their edits. |
| EPUB Reader | Off | Opens `.epub` books with a table of contents, scrolling and paginated modes, and search. |

### Version control and integrations

| Plugin | Default | What it does |
| --- | --- | --- |
| Git Graph | On | Shows the project's branch graph, with its commits and worktrees, as a side tool. |
| Tailscale | Off | Pairs clients on your tailnet without copying IP addresses, ports or session tokens. See [Pair over Tailscale](tailscale.md). |
| Discord | Off | Shows what you're working on as Discord Rich Presence. |

### Language servers

Each language has its own plugin, enabled by default, and terminal agents reach the servers through MCP.

| Language | Server |
| --- | --- |
| C and C++ | clangd |
| C# | Roslyn |
| CSS | VS Code CSS |
| Dart | Analysis Server |
| Go | gopls |
| Java | Eclipse JDT |
| Java and Kotlin | IntelliJ |
| JavaScript and TypeScript | TypeScript LS |
| Kotlin | Kotlin LS |
| PHP | Intelephense |
| PowerShell | PowerShell Editor Services |
| Python | Pyright |
| R | languageserver |
| Ruby | Ruby LS |
| Rust | rust-analyzer |
| Scala | Metals |
| Shell | Bash LS |
| Swift | SourceKit-LS |

Managed servers are downloaded at runtime and pinned, and the IntelliJ server asks you to accept its terms. Dart and
Swift run the SDK's own executable when it is allowed for that server.

## Notes

- The agent plugins integrate external command-line tools, which must be installed on the machine running the
  workspace.
- Agent sessions run in terminals; the Claude Code plugin can also present a session as a chat tab.
- PDFs preview with built-in support and need no plugin.
- Android includes the builtin plugin interfaces. Plugin UI beyond the default workbench has not yet been verified on a
  device.

[Agents](agents.md) explains how the agent plugins, omp's profile and Air Gateway work.

## External plugins

- Install managed plugins under `<stateDir>/plugins/<id>/`, or configure additional plugin paths.
- External plugins arrive disabled until you enable them.
- They can contribute side tools, launchers, file viewers, icons, settings and MCP tools through the same API as the
  builtins.
- Plugin code runs with the host or client's permissions, so install plugins you trust.

The [Plugin API contract](../src/SharpRail.Plugins.Api/SPEC.md) documents the extension points and installation format.
