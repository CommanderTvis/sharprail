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
  documented `--approval-mode` values `always-ask`, `write`, `yolo`, and for `--model` the built-in roles as `@role`
  plus the example selectors from the roles page. Blank values pass no flag, leaving omp's configuration in charge.
- MCP: omp has no per-launch MCP flag or config-file variable. Launches with MCP on use the omp profile `sharprail`,
  whose `~/.omp/profiles/sharprail/agent/mcp.json` SharpRail owns and rewrites whole (atomically, only when it
  differs) on activation, on settings changes and for each terminal: one `sharprail` HTTP server whose URL is
  `${THINKRAIL_MCP_URL}`. omp expands `${VAR}` when it loads the file, so one static file serves every terminal's own
  MCP endpoint. The profile replaces omp's global scope: it has its own sign-in, settings, sessions, caches and
  user-level context files (the docs document no way to share auth between profiles). The pane says so; users run
  `/login` once in a SharpRail omp tab or use provider API key variables, or turn MCP off to launch with their default
  profile and no SharpRail tools.
- Code intelligence: by default launches pass `--no-lsp`, so sessions use SharpRail's shared language servers through
  the `lsp_*` MCP tools instead of starting omp's own; the setting keeps omp's servers. The pane says which applies.
  A possible follow-up is bridging omp's `lsp.json` to SharpRail's servers with a proxy command, so omp's built-in
  `lsp` tool reaches the shared servers.
- Sessions: omp cannot take a caller-chosen session id. Each UI launch gets its own session folder,
  `--session-dir "$SHARPRAIL_OMP_SESSIONS/<32-hex id>"`, where the host sets `SHARPRAIL_OMP_SESSIONS` in every
  terminal to `<plugin state>/sessions` under `~/.sharprail`. The agent record's session id is that folder name, read
  from the process arguments. Revival resumes the folder's newest `*.jsonl` with `-r <file>`, or starts again in the
  folder when none was saved; the generic launcher's resume continues with `-c` in the folder. Sessions in these
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
- Air Governance does not govern omp: Central's `central run` (1.13.0) offers junie, claude-code, codex, gemini-cli,
  pi and copilot, and no way to name another binary, so omp launches are never wrapped. Adding omp needs Central
  support first.

## Not yet

Hook-based status (running/blocked/done), account and usage, SharpRail's appended instructions and skills, IDE
context, and a settings editor for omp's `config.yml` are out of this basic integration.

## Checks

`--omp` (gate cases `omp-host`, `omp-ui`): launch argv for profile, session folder, model, approval mode, advisor and
`--no-lsp` (and its omission), resume lines and Windows; context resolution with nested, cycle-safe, depth-limited
imports, shadowing, ignored code/e-mail tokens and offers; the profile MCP file; the version probe against a fixture
`omp` and a missing one; zombie-safe detection; newest-session lookup; Air Governance's stance; and end to end the
Early badge, version, notes, nested rows, an Add offer, the start action's argv and the missing-binary state. The
Settings › Plugins card badge is covered in `--plugins`.
