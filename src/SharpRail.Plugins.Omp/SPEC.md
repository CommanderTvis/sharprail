# omp builtin

Basic ("Early support") integration of the omp coding agent (https://omp.sh). Disabled by default. The manifest
declares `Support = early`, which Settings › Plugins shows as an "Early support" badge on the card; the side tool and
Settings › omp wear the same badge. Host and UI talk only through the plugin contract (wire version 1). SharpRail never
installs omp: it runs the `omp` the host finds on PATH (or the configured command).

Facts below come from omp's docs: CLI https://omp.sh/docs/cli, context files https://omp.sh/docs/context-files,
MCP https://omp.sh/docs/mcp, settings and profiles https://omp.sh/docs/settings and https://omp.sh/docs/env, sessions
https://omp.sh/docs/sessions, roles https://omp.sh/docs/roles, code intelligence https://omp.sh/docs/code-intelligence.
No real omp run backs them; the checks use a fixture `omp`.

## Acceptance contract

- Launch line (`OmpLaunch.Line`, shared by the launcher, the start action and the revive prefill):
  `[SHARPRAIL_UI_LAUNCH=omp] omp [--profile sharprail] [--session-dir <dir>] [-r <file> | -c] [--model <m>]
  [--approval-mode <mode>] [--advisor] [--no-lsp] [--append-system-prompt <text>] [@image…] [prompt]`.
  Other plugins' launch contributions wrap the executable word as for the other agents.
- Settings: command (default `omp`), model, approval mode, advisor, "Give sessions SharpRail's tools" (default on) and
  "Use omp's own code intelligence" (default off). Model and approval mode are free text with suggestions: the
  `--approval-mode` values `omp --help` lists (omp 18.12.6: `always-ask`, `always-ask-cached`, `write`, `yolo`), and
  for `--model` the built-in roles as `@role`
  plus the example selectors from the roles page. Blank values pass no flag, leaving omp's configuration in charge.
- MCP: omp has no per-launch MCP flag or config-file variable. Launches with MCP on use the omp profile `sharprail`,
  whose `~/.omp/profiles/sharprail/agent/mcp.json` SharpRail owns and rewrites whole (atomically, only when it
  differs) on activation, on settings changes and for each terminal: one `sharprail` HTTP server whose URL is
  `${THINKRAIL_MCP_URL}`. omp expands `${VAR}` when it loads the file, so one static file serves every terminal's own
  MCP endpoint. The profile replaces omp's global scope: it has its own sign-in, settings, sessions, caches and
  user-level context files. The pane says so; users run `/login` once in a SharpRail omp tab, use provider API key
  variables, turn MCP off to launch with their default profile and no SharpRail tools, or press "Use my omp sign-in in
  SharpRail" (Settings › omp points to it).
- Sign-in import: the button shows only while omp is installed and MCP is on, and runs only when pressed, never
  automatically. The host (off the UI thread, one run at a time, 60 s bound) pipes `omp auth export` (default profile;
  an inherited `OMP_PROFILE` is removed) into `omp --profile sharprail auth import -` (syntax from `omp auth --help`,
  omp 18.12.6: export writes JSON to stdout, `-` reads stdin). The credentials stream process to process: SharpRail
  never writes them to disk or logs and never sends them to clients; the result carries only success or the first
  error line, at most 160 characters with token-like runs masked. The button is disabled while running and the pane
  shows progress, then the result. Any authenticated client may trigger it; it changes the host's own omp profile.
  `SHARPRAIL_OMP_HOME` also becomes omp's `HOME` for these runs, so checks never touch the real `~/.omp`.
- Code intelligence: by default launches pass `--no-lsp`, so sessions use SharpRail's shared language servers through
  the `lsp_*` MCP tools instead of starting omp's own; the setting keeps omp's servers. The pane says which applies.
  A possible follow-up is bridging omp's `lsp.json` to SharpRail's servers with a proxy command, so omp's built-in
  `lsp` tool reaches the shared servers.
- Sessions: omp cannot take a caller-chosen session id. Each UI launch gets its own session folder,
  `--session-dir "$SHARPRAIL_OMP_SESSIONS/<32-hex id>"`, where the host sets `SHARPRAIL_OMP_SESSIONS` in every
  terminal to `<plugin state>/sessions` under `~/.sharprail`. The agent record's session id is that folder name, read
  from the process arguments. Revival resumes the folder's newest `*.jsonl` with `-r <file>`, or starts again in the
  folder when none was saved; the generic launcher's resume continues with `-c` in the folder. The host offers the
  revival and runs it only on Resume (Host Plugins.SPEC, Terminals); the hook's `Fresh` command starts in the same
  folder without `-r`. Sessions in these
  folders do not appear in omp's own session picker. Windows launches keep omp's default store.
- Context files (host, off the UI thread): per scope and directory depth the highest-priority provider with a file
  wins (native `.omp/AGENTS.md`/`AGENTS.md`, claude `.claude/CLAUDE.md`, agents `.agent(s)/AGENTS.md`, gemini
  `.gemini/GEMINI.md`, github `.github/copilot-instructions.md`; ancestors walk agents then `AGENTS.md`; user scope
  native, then `~/.codex/AGENTS.md`, then `~/.config/opencode/AGENTS.md`). Shadowed files are listed and marked.
  `RULES.md` applies at user and project level; `SYSTEM.md`/`APPEND_SYSTEM.md` come from `.omp/` before the user
  directory. User-level files come from the profile's agent directory while MCP is on. Rows are ordered most general
  first with a token estimate (bytes / 4). `@path` imports (at a line start or after whitespace, outside inline code and
  fences, relative to the importing file, `~/` and absolute accepted) are nested rows under their importer, at most five
  hops; a file already on the import chain is shown as a cycle and not followed; an unreadable import is not a row.
  The pane offers "Add AGENTS.md", "Add .omp/RULES.md" and "Add .omp/APPEND_SYSTEM.md" when missing; adding never
  overwrites an existing file. This model follows the docs, not omp's code.
- Version and missing binary: the host runs `<command> --version` on a pool thread with a 10 s bound. The pane shows
  `v<version>`, or "omp is not installed" with the reason and an "Install from omp.sh" button; the launcher reports
  itself unavailable with the same hint.
- Agent detection: a 2.5 s process poll (a copy of the Codex plugin's) records an `omp` descendant of a tab's shell
  as an `omp` agent record and clears it when the process leaves; zombie (`Z`) rows are skipped. A record of another
  agent kind is left alone. If an omp build runs under another process name (for example its runtime's), it is not
  detected; this is not verified against a real install.
- Air Governance can govern omp (its `omp` switch, with Central chosen): Central's `central run` (1.13.0) has no omp
  agent, so Air Governance contributes Central's Pi wire as `ANTHROPIC_BASE_URL`/`ANTHROPIC_API_KEY` and a proxy start
  before omp, read from Central's Pi integration (`central add pi`); without it the launch fails in the shell. Only
  Anthropic models are governed. Details and the real-run evidence are in Air Governance's SPEC; the launch line
  applies the contribution like any other.
- Mark: the manifest icon is `asset:omp.svg`, omp's π mark from the omp.sh site header redrawn as one `currentColor`
  path, so Settings › omp, the plugin card, the side tool, its pane tab and omp terminal tabs (workbench and Projects)
  wear it. The launcher's icons and the start action draw it in a theme brush and follow theme changes; the kit's
  terminal glyph stands in until the asset is read.

## Not yet

Hook-based status (running/blocked/done), account and usage, SharpRail's appended instructions (so no generated
instructions file) and skills, IDE
context, and a settings editor for omp's `config.yml` are out of this basic integration.

## Checks

`--omp` (gate cases `omp-host`, `omp-ui`): launch argv for profile, session folder, model, approval mode, advisor and
`--no-lsp` (and its omission), resume lines and Windows; context resolution with nested, cycle-safe, depth-limited
imports, shadowing, ignored code/e-mail tokens and offers; the profile MCP file; the version probe against a fixture
`omp` and a missing one; zombie-safe detection; newest-session lookup; a governed launch run by a real shell against fake `omp` and `central` (proxy started, wire
in omp's environment, arguments unchanged; none when direct; blocked without Central's Pi integration, which is only read); and end to end the
Early badge, version, notes, nested rows, an Add offer, the start action's argv, the omp mark (not the terminal glyph) on the start action and an omp terminal tab, the sign-in import against a fixture
`omp` (data lands in the fixture profile, nothing else persisted, a failure shows a masked message) and the
missing-binary state. The
Settings › Plugins card badge is covered in `--plugins`.
