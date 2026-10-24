---
id: module-plugin-claude-code
type: module-design
status: active
title: Claude Code — configuration, IDE bridge and terminal status
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins]
tags: [plugins, claude-code]
---

# Claude Code

Port source: `6401ccae8`, `packages/plugin-claude-code/SPEC.md` and its host/web modules in
CommanderTvis's ThinkRail fork. This plugin integrates the external Claude Code terminal program;
it does not provide bundled AI chat or Pi execution. It is disabled by default.

## Boundary

The root project owns the manifest, typed contract, configuration records and status vocabulary.
Host references only that contract and the plugin host API. UI references the contract, UI API and
shared kit. Neither half imports a host service or app implementation. Core composes the builtin
host; the app composes the builtin UI. Assets publish under `plugins/claude-code/assets`.

The generic plugin call, addressed event and keyed state transports carry this contract through
direct calls or WebSocket RPC. Claude Code's own IDE protocol remains an authenticated loopback WebSocket
MCP server: replacing it with the host RPC protocol would break the external CLI's protocol.

The prompt appendix directs workspace creation through the host's `workspace_create` tool with a
visible task description and encourages cleanup of the agent's own completed workspaces. Reuse a
workspace the user already prepared; create another only for additional isolation or parallel work.
When unrelated tasks or bug reports accumulate, suggest a subagent and separate workspace per
independent task before processing the backlog sequentially, even in a session not started as an
orchestrator. Explain the focused context and separate review benefits; delegation requires
authorization and available subagent tools.
Through the host's `core.workspace-emigrate` section it also describes `workspace_emigrate`: only on the user's explicit
request for a new workspace and with certainty of never touching the original one, the agent may move its own tab into a
workspace it created; the host relaunches it there through this plugin's revive hook (`--resume <id>`). See the
[Workspaces SPEC](../SharpRail.Host.Core/Workspaces.SPEC.md).
It also asks the agent to title its terminal's tab through the host's `set_title` when the task is clear;
the first title also names an untouched generated managed workspace; manual names are preserved. Cleanup goes through the host's `workspace_delete`
rather than `git worktree remove`, so Projects follows immediately.
The appendix also documents the project's `.sharprail/workspace.toml` (schema, root-relative patterns, edit it when adding a shareable dependency or cache directory, effective for workspaces created afterwards).
The appendix permits `shipping-a-pr` only when the user's prompt indicates pull-request work;
a request to commit and/or push alone does not trigger the skill.

POSIX UI launches carry the command-scoped `SHARPRAIL_UI_LAUNCH=claude` marker independently of
the prompt and agent-view settings. Process detection records the launch origin and rechecks it when
the process changes. A manual session shows a persistent terminal notice explaining that typed CLI
commands do not get SharpRail's launcher settings, tools and prompt appendix, with the
workspace launcher offered as the way to apply them. Installed hooks and independently configured
MCP/IDE connections may still work. Unknown origins, including unavailable environment inspection
on Windows, are not labelled manual. Origin persists and crosses local/remote host state; hook reports
retain it. A revival is SharpRail's own launch and always carries the UI marker, whatever origin the
revived session had, so a revived session is never labelled manual. It also reapplies the current
agent-view setting, which the process table the command is rebuilt from does not hold.

Hook reports persist the session's latest model and effort, including fact-only model switches.
Revival uses those recorded choices rather than the defaults for new launches and replaces any
older model/effort flags in the recorded command. Sparse reports preserve known choices; a different
session id starts a fresh set of choices.

