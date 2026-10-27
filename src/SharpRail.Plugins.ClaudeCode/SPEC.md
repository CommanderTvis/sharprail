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
it does not provide Pi execution. A launch is presented either as that terminal program or, in chat mode (below), as a conversation over the CLI's own headless protocol; the plugin has no AI chat of its own. It is disabled by default.

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
the terminal shows an advisor chip and a Chrome chip read from that command. The model chip leads with the Claude mark (`ClaudeGlyph`, 12 px, muted, the tab's `claude.svg`) and the advisor chip with the kit's `discuss` icon (two speech bubbles, 12 px, muted), both through the optional `leading` control of `TerminalFacts.FactChip`/`ActionFactChip`; the Chrome chip reads lowercase `chrome` and leads with the kit's `chrome` icon (12 px, muted); the other chips are unchanged.
Unset launch defaults persist as empty strings so a merged settings update can clear an earlier override.
Wire version 2 covers these added settings: the strict settings schema would make older UI halves
fall back to defaults, so mismatched host/UI halves stay dormant.


## Chat mode (host)

The `Mode` setting (Terminal by default, or Chat) says how a SharpRail launch is presented; the host serves chat whatever
the setting says, and the view chooses by it. The wire is `ChatContract` (declared apart from `ClaudeCodeContract` and merged
into it): items, a header, three push kinds on the keyed `chatUpdates` state channel (workspace id and chat id), and the
methods open, snapshot, send, interrupt, close, set model, set permission mode, set effort, answer, rewind, fork, stop task,
item detail, sessions and files. Chat mode drives Claude Code over its stream-json protocol, as the official VS Code
extension does. The host half lives in `Host/Chat`.

Process. A chat's child is `claude -p --output-format stream-json --verbose --input-format stream-json
--include-partial-messages --replay-user-messages --permission-prompt-tool=stdio`, plus the settings' model, effort,
permission mode, advisor, Chrome and appended prompt file, the bundled plugin directory, and the session flags below. It
gets the environment a terminal launch gets (the prompt file, scratch and worktree directories, `CLAUDE_CODE_SSE_PORT` for
the IDE bridge, `SHARPRAIL_UI_LAUNCH=claude`, `THINKRAIL_MCP_URL` bound to the chat) and every other plugin's
launch contribution (wrapper, arguments, environment) composed as `AgentLaunchComposition` does for a command line; the
hook plugin's status URL is blanked because the chat reports its own status. The IDE bridge is unchanged and still serves terminals, but a headless chat child does not reach it. Probed against the
real CLI (2.1.310): a `-p` child ignores `CLAUDE_CODE_SSE_PORT` plus the lock file, with or without `--ide` or
`CLAUDE_CODE_AUTO_CONNECT_IDE`; `--mcp-config` with a `ws-ide` entry (the type in the CLI's config schema) is skipped with
"unknown MCP server type"; and the `mcp_set_servers` control request answers "IDE transports are registered by the CLI
itself". So in chat mode open-diff, diagnostics and selection do not travel the bridge; a chat's edit and write cards carry
`FilePath` for the view to open the diff itself, and the permission request is the approval point. The bridge's
workspace placement is not extended to chat children. Stdout lines are read and stdin written off the UI thread (one serialized
writer); stderr becomes warning notices. Off Windows the child starts behind a setsid launcher
(`sharprail-claude-process-group`, built from the Codex plugin's `process-group.c`), and closing stdin, then SIGTERM and
SIGKILL to its group, ends it and anything it started; Windows kills the process tree.

Folding. One folder turns messages into the ordered, id-keyed item list, live and from a transcript alike. Streamed partials
and the complete message of a block meet in one item (the nth block of a type in an API message), tool calls are keyed by
their `tool_use` id, and `parent_tool_use_id` becomes `ParentId`. A tool item carries a one-line summary, the file path of an
edit or write, its state, and output cut at `ChatContract.OutputLimit` with the whole text served by item detail. The
`task_started`, `task_progress`, `task_notification` and `task_updated` events become header tasks and the Task call's
`TaskId`. A `can_use_tool` control request becomes a pending permission item; answering sends the control response with the
edited or original input and, when remembering, the CLI's own rule suggestions as `updatedPermissions`. A result adds no row: it fills cost, tokens and context in the header (a failed one adds one failure notice); init and the `initialize` answer fill commands, models, agents and
MCP servers. Prompts are sent with a uuid and the CLI echoes them back (`--replay-user-messages`), so a prompt sent during a turn is queued
until its echo and rewind and fork have a uuid for every prompt. Changed items are pushed together at most every 16 ms, so a
stream costs one push per item per interval.

Queue. A prompt sent during a turn is a queued user item until the CLI echoes it. `chatDequeue` cancels it with the
`cancel_async_message` control request (keyed by the prompt's uuid) and drops the item with a remove push; the CLI answers
`cancelled:false` when it already took the prompt up, and the call then fails and the item stays. `chatEditQueued` is that
cancel followed by sending the new text as one host step, so the old prompt never runs.

Questions. An `AskUserQuestion` `can_use_tool` request becomes a question item (questions with options, multi-select, a
header; "Other" is free text the view puts in the answer), not a permission. `chatAnswerQuestion` replies with
`updatedInput` equal to the CLI's own `questions` plus `answers`, keyed by question text (a multi-select answer is its
labels joined by ", "); cancelling replies with a deny. The shape was read from the official extension, not probed live.

Notices. A notice may carry a category: `compaction` (from `compact_boundary`, with the trigger and token count), `failure`
(a result that is an error or has a non-success subtype, with the subtype and whether resending the last prompt is a
sensible recovery: API errors and `error_during_execution`, not turn or budget limits), `retry` (`api_retry`) and
`restart`. The status feed marks a failed result Failed.

Models and header. Models carry a provider and family label (derived from the id) and the effort levels the CLI reports
(`supportedEffortLevels`). While a change is under way (the restart for a new effort) the header's `Pending` names it and is
cleared by the restarted child's init, so the chip never looks idle during the restart.

Control. The model and permission mode change in the header only after the CLI's success response (`set_model`,
`set_permission_mode`). The effort level changes live too: `apply_flag_settings` with `effortLevel`, confirmed by `applied.effort` from `get_settings`
(probed against CLI 2.1.310; the extension does the same). Only a CLI that rejects the request gets the older restart on the same
session with `--effort` and a notice. A chip never shows an arrow: a change under way is the current value and an ellipsis. Interrupt, stop task and rewind-files are control requests; a conversation rewind
replays the transcript up to the chosen prompt and resumes the CLI at the message before it with `--fork-session`.
A fork is a new chat record that branches the same way when first opened.

Revival. Each chat's session id, directory, model, effort, permission mode, title and pending fork are kept in the plugin's
user state (`chats.json` under the plugin state directory, never in a project). Opening a known chat after an app restart
resumes with `--resume <id>` when the transcript exists (`AgentResume.SessionExists`) and otherwise starts afresh with
`--session-id <id>`, both with the recorded choices. Before the child is ready the host replays the transcript
(`~/.claude/projects/<slug>/<id>.jsonl` and the `<id>/subagents/agent-*.jsonl` files, whose meta file names the Task call they
hang from) into items, sending a reset push first. A revived chat is SharpRail's own launch.

Status. A chat feeds the same status store and `Status` channel as a terminal, keyed by its chat id as the tab key, with the
hook events' mapping (`ClaudeValues.StatusForAgentEvent`): `prompt_submit` when a turn starts, `permission_request` while
waiting for an answer, `stop` or `stop_failure` on a result, `interrupted` on interrupt and close, so tab badges and attention
notifications need no terminal. `--claude-chat-host` checks all of this against a scripted stand-in for the CLI.

## Chat semantics (from the VS Code extension)

The host folds stream-json the way the official extension's webview does, live and from a transcript through one path
(`ChatFolder`), so a replay gives the same typed items as the live stream (checked by `--claude-chat-host`). Each rule is
named with the frame that triggers it.

User frames are classified by `origin` and text shape, not by role. `origin` is `{kind, name?, senderTaskId?, subkind?}`;
`isSynthetic` is the frame's flag, or `is_meta` with an origin. A synthetic frame is dropped unless it is a peer message. A
frame with `parent_tool_use_id` (subagent traffic) keeps its data in the items' `ParentId` but its prompt text is no row.
`tool_result`-only frames are no row; the result attaches to its tool item. Text that is only `<system-reminder>` is never a
row, nor is a hidden `ToolSearch` call.
- Human prompt (no origin or `kind:human`): a user item; ANSI, control and bidi characters are removed, `pasted_content`
  tokens and a trailing `<forked-skill-launch>` blob are stripped; `image` and `document` blocks and the IDE's
  `<ide_selection>` and `<ide_opened_file>` blocks become attachment chips. Another origin (channel, plugin, sdk) keeps the
  prompt with `Origin` set.
- Interrupt markers `[Request interrupted by user]` and `... for tool use]`: notices "Interrupted" and "Tool interrupted".
- `<task-notification>` (origin task-notification, no subkind, non-empty summary): a notice showing the summary, with task
  id, tool-use id, status, output file, result and usage as sections.
- Peer, agent, cross-session and channel messages: text with a lead-in line ("Another Claude session sent a message[ while you
  were working]:", "A peer session sent ...", "A message arrived from <source>:"), one `<agent-message from>`,
  `<cross-session-message>` or `<channel source>` tag around the body and one of the fixed trailer paragraphs. With a
  matching origin (peer for agent and cross-session, channel for channel) or an `agentSender` it is a collapsed row
  ("Message from @name", "Message from channel @name", "Message from <agentSender>") with a 512-character one-line preview
  and the whole body; with no origin it is "Sender not recorded"; with another origin it is an ordinary prompt.
- Wrapped notices when the origin is absent (`local-command-caveat` "Command caveat", `teammate-message`, `tick`,
  `mcp-resource-update`, `mcp-polling-update`), `<command-name>` records ("/cmd args" with Command, Command message and
  Command contents), `<local-command-stdout>` and `-stderr` (one line, or a block when multi-line or an error; "Set model to X"
  reads "Switched to X"), `<bash-stdout>` with `<bash-stderr>` and `<bash-exit-code>` (Output, Error output, Exit code) and
  `<post-tool-use-hook>` diagnostics.
Result frames draw nothing: they end the turn and update the header (cost, `modelUsage` context window and max output of the
current model, `fast_mode_state`). A model-less result (`num_turns` 0 with a task-notification origin) is no turn and no
failure. An error result or a non-success subtype is exactly one failure notice (retryable for API errors and
`error_during_execution`). `system` frames: `status` drives the header's `Activity` ("Compacting", "Running start-up hooks")
and permission mode; `thinking_tokens` the streaming thinking item's token count; `compact_boundary` plus the following
synthetic string user frame one compaction notice ("Compacted chat · auto|manual · Nk tokens freed", the summary as its
detail); `informational` lines of level other than info (any with tag `brass_thimble`) become meta notices;
`model_refusal_fallback` a notice that also removes the retracted messages; `task_started`, `task_progress`, `task_updated`,
`task_notification` and `background_tasks_changed` the header tasks (type labels subagent, workflow, shell, monitor, MCP task,
teammate, dream, auto-mode scan, memory import, cloud session; background flag, usage). `rate_limit_event` sets the header
banner: rejected "You've hit your <window> · resets in 2h" (windows: session, weekly, weekly Opus, weekly Sonnet, Fable,
usage credit limit), warning "You've used N% of your <window> · ..." from 70%, cleared when allowed. Unreadable transcript
lines are one "N earlier lines of this conversation could not be shown." notice.
Held prompts: a prompt sent while busy is a queued user item. CLI 2.1.310 emits no `system/command_lifecycle` (probed with
`--replay-user-messages`), so the echo of the prompt with the same uuid releases it; a CLI that sends `command_lifecycle`
releases it on started, completed, cancelled, discarded or refused, and `ran_ahead` adds "Ran at once, while Claude was working."
Permissions: a `can_use_tool` request carries the card's question per tool ("Allow this bash command?", "Make this edit to
a.cs?", ...), the "don't ask again" label rewritten from the suggestions (one rule, two rules, "and N more", directories, "Yes,
allow all edits this session", "Yes, return to normal mode") and the five destinations with their labels and tooltips. The reply
is allow (with the edited input; with the remembered suggestions rewritten to the chosen destination; or, for ExitPlanMode, a
`setMode` of the chosen mode) or deny with the extension's words: the plain rejection, the same with "The user provided the
following reason for the rejection: <reason>", "User chose to stay in plan mode and continue planning", or plan feedback
"Comments on the plan:" followed by `[Re: "<selected text>"] <comment>` lines. An AskUserQuestion item ends as answered,
declined (with the reason), no-answer or session-ended according to its tool result, and "Answering your earlier question ..."
text settles an unanswered one. Rewind: `chatRewindPreview` is the dry run (`rewind_files` with `dry_run`), the rewind fails with
"Failed to rewind code: ...", and skipped links add a meta notice; fork returns the prompt text and fails with "Failed to fork
conversation: Unknown session id" for an unknown chat. Header: `PermissionModes` (Manual, Edit automatically, Plan, Auto, Bypass
permissions, Don't ask, with the extension's descriptions), commands with `Section` "Slash Commands", and context numbers from
the last main-thread assistant usage (input, cache, output) and the model's window and max output.

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
narrower than the Agent UI `TokenAmount` threshold; a short line above the table says "Measured 5m ago" (the same `ClaudeParts.Ago` phrasing as the account usage reading, the absolute time in the tooltip), that it is measuring, or why it
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
combining facts, model/effort chips, plan, tokens, attach and session `/ide` switch, and a terminal tab menu
item, Copy session ID (Plugin API W26), offered while the tab's agent record is Claude's and has a session id and copying
that bare id, the one `claude --resume` takes; it is hidden, not disabled, otherwise.
Tab marks refresh as soon as the host identifies or clears Claude in a terminal, without switching
tabs or reopening a pane. The host agent record remains the source of truth for terminal identity.
The surface selector scrolls horizontally when narrow without a visible scrollbar or extra header height.
Each workspace retains its pane and selected surface. An unchanged configuration/capability result
preserves the body, scope and surface controls, including local input and scroll state.
The generic launcher supplies a fresh Claude SVG icon through its owning asset reader for
consumer plugins, at the requested size and theme tint.
New-launch presets retain the four model aliases opus/fable/sonnet/haiku.

The terminal's model and effort chips show what Claude Code's hooks last reported (`model` and `effort.level` in the
hook input, `model_switch` for a switch), with a tooltip saying so: it is not a live read. Per-session terminal-driving
model and effort pickers are retired. They typed `/model` and `/effort`, walked Claude's TUI by screen-scraping and
believed the result; no authoritative live source for a session's model or effort exists, the scraping was unstable
and the popup showed rendering glitches. Gone with them: the menus, the model-list discovery and its host cache
(`modelListGet`/`modelListPut`), the optimistic `effort_switch` update, the draft guard and `PickerChip`. A click on
either chip only opens Claude's own picker: `ClaudeTerminalAccessory.OpenPicker` types `/model` or `/effort` plus Enter
once and focuses the terminal, and the user chooses with the arrow keys inside Claude. SharpRail never types a
selection and keeps no state for it. The chips are disabled with a tooltip while the session is Running, and their
automation names are `Open model picker` and `Open effort picker`. Launch-time model and effort (launcher, Settings
defaults, `--model`/`--effort`) are untouched. The `/ide` switch still types its command under a `HoldInput()` hold.
Launch flags use shell syntax appropriate to the
host, with agent view disabled by default and the host's prompt file first. Caller prompts stack
independently. The settings switches affect app-started sessions, not commands the user types.

Agent terminals request Shift+Enter as raw Escape followed by Return when neither kitty nor
modifyOtherKeys mode 2 is negotiated. Ghostty handles negotiated protocols itself. Clearing the
agent identity restores its normal encoding; the request survives terminal startup and retry.
Host-state updates retain the accessory mount.

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

## Chat mode (UI)

### Upstream chat mapping

The chat view follows ThinkRail's web chat (`apps/web/src/chat`) in layout and vocabulary, over Claude Code's contract and a drawn, virtualized list. Left out on purpose: Pi-specific parts (provider switching, remote model catalogs, extension UI dialogs, templates and slots, review packages) and anything that needs data `ChatContract` lacks.

| Upstream | Here | Notes |
| --- | --- | --- |
| `Composer.tsx` footer | `ChatView.Composer.cs`: a bordered shell, the input over a footer row | pickers (model, effort, permission mode), attach and Skills at the left, session stats between, working time, history, Stop, Queue/Send and its menu at the right; cwd, MCP and IDE chips under the shell. Narrow, Skills and stats give way and Send is an arrow |
| IME care, `recentPrompts`, send options | a key the input method handles (`ImeProcessed`) never sends; Enter sends (or Ctrl/Cmd+Enter, a stored option); Up and Down walk this chat's prompts then earlier chats' (30 kept, client-local) | the send menu offers Queue and Stop and send; there is no mid-turn steering, so no steer option |
| `promptImages`, `imageAttachment` | image attachments show a thumbnail chip decoded off the UI thread; paste and drop add files and images | |
| `ModelEffortPicker`, `ModelSelector`, `ThinkingSelector`, `pickerGroups`, `modelVisibility`, `modelPicker`, `useModelPreferences` | `ChatView.Pickers.cs` and `ModelPicks`: a searchable list (upstream's fuzzy score) with Favorites, Recent and families as groups, a star and a hide control per model, hidden models behind a toggle, arrow keys and Enter; effort levels with the current one checked | the group is the host's provider when it names one, else the family; the thinking selector offers the levels the current model reports; a change under way (`ChatHeader.Pending`) shows on its chip; favourites, recents and hidden models are stored through the plugin's client-local preference, never in a project |
| `SkillsButton`, `SkillsDialog` | Skills button listing `ChatHeader.Commands` whose source is a skill | picking inserts the command |
| `QueueStrip` | strip above the composer listing the user items marked queued, each with Edit (inline, then Send) and Remove | `chatDequeue` and `chatEditQueued`; a message the CLI already took up stays and the strip says so; a remove push drops an item from the list, its group and its thread |
| `SessionStatsBar`, `StreamIndicator`, context pie (`sv0`) | stats text, the context pie and "Working" with elapsed seconds (or the CLI's activity word) in the footer | from `ChatHeader.Usage` and `Activity`; a UI timer, no I/O |
| `ChatHeader` | the title and a past-conversations flyout (a `Search sessions…` box over the host's sessions, newest first; `Loading sessions…`, `No sessions yet`, `No sessions found`); there is no New chat button, a new conversation comes from the `/` menu or Start work | the title is not editable: the contract has no rename; no Web tab, groups or archive |
| `HistoryOverlay`, `useHistorySearch` | a full overlay: prompts first, Tab zooms to messages and a preview, arrows and Enter jump to the row, Esc closes | scoped to this chat; the contract has no wider search |
| `rows.ts`, `turns.tsx`, `ActivityGroup`, `activityBreadcrumbs`, `ThinkingGroup` | `ChatTranscript` folds consecutive routine tools and thinking into one activity row with a breadcrumb; opened, tools nest under the thinking that led to them | a plain user message over 500 characters folds to a preview with Show more |
| `ToolCard`, `tools/*` | drawn by `ChatRowLayout.Tool` from the words in `ToolCards`; see the tool call card row below | |
| `planKit`, `planView` | plan card for `ExitPlanMode` in the request panel above the composer (and in the list as a tool card): the plan as selectable text, `Yes, and auto-accept`, `Yes, and manually approve edits`, `No, keep planning` / `Send feedback and keep planning`, and `Comment on selection`, which sends `ChatPlanComment(quote, comment)` | the plan is plain text in the panel, Markdown in the list card |
| `SubagentTranscriptDialog`, `tools/subagent` | Task cards nest their thread and open it in a dialog (a second virtualized list) that stays live | |
| `BackgroundCommandCompletion` | a finished shell, monitor or workflow task leaves a notice in the conversation | read from `ChatHeader.Tasks` transitions |
| Skill invocation card | the Skill tool's card keeps the skill's name in its header and the request beneath | |
| `Markdown`, `markdownBlocks`, `assistantLinks`, `FileChip`, `CopyButton` | drawn Markdown by block; web links only for http, https and mailto; `path[:line[:col]]` and `path#Lline` in links and code spans are file chips that open the file at the line through the editor API, refusing parent segments and paths outside the chat's directory; hover Copy on replies | |
| `virtualRows`, `rowHeightEstimates`, `readingBand`, `scrollGeometry`, `useChatScroll` | `ChatList`: one drawn surface inside a scroll viewer, as the kit's trees draw; only the rows near the viewport are laid out and drawn, none owns a control | row heights are measured (kept by item, revision and width) or estimated by `ChatRowEstimates`, so the scrollbar is sized from guesses refined as rows come into view; the reading band (a line a tenth of the viewport, 48 to 80 pixels, below the top) names the row and offset into it that is put back when heights above change, rows are inserted or removed, the width changes or the tab is shown again; stick-to-bottom with jump to latest; item stubs expose a list of items to assistive technology |
| text selection (the webview's native selection) | `ChatList` keeps a selection layer over the drawn rows: anchor and focus are a row id and a character offset into the row's selectable text (its text runs joined by a line break), so they survive virtualization, streaming growth and height changes; highlight is painted from `TextLayout` hit-test ranges with `Ui.TextSelection`; drag within and across rows with edge auto-scroll, double click a word, triple click a paragraph or line, Shift+click extends, Cmd/Ctrl+A selects the transcript, Cmd/Ctrl+C and a context menu Copy put the plain text on the clipboard; a link activates on release only when the pointer did not drag; rows far from the viewport are read in short slices for copy and are never kept | the plan card's `SelectableTextBlock` with Comment on selection sends `ChatPlanComment(quote, comment)` |
| tool call card (3.2 to 3.5), thinking blocks (3.3) | `ToolCards` holds the words and `ChatRowLayout.Tool` draws them: a status dot (accent while pending, red for a failed call, warning for a denied one, green otherwise; drawn still, not blinking), a bold name and a monospace secondary text per tool as in the extension's table (Bash description else command; `Read basename (lines a-b)` or `(from line n)`; Edit and Write basename; `Glob pattern: "p"`; `Grep "p" (in path, glob: g, type: t)`; `<skill> skill`; `Update Todos`; `Web Fetch url`; `Web Search query`; `Edit Notebook Cell file:cell`; `Artifact path`; `Sent files`; `Agent: description`; MCP `<Server humanised> [<tool>]` with the first present of query, message, channel (as `#channel`), repo, url (hostname), path, title, search, text, cut with an ellipsis at 40 characters, 30 for text; `Claude in Chrome [Tool]`), a secondary line under it (Edit: added, removed or `Modified` from the line counts, `Edit failed`; Write: its line count while running, `Write succeeded`, `Write failed`; Glob, Grep and Search: `No matches found`, `1 line of output`, `N lines of output`; NotebookEdit status; `Fetched from url`; WebSearch `Allowed: .. \u00B7 Blocked: ..`), and, when the card is open, the IN and OUT rows: a label, then the text in a bordered box clipped to 60 pixels with a fade, which opens whole in a dialog titled `<Tool> tool input` or `output` (an output the wire cut is fetched first) when it is over 250 characters or three lines. The Agent card has IN (the prompt) only and `Open thread`; Read, Skill, Edit, Write, Grep, Glob, Search, WebFetch, WebSearch, Artifact and SendUserFile have no IN or OUT of their own (a failed call still shows its OUT); `ToolSearch` is never drawn; every other tool is the generic card with the input as indented JSON. A thinking block is collapsed, `Thinking...` with ` \u00B7 N tokens` while it streams, `Thought for Ns` (rounded seconds from `ChatThinkingItem.DurationMs`) after and `Thinking` when no duration is known (a transcript replay); one without text is a static label | the extension's `Edit` line for an equal count of added and removed lines is read as `Modified`; the header of a card is a single line, not clamped to two |
| composer and chrome (4.1 to 4.9) | Shift+Tab (anywhere in the view) cycles `ChatHeader.PermissionModes` (the machine's own list when the host sent none), and the mode button's tooltip is `<description>. Click to change, or press Shift+Tab to cycle.`; the `/` menu groups entries under Context, Model, Customize, Settings, Slash Commands, Support and MCP servers in that order (a CLI command sits in its `ChatCommand.Section`, an unknown section follows Slash Commands, `No matching commands` when none match), with a `/` button titled `Show command menu (/)`; finished `@path`, `@"path with spaces"` and `#L10-20` mentions show as removable chips above the input; the context pie shows only with under 50% of the window left, its tooltip `N% of context remaining until auto-compact.` and a click sends `/compact`; the working word is `ChatHeader.Activity` with `...` (`Compacting...`); `ChatHeader.Banner` is a dismissible banner remembered by its key; Rewind opens a dialog titled `Rewind` that shows `Checking code changes…` from `chatRewindPreview`, then `N lines will be removed and M lines will be added across K files:` with the files and `Rewinding does not affect files edited manually or via bash.`, or `The code has not changed, so no code will be restored.`, with `Restore conversation` / `Restore code and conversation` and `Never mind`; the `Waiting for permission…` and `Waiting for your answer…` placeholders are the extension's; Esc interrupts a running turn from anywhere unless a request is waiting, a menu is open or the history overlay is | built-in menu actions are only Attach file…, Resume conversation, New conversation, Switch model… and View help docs (Customize and Settings are empty unless the host names them; omitted because SharpRail has no honest equivalent: Clear conversation and Export conversation, Rewind from the menu, Thinking, Toggle fast mode, Account & usage, MCP servers, Hooks, Permissions, Status, Sandbox, Memory, Instructions, Output styles, Manage plugins, Claude in Chrome, Claude Design, Bookmarks, General config, Focus view, Open Claude in Terminal, Sign out, Switch account, Mention file from this project); the pie's window is the context window less the output the model may reserve (at most 20,000 tokens) and a 13,000-token buffer, the extension's own constants (`Pf5` and `_v0` in its bundle); the stats bar with its five-cell context meter stays beside the pie; mentions are chips beside a plain-text input, not inside it; past conversations have no Web tab, groups or archive; wording that is not the extension's is listed under Neutral phrases below |
| `AskUserQuestionCard`, `askState` | question panel above the composer for `ChatQuestionItem`: tabs for several questions, single and multi select, `Type your answer…`, `Submit answers` and `No` through `chatAnswerQuestion`, Esc declines; a single-select question where an option has `Preview` shows a side pane after a 150 ms hover (the Markdown, or `No preview for this option`); answered, the row in the list folds to the answers | the draft lives in the transcript, so it survives the row's replacement; a multi-select question never shows the pane |
| permission request | one panel above the composer, first in first out, the list dimmed behind it: the extension's question per tool, `Yes` / `Yes, allow ...` / `No` answered with 1, 2, 3 or Enter, Left and Right choosing where the rule is saved, a reason box, Esc declines, an editable Bash command | the request leaves no row of its own; the tool's card stays in the list |
| failure, compaction, retry and restart notices | by `ChatNoticeItem.Category`: a failure card with Try again (the last prompt sent again) when retryable, a rule for compaction, quiet lines for retry and restart | |
| `RequestReviewCard` | a review tool's verdict (tool name ending `request_review`, a JSON output with `verdict`, `itemTitle` and `findings`) reads as a summary line and the findings | the other SharpRail review cards (`ResolveCommentCard`, `SpecToolCard`) are ordinary tool cards: their results are not shaped for it in a chat's tool output |

### Attachments and paste

Paste belongs to the composer, not to its text box: Cmd/Ctrl+V, Shift+Insert and the box's own Paste take one road, which tries files, then an image (Avalonia's bitmap format, which reads PNG and, on macOS, TIFF), then text, and only text goes into the box. A box alone would ignore a clipboard that holds an image and no text. An image or PDF (pasted, dropped or picked) is read on the UI machine, capped at 5 MB with a message, shown as a chip with a thumbnail and a remove control, and sent in `ChatAttachment.Data` with its `MediaType`: the host turns it into a base64 `image` (or `document`) content block in the stream-json user message, refuses more than 8 MB, and keeps only the name in the transcript, so a host on another machine needs no path. Other files are `@path` mentions as before. Checked on the headless in-memory clipboard (key, Shift+Insert, files, text only, the size cap) and, host-side, by a fake CLI that asserts it received the image block; not checked against the real macOS pasteboard.

### Composer layout

The chat has no header of its own: no title row and no start banner, the list starts at the first row, and the tab carries the name; past conversations open from the `+` popup. The composer is one calm bubble over a plain footer. The shell is the only element with a border and a radius: a raised surface (`ChatPopup.Raised`, a step lighter than the page in every theme), radius 18, a soft `Ui.ControlBorder` border that turns `Ui.ControlBorderActive` while the input has focus, equal padding. The input sits on it with no border, fill or radius in any state (normal, hover, focus, disabled). Inside the box, above the input, are the content chips: the editor-selection chip, attachment chips and `@` mentions; at the right of the input are the working time, history search, the Queue chevron while a turn runs, and the round Stop and Send (an arrow-up glyph, primary when there is text; Stop replaces nothing but sits beside it while the turn runs; Queue is the chevron's menu). The round buttons are as tall as one line of input, so the shell's top, right and bottom insets around them are equal. Below the box, outside it, is one footer row of plain controls with only a `Ui.Hover` wash: at the left the `+` button (a warning dot when an MCP server is down) and the context pie; between them a quiet usage line with a tooltip naming each figure; at the right the model (normal tone) with the effort beside it (muted, shortened away when room runs short) and the mode (muted) as plain text with a small down arrow always shown; the model button's popup lists a flagship per family (the first the CLI lists), then Effort and More models, which open their lists in the same popup; then the chips the terminal shows: the advisor and Chrome the child was launched with (`ChatHeader.Advisor`, `Chrome`), the folder, the IDE context switch, attach and the environment chips (mise, direnv, JVM) other plugins offer through `TerminalAccessoryRegistration.ForWorkspace`. The `+` opens the shared popup with Past conversations, Skills, the command menu, the IDE context switch (checked when on) and the MCP servers. The usage popover names each figure instead of repeating the footer line. As the measured width shrinks the footer gives way in this order: the usage line goes, the values shorten (`default effort` is `default`, `Accept edits` is `edits`), the working time goes, history goes; the chips never go: when they do not fit on one line they wrap onto a second. A wider view relaxes the footer to a lower level only when that level fits with 28 pixels to spare and the view is 28 pixels wider than the width at which that level last failed to fit (a freshly rebuilt footer measures smaller than a settled one, so the measurement alone cannot be trusted), so dragging an edge back and forth over a boundary does not flip it. The editor-selection chip, shown while IDE context is on, reads `name, N lines` with `#Lstart-end` for a selection, `name: “quote…”` (full text in the tooltip) for text selected in a rendered preview that maps to no source lines (the editor event then carries `Mapped: false`; the terminal and Codex ignore such events), or the open file's name alone, lighter; it follows the selection live, its `×` leaves it out of the next prompt, and sending clears it; the prompt carries `@path#Lstart-end`, `@path`, or `@path` plus the quote as a block quote. The usage chip opens a popover with the tokens, cost, context meter and Compact now. When a paste finds nothing the box can use, a line under the input says `Nothing to paste. The clipboard has: <format names>` (or `... is empty.`); that wording is neutral, mine.

### Command, mention and Skills menus

One compact list for all three: as wide as the composer and never over 640 px, no taller than two fifths of the chat, one fixed-height row per entry (the name semibold in a 168 px column, the description after it, muted and ellipsized to one line; the whole text, argument hint included, is the tooltip and the accessible name), section titles as small muted caps labels with room above them, and a virtualizing list, so with hundreds of skills only the rows in view exist. Filtering is as you type and the keys are unchanged.

### Chat zoom

Each chat zooms by itself, like a terminal's font and unlike the page: Ctrl or Cmd with the wheel, a trackpad pinch (magnify), a touch pinch on Android, and Mod+=, Mod+- and Mod+0 while the chat has focus (the window's page-zoom chords stand aside for a focused chat through `ZoomChords`; anywhere else they zoom the page as before). The range is 60% to 250%, remembered per chat id in the `chat-zoom` preference, and the level shows briefly in a floating plate that moves nothing. The whole chat sits in a layout transform, so the list, panels, composer and dialogs-in-place reflow at the scaled width: row layouts are cached by item, revision and width, and the scale is carried by the width, so no cache needs a second key; row estimates and the reading band, selection and hit rectangles work in the chat's own units. The popups (menus, flyouts) are outside the transform and keep the page's size.

### Resizing and page zoom

A change of the list's width (the page zoom steps the width) drops nothing: layouts are keyed by item, revision and width, so those at the old width stop matching and are trimmed after the next draw. The rows in and near the viewport are laid out once, in the measure that sees the new width, so the first frame at that width already has true heights and the settling pass has nothing to correct; the reading band puts the row being read back in the same pass. The footer is fitted once per frame, not once per size step. Checked by counting layouts built and settling passes across zoom steps over 20,000 rows, and that the surface object and the row being read survive each step; whether it looks calmer on screen has not been judged.

### Picking an answer

A pick in the question panel repaints in place: the option marks (fixed-size, same border and inset chosen or not, selection by colour only), the tab ticks and Submit's enabled state change, and the panel, the transcript rows and the list's layouts are untouched. The draft lives in the transcript but a pending question draws nothing in the list, so storing it replaces no row; a row that draws nothing is estimated at zero height, so replacing one never shifts the rows around it. Only switching tab, folding the panel or a new request rebuilds the panel.

### Past conversations

The flyout lists the host's sessions newest first under Today, Yesterday, Previous 7 days and Older (local calendar days), one fixed-height line each: the title ellipsized, small markers (`this chat`, `open in another tab`, and `chat` when a SharpRail chat started the session), and a quiet right-aligned time (`5m ago`, `3h ago`, `Yesterday`, `3d ago`, then `Sep 25` or `Sep 25, 2025`) with the full local date and time in the tooltip. It is bounded like the command menu and virtualized. One session has one child: `ChatSessionInfo.ChatId` names the chat of the workspace that runs a session, choosing such a session focuses that tab, choosing this chat's own does nothing, and `chatOpen` refuses a resume of a session another chat of the workspace runs (`That conversation is already open in another tab (chat <id>).`). `ChatSessionInfo.StartedIn` is `chat` when the host has a chat record for the session; it cannot say where any other session began, so a terminal session carries no label.

### Hover and focus in the drawn list

Every click target in a drawn row (Rewind, Fork, links and file chips, Show more and Show less, tool and thinking headers, Open thread, Open diff, Load full output, the open-in-full boxes, Retry) is found by its hit rectangle, with no control per row: under the pointer it gets a wash from `Ui.Hover` and an underline in the accent, and the surface a hand cursor; leaving clears both, and only the previous and the current target are repainted (as the hovered row is). From the keyboard, Tab and Shift+Tab walk the selected row's targets in reading order (whole-line toggles and the hover-only Rewind and Fork, unless the row is hovered, are skipped), the focused one wears an accent ring, and Enter or Space activates it; past the last one Tab leaves the list.

### Chat UI semantics

What is drawn per item kind (the extension's semantics, ThinkRail's look):

- A human prompt is one bubble, clipped to about three lines with Show more and Show less; Copy, Fork and Rewind sit in a strip below it, shown on hover. A prompt that did not come from a human (a peer, channel or task notification) is a folded row; a queued one says `queued`; a prompt that ran ahead says `Ran at once, while Claude was working.`
- An assistant reply is its Markdown by block, only the blocks near the viewport realized; Copy sits below it. Subagent traffic is not drawn in the main list; the Agent card opens it in a dialog.
- A thinking block is a collapsed line (`Thinking...`, `Thought for Ns`, `Thinking`); consecutive routine tools and thinking fold into one activity row with a breadcrumb.
- A tool call is the card described in the tool call row above; `ToolSearch` is never drawn and a result is never a row.
- Notices are drawn by category: interrupts and meta lines italic, a failure as a card with Try again, compaction as a rule, retries and restarts as quiet lines.

Selection and popups:

- A selection highlights glyphs only: each line's highlight ends at its last glyph plus a 4 px newline cue, never at the box edge, and is clipped to the clip its text draws in, so text clipped away (the IN and OUT boxes clip at 60 px) is neither highlighted, selectable nor copied. No highlight is painted in gaps between blocks or rows, and an empty row draws none.
- The past-conversations flyout lists two-line rows: the title ellipsized, then a muted subtitle of the relative time, the message count when the host sends it, and the markers (`this chat`, `open in another tab`, `chat`), which never clip (the title gives way). Rows have a hover and selected wash and a thin separator; date-bucket headers have extra space above.
- Every popup of the chat (the pickers, the past-conversations flyout, the slash and @ menu, the Skills list, the send options, the usage popover, the question preview pane) is one visual system from `ChatPopup`: one surface (`Ui.Surface`, a step lighter than the page in the dark theme), `Ui.BorderBrush` border, radius 6 and the same 6 px padding on all four sides; lists, scroll viewers and rows inside have no fill of their own, the only fills being the `Ui.Hover` and `Ui.Selected` washes on the row (radius 4); rows inset 8 px, and the header label, bucket headers and row text share that left line while the search box and the rows' wash share the list's left edge; search placeholders use `Ui.Muted`; nothing extends past the container and long text ellipsizes inside its row. `--claude-chat-ui` checks this in both themes.
- The model, effort and permission-mode pickers are one popup family (`ChatPopup`): a container from the Ui tokens, a small muted header (`Model`, `Effort`, `Permission mode`), rows of one height with a fixed check column that marks the current value without resizing the row, the Ui.Hover and Ui.Selected wash, one click or Enter chooses and closes, Esc closes. Permission-mode rows show the mode's description as a muted one-line second part (full text in the tooltip); the model picker keeps its search, Favorites and Recent sections, stars and hide control; effort levels are lowercase.

What is drawn per panel: the request panel (permission, question, plan) above the composer; the queue strip; the composer with the mention chips, the footer (model, effort and mode pickers, attach, Skills, the `/` button, the context pie, stats, history, Stop, Queue or Send) and the facts line (cwd, MCP, IDE) under it; a status banner for starting, failed or exited, and the host's dismissible banner; the task strip; the history overlay; the past-conversations flyout; the Rewind and full-text dialogs.

Left out on purpose, with the reason:

- Focus view: a setting that is off by default and replaces the transcript with a summary list; the list here is already virtualized and grouped.
- Ratings and bookmarks on replies: they report to the vendor and persist to a store the contract has no place for.
- The Web (remote sessions) tab, session groups and archive: they need claude.ai accounts and state SharpRail does not hold.
- Mentions as chips inside the input: the input is a plain text box; finished mentions show as removable chips above it instead.
- Built-in slash actions SharpRail cannot honestly do: Clear conversation, Export conversation, Rewind from the menu, Thinking, Toggle fast mode, Account & usage, MCP servers, Hooks, Permissions, Status, Sandbox, Memory, Instructions, Output styles, Manage plugins, Claude in Chrome, Claude Design, Bookmarks, General config, Focus view, Open Claude in Terminal, Sign out, Switch account and Mention file from this project. The menu offers Attach file…, Resume conversation, New conversation, Switch model… and View help docs.
- Side questions, the browser and terminal collaborators, pasted-content tokens and the per-row timestamps.

Neutral phrases made up here (checked against the webview spec and its bundle; the extension's own wording is used wherever it exists there): `The code cannot be restored to this point.` (a refusal without a reason), `Couldn't check the code: …`, `Couldn't list sessions: …`, `Working · Ns` (the extension's visible busy word was not found; its screen-reader strings are `Claude is working.` and `Compacting conversation.`), the Rewind dialog's title `Rewind`, `Comment on selection`, `Open thread`, `Open diff: <path>`, `Load full output`, `Jump to latest`, `Send options`, `Stop and send`, `Queue`, `Thinking...` with `· N tokens` (the extension shows `Thinking…` in its activity line and the spec gives `Thinking...` for the block), `1 line` / `N lines` in the secondary lines (the spec writes `line(s)`), `Skills`, `attach file`. Recovered from the bundle and used as is: `No preview for this option`, `Checking code changes…`, `The code has not changed, so no code will be restored.`, `N lines will be removed and M lines will be added across K files:`, `Rewinding does not affect files edited manually or via bash.`, `Never mind`, `Restore conversation` and `Restore code and conversation` (the extension's third choice, `Restore code`, is not offered because `chatRewind` always drops the later turns), `Search sessions…`, `Loading sessions…`, `No sessions yet`, `No sessions found`, `Waiting for permission…`, `Waiting for your answer…`, `Open a new conversation in a new tab or the sidebar`, `Type your answer…`, `Submit answers`, `No, keep planning`, `Send feedback and keep planning`. Still unrecoverable: the extension's visible streaming word, its exact empty-state for a failed session list, and whether `Edit` shows `Modified` for an equal count of added and removed lines.

Interface zoom at 150% is exercised in `--claude-chat-e2e` through the real Mod+= steps: the footer keeps the model chip, history and Send.

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

The terminal chip cases check that the model and effort facts come from the hook report and that `OpenPicker` types
exactly `/model` (or `/effort`) plus Enter once, focuses the terminal, never types a selection and does nothing while
the session is Running. They do not exercise a live Claude Code.

The 2026-10-10 narrow surface-selector check verified the hidden horizontal scrollbar and scrolling
to Account headlessly. It did not check the packaged app or native narrow-pane appearance.

Still required for the full port: complete source-by-source parity audit, the remaining IDE action
and failure cases, the pane-driven halves of the configuration cases (composer dialogs and their narrow-pane
layout, source links after unsaved edits, account/usage and the MCP health row), the review
round trip over MCP, the terminal facts cases and sealing
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