Settings supplies optional model and effort defaults for new SharpRail launches, passed with
`--model` and `--effort`. Empty choices use the CLI's defaults. Generic launcher selections and
workspace-launcher arguments take precedence over these defaults without editing Claude's saved
configuration or a running session.
Settings also holds a default permission mode, mirroring Codex's default permissions mode: a chosen
mode adds `--permission-mode <mode>` to every SharpRail launch (workspace launcher and generic
launcher); the empty "Claude Code's default" omits the flag so `permissions.defaultMode` in the
user's `~/.claude/settings.json` (named in that entry's label when set) decides. The workspace
launcher's right-click Permission mode submenu picks a mode for one run, replacing the saved one,
or omits the flag with its default entry; a `--permission-mode` or `--dangerously-skip-permissions`
in the run's own arguments wins over both. The modes are the `choices` of `--permission-mode` in
`claude --help`, probed on the host once per executable (`permissionModesGet`) and kept for the
process; an unreadable help is not cached and yields the fallback default, acceptEdits, plan, auto,
dontAsk, bypassPermissions. A saved mode the CLI no longer lists stays offered. `bypassPermissions`
is labelled dangerous in the danger colour in both places, and Settings turns its note into a
warning while it is the default; like Codex's full-access sandbox it needs no confirmation.
Revival keeps the mode in the recorded command (`--resume` accepts the flag). The running terminal
does not show the mode, as Codex's does not.
Settings likewise holds a default advisor (`--advisor <model>`, a flag the CLI's help hides) and a
"Claude in Chrome: always enable" switch (`--chrome`). The advisor choices are the launcher's model
aliases less haiku (fable, opus, sonnet: the advisor candidates in the CLI bundle; the account may
refuse one at start). None omits the flag, and its entry names `advisorModel` from the user's
`~/.claude/settings.json` when set; Chrome off omits both flags. The right-click menu's Advisor
submenu picks one for a run, and its Chrome toggle adds `--chrome` for a run, or `--no-chrome` while
the setting is on. Arguments of the run's own win. Revival keeps both from the recorded command, and
the terminal shows an advisor chip and a Chrome chip read from that command.
Unset launch defaults persist as empty strings so a merged settings update can clear an earlier override.
Wire version 2 covers these added settings: the strict settings schema would make older UI halves
fall back to defaults, so mismatched host/UI halves stay dormant.


## OSC 133 marks

Where Claude Code draws OSC 133 marks itself (`TerminalPromptChanged` with `Foreground`, for a terminal whose agent
record is Claude's), they set the status, preferred over guessing: a prompt mark Idle (event `prompt_mark`), a command
mark Running (`command_mark`); an idle mark leaves the hook's Blocked, Done or Failed until Claude runs again. The
shell's own marks around Claude and terminals without marks change nothing; the hook keeps reporting either way.
`--plugins` (Claude Code host) and `--codex` check the mapping; `--plugins` also checks the host telling a foreground
program's mark from the shell's.
## Configuration

The pane resolves configuration for the first shown Claude terminal, then the first shown terminal,
then the workspace root. Every configuration/read/write/edit/CLI operation carries the selected
terminal key. The host uses the detected process's current directory, its reported directory, then
the workspace root. Appended system prompt files are additional instruction layers.

SharpRail's own appended prompt lives at `<stateDir>/claude-code/thinkrail-prompt-claude.md`, with each
workspace's composition under `workspaces/`. They are generated, never the user's: activation and every started
terminal regenerate them, overwriting any hand edit, and they open read-only under SharpRail's notice
([Plugins](../SharpRail.Host.Core/Plugins.SPEC.md#agent-instruction-sections)). Their content is shaped only by turning sections off for a project: the context table shows
them as one entry, the shared instructions view (Agent UI `InstructionSectionsView`) with the PROJECT chip, the
appended file's tokens, its path and a muted count (`8 sections · 1 off · Choose…`); the whole row is a button opening a popup (a flyout, so it works on every platform) of the sections Claude's sessions in that workspace get, grouped by
source (SharpRail, then each enabled plugin), each a compact checkbox with title and size and a one-line summary that
discloses the section's read-only text. Ticking writes the project's `.sharprail/instructions.toml` disable-list through
the host (`instructionsList`, `instructionsSet`; schema in [Plugins](../SharpRail.Host.Core/Plugins.SPEC.md#agent-instruction-sections));
the view shows that file's path and that running sessions keep their prompt until
restarted, and it re-reads on `instructionsChanged`. Nothing offers free-text editing. A shared file the user edited
under an earlier version (its SHA-256 stamp no longer matches) is copied once to `thinkrail-prompt-claude.custom.md`
before it is regenerated; that copy is never used or deleted, and the view notes it while it exists.
Spec tools are suggested only when available and the project uses a spec graph; projects without
specs require no spec workflow or unsolicited spec creation.

### Context usage

The Context surface's context table is the one place for baseline costs, in tokens only (no sizes on disk).
Its rows are everything a fresh session started like the tab's pays for, as `/context` counts it: the memory and
instruction files the resolver lists (a file `/context` does not list says "not loaded", an on-demand rule shows a
dash), files `/context` lists that the resolver does not (nested instructions, rules, imports) with its source label,
SharpRail's instructions entry (the appended prompt file; never a separate file row), then Claude Code's own system prompt (less the appended file), built-in tools, MCP
tools, MCP server instructions and skills; deferred tools, the `/context` message itself, free space and the
autocompact reserve are not rows. MCP tools open, in the table's own row style with a chevron, into servers (heaviest
first, with subtotals) and each server into its tools; Skills open into each skill. The header totals the rows as
"N tokens in new session". Every amount says "1 token" for one and abbreviates to "tok"/"toks" where the row is
narrower than the Agent UI `TokenAmount` threshold; a short line above the table says "measured HH:mm", that it is measuring, or why it
failed, with Retry. Before a baseline exists rows show an ellipsis. The host matches memory paths to the resolver's
with `~` expanded, made absolute and through symbolic links.

`/context` (2.1.x, also `/context all` and the JSON reply) does not list an `--append-system-prompt-file` file; it
folds the file into its "System prompt" category. So when the tab's flags append files, the host also runs the same
baseline without them, concurrently, and reports the difference of the two "System prompt" counts
(`AppendedPromptTokens`); with several appended files the first row carries their sum. The bare run is cached on the
entry with the fingerprint of its own inputs (the entry's inputs less the appended files), so editing the appended
file re-runs only the run that reads it; a failed bare run leaves the appended row unmeasured.

The baseline is `claude -p /context --output-format json --no-session-persistence`, whose JSON carries the Markdown
report in `result`; the host parses its tables by section title. It runs on the host that runs Claude, in the tab's
directory, with the tab process's context-shaping flags (`--append-system-prompt-file`, `--plugin-dir`, `--add-dir`,
`--model`, `--effort`, `--mcp-config`, `--settings`, `--setting-sources`, `--system-prompt-file`, `--strict-mcp-config`;
session and prompt arguments are dropped, so the tab's conversation is never touched) or, with no Claude in the tab,
the flags the launcher would pass; with SharpRail's MCP probe address, the agent-view variable and an empty status
URL. Results are cached in host memory per (binary, directory, flags), shared by tabs; a run is background,
single-flight per key, killed after 90 s and cancelled on deactivation, and a failure keeps the previous breakdown
and shows an inline error with Retry. A run starts when a request finds no entry, when the stat fingerprint of its
inputs (the configuration files the resolver names, flag files, user and project settings, `.mcp.json`) differs, or
on Refresh; changes reported by the workspace watch or a watch of Claude's configuration directory re-run an entry
once per 2 s quiet burst, only if its fingerprint changed. A finished run publishes `contextUsage` for the workspace and
clients ask again.

Settings retain the winning source and shadowed values. Context includes user and ancestor
instructions, `AGENTS.md` when a directory has no `CLAUDE.md`, local instructions, rules, imports,
memory and appended prompt files. Capabilities include MCP servers, plugins, skills, agents, hooks
and marketplaces with provenance, disabled sources and discovery problems.

The Settings value composer suggests documented values for keys whose value set is closed
(`UI/ClaudeSettingSuggestions.cs`: `permissions.defaultMode`, `model` aliases, `effortLevel` and
`modelSettings.*.effortLevel`, `theme`, `tui`, `outputStyle`, `forceLoginMethod`,
`autoUpdatesChannel` and similar) as chips that fill the text field; the field still saves any
string, since a newer Claude Code may accept values the table lacks. A `*` segment matches one key
segment. Keys the schema types as boolean are composed as on/off and written unquoted; unknown keys
keep the free shape switch. Sources, cited per row: the settings JSON schema at
https://json.schemastore.org/claude-code-settings.json, https://code.claude.com/docs/en/model-config
and `claude --help` (`--permission-mode` choices). The edit review names its scopes "Your settings",
"Project settings" and "Local project settings", each with the pane's scope chip.

Every mutation is composed, scoped to user/project/local, planned as a diff, reviewed and then
applied with the plan's content hash. Changing scope invalidates its preview; stale files require a
new review. Source links expose only discovered files and reveal JSON key paths in current editor
contents. CLI plugin/marketplace changes show the exact command before execution. SharpRail’s own integration
is loaded only for app-started Claude processes through `--plugin-dir`; activation never installs or updates it in Claude’s user settings.

## Status and recovery

Activation owns the process poll, bridge, terminal environment, revive hook and status route.
Disable removes them and stops reading Claude configuration and polling processes. Nested agents
do not replace an outer terminal agent record. The poll skips zombie processes (`ps` state Z): an agent that exited
but is not yet reaped keeps its name, and must still clear its record.

The status URL includes a host-minted terminal token. Its POST resolves the terminal, reads known
events leniently and publishes a keyed status push. `statusSnapshot` returns an array, one row per
reporting tab. The UI folds all pushes through one store and hydrates workspaces on first use.
Facts-only pushes retain prior status, plan and usage. Closing/clearing a terminal forgets its cache.

Session start is idle; prompt submit/tool completion running; permission request blocked; Stop done;
Stop failure failed. A continuation's Stop still settles status with notifications suppressed.

Each live push that settles a terminal blocked, done or failed asks core for an away notification
(`NotifyAttention`), unless the report says `notify: false`. The title follows the fork ("Claude needs
you", "Claude finished", "Claude hit an error", with the reported project); the body is the report's
summary, response, query or error type, else a pointer to the terminal. Hydrated snapshot rows and
facts-only pushes never ask, an interrupted run settles idle and so stays silent, and a request is
dropped when the terminal's status has moved on before it is shown. Whether it is shown is core's
decision: only while no SharpRail window is focused and the host's notification setting is on.
Interrupts fire no CLI hook, so the host polls the transcript's last conversation turn, ignoring
bookkeeping and rejecting markers older than the current running turn.

Token usage is read from the hook's transcript path, incrementally over complete lines. Streaming
blocks with one message id count once at the last usage; subagent transcripts contribute too.
There is no estimated usage or subscription cost. Older hooks without a transcript path retain the
fork's standard-directory fallback. That directory is `<CLAUDE_CONFIG_DIR or ~/.claude>/projects/<cwd
with every character but an ASCII letter or digit as ->/<session>.jsonl` (`AgentResume.ExpectedTranscriptPath`,
verified against claude 2.1.308, which also resolves the cwd's symlinks): a tab's record id plus its
directory names its transcript before the file exists.

Session ids are SharpRail's. Every launch that starts a new conversation (no `--continue`, `--resume`,
`--teleport`, `--fork-session`, `--session-id` or `--print` among its arguments) gets a fresh UUID as
`--session-id <uuid>`, placed before any positional prompt. Process detection reads the id from the
command line (`--session-id`, or `--resume` with an id; never a fork) the moment it sees the new process,
so the record names the session before any hook reports. Hooks still move it afterwards: a report with a
different session id (/clear, /resume, a fork) replaces the record's id as before.

Revive retains invocation flags, removes old positional prompts and resume/continue/session-id flags,
and runs the result as the revived terminal's program (`RevivePrefill.Program`): nothing is typed into a
shell. A record with an id always revives that id: `--resume <id>` when its transcript exists, otherwise
`--session-id <id>` to start it again under the same id (claude refuses `--session-id` for an id whose
transcript exists, and `--resume` of an unsaved id). Two tabs in one directory therefore each resume
their own session. Only a legacy record without an id keeps the fork's `--continue` fallback. When the
agent exits the tab is an interactive shell in the same directory. The host never runs that revival on
its own: the tab offers it and runs it only when the user chooses Resume and no other process has the
session open (Host Plugins.SPEC, Terminals). The hook's `Fresh` command is the same invocation with
`--session-id` of a new id, for Start fresh. Blueprint's independent revive
hook contributes submit without consuming this plugin's text.

## IDE and UI

The bridge writes a private discovery lock and checks its token before upgrading a connection.
An agent identifies its process before gaining a workspace. From then on it hears only that
workspace's selections and its tool calls act there, whichever workspace the user touched last; a
session SharpRail did not start takes the workspace of the last selection. Selections are kept per
workspace. Selection and document-close events, and a client reading a workspace's terminal
statuses, remember the owning app client; IDE actions are addressed to that client and open in the
window showing the workspace.

The protocol's shapes are read off the CLI itself and Anthropic's VS Code and JetBrains extensions,
not the fork. The CLI listens for two notifications. `selection_changed` carries zero-based
positions and `isEmpty`; the plugin's contract counts from one and the bridge converts. `at_mentioned`
has the CLI type `@path#L<start>-<end>` at its prompt's cursor, nothing reaching the model until the
user submits: the plugin offers it as a file action (W21) on Files rows, the code editor and the
Markdown preview while the workspace runs a Claude terminal, sends it to every session of the
workspace as the JetBrains extension does, and selects the terminal when there is one. No
notification exists for a closed tab, so a close only forgets the selection the tab held.

Tool replies are JSON for the tools that describe state (`tabs` with `uri`, `isActive`, `label` and
`isDirty`; folders with `uri` and a `rootPath`; `Document not open: <path>`) and the words the CLI
compares for the rest: `TAB_CLOSED` or `Tab not found`, `CLOSED_<n>_DIFF_TABS`, `Opened file: <path>`.
`close_tab` takes the name the CLI gave `openDiff` or a file's absolute path, and closes only a tab
`openFile` or `openDiff` newly opened for that workspace's Claude, matched exactly by that name; a tab the user
already had open, or a file sharing only its bare name, stays (`Tab not found`). The record forgets a tab once it
closes by any means, and a whole workspace when it is removed. `openFile` reveals the
line where `startText` first occurs. `openDiff` still opens the target file and answers
`diffShown: false`, which the CLI treats as a failed diff and answers by asking in its own terminal:
Git diff tabs do not represent unsaved proposed content, so `FILE_SAVED` and `DIFF_REJECTED` have
nothing to stand on yet. `closeAllDiffTabs`, which the CLI sends on every submitted prompt, closes
nothing: every open diff or Changes tab is the user's own, since `openDiff` opens none. `getDiagnostics` and `executeCode` are not offered.

UI contributions are the Context/Settings/Capabilities/Account pane, compiled settings section,
workspace launcher, generic `claude` launcher, status tab decorations and one terminal accessory
combining facts, model/effort pickers, plan, tokens, attach and session `/ide` switch.
Tab marks refresh as soon as the host identifies or clears Claude in a terminal, without switching
tabs or reopening a pane. The host agent record remains the source of truth for terminal identity.
The surface selector scrolls horizontally when narrow without a visible scrollbar or extra header height.
Each workspace retains its pane and selected surface. An unchanged configuration/capability result
preserves the body, scope and surface controls, including local input and scroll state.
The generic launcher supplies a fresh Claude SVG icon through its owning asset reader for
consumer plugins, at the requested size and theme tint.
New-launch presets retain the four model aliases opus/fable/sonnet/haiku. The running terminal model
menu instead reads that session's live `/model` picker on demand, including Default and versioned
or custom choices beyond the visible rows. Discovery cancels the CLI picker without choosing or
saving a default; selection matches the exact discovered label and uses the session-only switch.
The discovered list is cached on the host that runs the CLI (`modelListGet`/`modelListPut`, file
`<state dir>/claude-code/model-list.json`, versioned, written through a temporary file; a missing, corrupt
or newer-format file is ignored). The key hashes the binary (path, size, mtime), the configuration
directory, endpoint/model/provider environment overrides and the signed-in account and organisation, so a
CLI upgrade or another account misses; it is computed without spawning anything. A fresh entry fills the
menu with no terminal activity; an entry older than `ModelListCache.FreshFor` (24 h) fills it at once and
is read again once in the background; no entry reads with the "Reading Claude Code's models…" item as
before. A menu "Refresh models" item reads on demand. Concurrent opens share one host lookup, one terminal
at a time walks the picker, an empty or failed read never replaces the last good list, and the menu keeps
showing the cached list when a refresh fails. A Central-governed launch's model override is not in the key
unless it reaches the host's environment.
The empty-prompt guard goes through the kit's `PromptComposer`; only `Message @agent…` is Claude-specific.
Every drive (discovery, model, effort) holds the user's keys out of the terminal from its first
keystroke to its last, released on success, failure, cancellation and exit, so typing cannot interleave with it;
keys typed during the hold are dropped. Unsent drafts and concurrent drives block discovery; unavailable or incomplete lists show an
explanation rather than fabricated choices. Detaching the accessory or losing the Claude agent
cancels discovery; delayed reads, keys and completions cannot act on a later attachment. Launch flags use shell syntax appropriate to the
host, with agent view disabled by default and the host's prompt file first. Caller prompts stack
independently. The settings switches affect app-started sessions, not commands the user types.

Agent terminals request Shift+Enter as raw Escape followed by Return when neither kitty nor
modifyOtherKeys mode 2 is negotiated. Ghostty handles negotiated protocols itself. Clearing the
agent identity restores its normal encoding; the request survives terminal startup and retry.
Host-state updates retain the accessory mount so an open picker keeps its control identity.

The bundled plugin contains hooks, skills and the MCP declaration. Besides status reports, its hooks
carry code-intelligence guidance to every session and subagent (`scripts/code-intel.sh`, POSIX sh and
awk, no jq; silent without `THINKRAIL_MCP_URL`): SessionStart and SubagentStart add a short reminder
naming `lsp_navigate` and `lsp_diagnostics` (subagents never see the prompt appendix), repeating the project's servers and first example from the session's prompt file when it has them (generic otherwise), and PreToolUse on
Grep, Glob and Bash adds an advisory note with the equivalent name-based `lsp_navigate` call when the
search targets a code symbol (a PascalCase identifier in C#/source scope, not docs, config or literals).
The note never carries a permission decision and the script always exits 0; it is rate-limited to the
first and every fifth code-symbol search per session and agent, counted in a file under `$TMPDIR`. Every launcher command receives
`--plugin-dir` using a host-provided directory, even when the prompt appendix is disabled. Revived
sessions use the current build’s directory, replacing an old SharpRail-owned path while preserving
other plugin directories. Paths are quoted for the launcher’s shell. There is no install/update
prompt, marketplace registration, user-settings edit or background cache maintenance for this
integration. Existing user-installed copies are not removed. Reports POST rather than emitting
OSC 777, which unrelated terminals interpret as user notifications.

## Workflow skills

The bundled plugin carries ten skills in its `skills/` directory, which Claude Code discovers
from the process-local plugin: `choosing-a-workflow`, `brainstorming` with its bug-branch and
spec-amendment documents, `asking-user-questions`, `spec-graph`, `writing-specs`,
`setting-up-a-project`, `importing-a-codebase`, `starting-a-new-project`, `shipping-a-pr` with its
phase documents, and `add-highlighting`.
The highlighting skill prepares importable TextMate JSON grammars and filename patterns
for Settings › Highlighting without modifying SharpRail's state files. Five more are the fork's
`packages/pi-thinkrail-workflow/skills` at `3b7c7d589`; the routing, brainstorming and question skills
are JetBrains ThinkRail's at `be06fb019` (its design interview with the bug branch, explicit rejected
alternatives and promotion into durable specs), and `spec-graph` is its `packages/spec-graph` skill.
Upstream ships them to pi only, so delivery to
this CLI is SharpRail's own. Their single source is `src/SharpRail.Plugins.Agent.Skills`, staged into
the plugin at build and shared with the Codex plugin; neither plugin references the other.

The text differs from the fork's only where pi's was untrue here: the app is SharpRail, pi's
`ask_user_question`, `read`, `edit`, `web_search` and `fetch_content` become the session's own means
(a question tool when the CLI has one, else a numbered plain message), the router names no
skill-authoring skill, the task-spec lives at `TASK-<slug>.md` in the repository root (untracked, deleted when the task lands; `spec_create` stays inside the project), and working files go to `$SHARPRAIL_SCRATCH_DIR`
(`SHARPRAIL_SCRATCH_DIR`, the workspace's directory under `<state>/scratch/<project>/`, created by the host, set in every terminal and passed to the session as `--add-dir`), else a
temporary directory outside the repository. The spec, routing and brainstorming skills follow the prompt's rule: their
descriptions offer them only when the user asks for specs or the project keeps a spec graph, and the
dispatcher stops when the `spec_*` tools are absent.

The skills are available to Claude processes launched by SharpRail through the bundled directory;
manual Claude invocations receive them only when independently configured. Loading the directory
uses this build’s assets directly, without a separate installed-cache version refresh.

## Verification and remaining gates

`--claude-code` checks isolated host enable/disable, configuration provenance, review without writes,
stale preview refusal, preservation, token routes, session identity, spending, continuation Stop,
revive, client folding, the shipped skills' shape, direct plugin arguments and revival path replacement, a real authenticated WebSocket IDE initialization/catalogue/zero-based selection/action
round trip addressed to one app client, the tab tools' reply words, a file reference reaching only its workspace's session, and headless configuration/launcher/settings lifecycle,
agent newline mode removal and retained accessory identity. `--notifications` drives the real UI half
with injected status pushes and a recording notifier: blocked, done and failed notify while unfocused;
a focused app, a continuation Stop, an interrupted run and answered attention do not. It also runs the host halves of
the fork's `claude-config.spec.ts` mutation cases through the plan/apply methods the pane's composers
call (an `@`-import at depth one, an MCP server added to `.mcp.json` then denied locally, a skill,
hook and plugin written to the project with a plugin move and marketplace add previewed and run
through a recording CLI, connectors parsed from `claude mcp list`, a local override shadowing a
user value) and both `claude-launcher.spec.ts` cases through the real launcher, menu and settings. The native terminal probe checks
agent fallback, default, kitty and modifyOtherKeys input bytes. Hook scripts have
their own shell checks. The integrated `--plugins` runner includes these checks.

The model-list cache cases cover the file round trip across processes, key change, TTL staleness, corrupt and
newer files, failed reads keeping the last list, and shared lookups and refresh slot.

The model-picker regressions simulate wrapping and clamped lists longer than the visible viewport,
starting in the middle, with Default, distinct releases from the same family and custom choices.
They verify complete discovery, cancellation without a selection/default write, exact-release
selection and draft preservation. They do not establish appearance or timing against a live CLI.

The 2026-10-10 narrow surface-selector check verified the hidden horizontal scrollbar and scrolling
to Account headlessly. It did not check the packaged app or native narrow-pane appearance.

Still required for the full port: complete source-by-source parity audit, the remaining IDE action
and failure cases, the pane-driven halves of the configuration cases (composer dialogs and their narrow-pane
layout, source links after unsaved edits, account/usage and the MCP health row), the review
round trip over MCP, the terminal facts cases, terminal picker and sealing
input behavior, a real macOS notification seen and clicked in the signed bundle (the checks stop
at the notifier seam), compiled static pane/dialog layouts, remote
UI parity, native appearance and the full repository verification gates. Current focused checks
do not establish these remaining requirements.

## Shared agent presentation

File drops on an active, held Claude terminal insert `@path` references without submitting the prompt.
Paths are relative to the agent's directory where possible and quoted when they contain whitespace or
control characters. The workbench resolves host paths and uploads client-local files for remote sessions.

Account and terminal fact, usage, plan, picker, attachment and IDE-toggle components
come from `SharpRail.Plugins.Agent.UI`. The plugin adapts its own contract values and
retains all provider state and commands; generic settings controls remain kit-owned.

Start work includes host image paths in the opening prompt with instructions to read the attached image files. The first agent task title names an untouched generated managed workspace through `set_title`.

Source and rendered diff selections use the same selection notifications as file editors, including continuous Changes review. Activating a different document clears the former current selection; closing a diff or review forgets its selected file. Original-side source selections are not replaced with working-tree contents.

Context labels delimit the active tab name with quotation marks, including the no-session fallback, so it is distinct from the explanatory sentence.

## Language tools

The prompt appendix directs navigation and post-edit inspection through `lsp_navigate` and
`lsp_diagnostics`, with `lsp_status` for initialization and recovery. Calls identify the edited
workspace explicitly. A subagent inherits the parent's MCP connection but not the prompt, and an LSP call
without `workspace` routes to the parent's workspace, so the prompt tells the parent to write the subagent's
workspace path, the `workspace` rule and the navigation/diagnostics/status guidance into its brief. Agents start any language server a task needs without asking, unless language services are disabled. Partial or unavailable inspection is reported.

Capabilities explicitly lists `lsp_navigate`, `lsp_diagnostics` and `lsp_status`, their workspace/subagent
routing, and the LSP management pane. It identifies the bundled Claude plugin MCP declaration as the
connection source and explains installation/enabling rather than claiming an unchecked live connection.
Its configuration guidance follows the [workbench](../SharpRail.UI/SPEC.md#language-intelligence).

Agent instructions request `lsp_diagnostics` with `paths` for every edited file or
`scope: "workspace"` for project source files, including unopened files. Agents must inspect
per-file checked/pending/skipped/error coverage; scans use running servers only.

Agent guidance explains discovering language servers and explicitly starting each needed server through MCP before mixed-language diagnostic scans, while respecting disabled settings.

System instructions prefer LSP over grep for available semantic navigation, advertise hover,
qualified symbol outlines/body reads and paged source excerpts, and prefer edited-file diagnostic
scans. Text search remains available for strings, configuration, unknown owners and incomplete
or unsupported semantic results; delegated agents receive the workspace and LSP guidance.

The system prompt advertises workspace LSP configuration inspection, save and targeted restart,
including library settings and clangd compiler configuration. Saved configuration is visible in
the workspace LSP pane and copied independently from Default for new workspaces.

A separate paragraph of the system prompt documents the project's `.sharprail/language.toml` (portable
server options shared through the project; see the [language services](../SharpRail.Host.Core/Language/SPEC.md#project-language-file)
spec): agents edit it directly, keep comments and unknown keys, and put no host paths or secrets in it.

The instruction text is composed from the host's and the enabled plugins' prompt contributions (Plugin API H20, `PromptTexts`): the plugins' tool items ahead of the text above, and sections after it. The Code Intelligence paragraphs (`lsp_*` tools and `.sharprail/language.toml`) are the host's own text, contributed by the host as a section after this file's text only while language services are enabled. The file is rewritten on activation and for each started terminal, so toggling a plugin, that setting or a project section reaches the next terminal. The Code Intelligence section is composed per workspace: it names only that project's language servers (at most four, ranked by file count from the language pane's bounded census, with their ids for `lsp_status retry`), says that `lsp_status servers` lists the others instead of listing the catalog, and gives examples on a real type, member and file of the primary language (picked from the file names most files share, read without starting a server). The census runs in the background and is cached per workspace (in memory and under the host's language storage); a launch never waits for it, so a workspace's first session gets the generic text and later sessions the project's. Without a workspace (the shared file) the section names no server and uses placeholder examples. Each terminal appends its workspace's composition, less the sections its project turns off, from `<stateDir>/claude-code/workspaces/<hash>.md` (`SHARPRAIL_CLAUDE_PROMPT_FILE`).

Agent LSP guidance prefers qualified outline queries over guessed positions, dependency scope and
bounded source retrieval with the returned URI/source provider. Reuse warm sessions and poll import
readiness; retain text-search fallbacks for incomplete or unsupported semantic results.


The prompt appendix carries the tool guidance enabled plugins contribute (H20; Air Context's context_search/status/index paragraph while that plugin is enabled), then the Code Intelligence section: why grep is wrong for code here (with the macOS renderer example), name-based `lsp_navigate` calls first, when grep is still fine, `lsp_diagnostics` on edited paths, and that subagents do not receive the appendix so a brief must carry it, each rule stated once; the fixed text stays under 3,600 characters. The file is composed on activation and for every terminal the plugin starts, so a plugin turned off leaves nothing in it. No jbcontext global Claude setup/hooks/skills are installed; the Air Context plugin triggers indexing only after workspace enablement.

## Launch contributions

Other plugins may contribute a wrapper, variables or arguments to this agent's launch (Plugin API W22 and H18). The
launcher, its start action and right-click flags, the Create-workspace and Project Home starts, and the host's revive
offer all pass the executable word of the command through `AgentLaunchComposition.Apply` for agent id `claude`, so a
contribution reaches every new or revived terminal. A custom command that is not the `claude` executable is never
rewritten, and a disabled contributor changes nothing.

A host-side contribution may also carry the account the launch runs under (`Account`, Plugin API H18; Air Governance with Central chosen and Claude Code enabled). The Account method then returns it instead of Claude's account and cached usage and does not run `claude auth status`, `--version` or `/usage`; the tab shows the source's identity, credits and refill time, or its actionable problem. Ungoverned launches are unchanged. The terminal fact chips show token totals and the plan, not vendor quota, so they are unchanged.

The appendix is strict: it says an agent MUST use `lsp_navigate` for symbol questions before grep or whole-file reads, MUST run
`lsp_diagnostics` with `paths` on every edited file before declaring done (a build is no substitute), and CAN and MUST start any
missing or stopped server itself through `lsp_status` catalog/retry without asking the user, waiting while it initializes; only
user-disabled settings are respected and reported. A delegating parent MUST put the workspace path, the `workspace` rule, these
rules and a requirement to report its `lsp_navigate`/`lsp_diagnostics` call counts into the brief, and MUST reject a report with
none for code work. Air Context's paragraph (only while enabled) adds MUST `context_search` first. `PromptChecks` asserts these
phrases in both agents and their absence with the feature or plugin off.
