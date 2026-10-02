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
direct calls or gRPC. Claude Code's own IDE protocol remains an authenticated loopback WebSocket
MCP server: replacing it with gRPC would break the external CLI's protocol.

The prompt appendix directs workspace creation through the host's `workspace_create` tool with a
visible task description and encourages cleanup of the agent's own completed workspaces. Reuse a
workspace the user already prepared; create another only for additional isolation or parallel work.
When unrelated tasks or bug reports accumulate, suggest a subagent and separate workspace per
independent task before processing the backlog sequentially, even in a session not started as an
orchestrator. Explain the focused context and separate review benefits; delegation requires
authorization and available subagent tools.
It also asks the agent to title its terminal's tab through the host's `set_title` when the task is clear;
the tool titles the terminal only, never its workspace. Cleanup goes through the host's `workspace_delete`
rather than `git worktree remove`, so Projects follows immediately.
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

## Configuration

The pane resolves configuration for the first shown Claude terminal, then the first shown terminal,
then the workspace root. Every configuration/read/write/edit/CLI operation carries the selected
terminal key. The host uses the detected process's current directory, its reported directory, then
the workspace root. Appended system prompt files are additional instruction layers.

SharpRail's own appended prompt lives at `<stateDir>/claude-code/thinkrail-prompt-claude.md`. Activation refreshes
it with the running build's text only while it still matches the text SharpRail last wrote (a SHA-256
stamp beside it); once the user edits it, it is theirs and is never overwritten. Settings › Claude Code
and the Context pane open it in an editor tab (it is always an exposed external file), and Settings
offers Reset to SharpRail's text while it is edited.
Spec tools are suggested only when available and the project uses a spec graph; projects without
specs require no spec workflow or unsolicited spec creation.

Settings retain the winning source and shadowed values. Context includes user and ancestor
instructions, `AGENTS.md` when a directory has no `CLAUDE.md`, local instructions, rules, imports,
memory and appended prompt files. Capabilities include MCP servers, plugins, skills, agents, hooks
and marketplaces with provenance, disabled sources and discovery problems.

Every mutation is composed, scoped to user/project/local, planned as a diff, reviewed and then
applied with the plan's content hash. Changing scope invalidates its preview; stale files require a
new review. Source links expose only discovered files and reveal JSON key paths in current editor
contents. CLI plugin/marketplace changes show the exact command before execution. Merely enabling
the SharpRail plugin does not approve a fresh Claude hook installation.

## Status and recovery

Activation owns the process poll, bridge, terminal environment, revive hook and status route.
Disable removes them and stops reading Claude configuration and polling processes. Nested agents
do not replace an outer terminal agent record.

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
fork's standard-directory fallback.

Revive retains invocation flags, removes old positional prompts and resume/continue flags, then
offers a known surviving session id or the fork's continue fallback. Blueprint's independent revive
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
`close_tab` takes the name the CLI gave `openDiff` or a file's absolute path. `openFile` reveals the
line where `startText` first occurs. `openDiff` still opens the target file and answers
`diffShown: false`, which the CLI treats as a failed diff and answers by asking in its own terminal:
Git diff tabs do not represent unsaved proposed content, so `FILE_SAVED` and `DIFF_REJECTED` have
nothing to stand on yet. `getDiagnostics` and `executeCode` are not offered.

UI contributions are the Context/Settings/Capabilities/Account pane, compiled settings section,
workspace launcher, generic `claude` launcher, status tab decorations and one terminal accessory
combining install offer, facts, model/effort pickers, plan, tokens, attach and session `/ide` switch.
Tab marks refresh as soon as the host identifies or clears Claude in a terminal, without switching
tabs or reopening a pane. The host agent record remains the source of truth for terminal identity.
The surface selector reserves space for its horizontal scrollbar when narrow, keeping buttons unobscured.
Each workspace retains its pane and selected surface. An unchanged configuration/capability result
preserves the body, scope and surface controls, including local input and scroll state.
The generic launcher supplies a fresh Claude SVG icon through its owning asset reader for
consumer plugins, at the requested size and theme tint.
The four model aliases are opus/fable/sonnet/haiku. Launch flags use shell syntax appropriate to the
host, with agent view disabled by default and the host's prompt file first. Caller prompts stack
independently. The settings switches affect app-started sessions, not commands the user types.

Agent terminals request Shift+Enter as raw Escape followed by Return when neither kitty nor
modifyOtherKeys mode 2 is negotiated. Ghostty handles negotiated protocols itself. Clearing the
agent identity restores its normal encoding; the request survives terminal startup and retry.
Host-state updates retain the accessory mount so an open picker keeps its control identity.

The marketplace contains its hook plugin and MCP declaration. Reports POST rather than emitting
OSC 777, which unrelated terminals interpret as user notifications. Installed cached versions are
inspected and refreshed after prior approval; a new installation requires the offer's confirmation.

## Workflow skills

The marketplace plugin carries five skills in its `skills/` directory, which Claude Code discovers
from an installed plugin: `writing-specs`, `setting-up-a-project`, `importing-a-codebase`,
`starting-a-new-project` and `shipping-a-pr` with its phase documents. They are the fork's
`packages/pi-thinkrail-workflow/skills` at `3b7c7d589`; the fork ships them to pi only, so delivery to
this CLI is SharpRail's own. Their single source is `src/SharpRail.Plugins.Agent.Skills`, staged into
the plugin at build and shared with the Codex plugin; neither plugin references the other.

The text differs from the fork's only where pi's was untrue here: the app is SharpRail, pi's
`ask_user_question`, `edit`, `web_search` and `fetch_content` become the session's own means, the
unported skills (`brainstorming`, `asking-user-questions`, `choosing-a-workflow`, the spec-graph skill)
are no longer named, and working files go to `.sharprail/context` where a workspace has it, else a
temporary directory outside the repository. The four spec skills follow the prompt's rule: their
descriptions offer them only when the user asks for specs or the project keeps a spec graph, and the
dispatcher stops when the `spec_*` tools are absent.

The plugin is enabled at user scope, so every Claude Code session sees the skills, not only those
SharpRail starts. Adding them raised the plugin version to 0.4.0; that is what brings an approved
installation's cached copy forward. A change to any skill needs another version raise for the same reason.

## Verification and remaining gates

`--claude-code` checks isolated host enable/disable, configuration provenance, review without writes,
stale preview refusal, preservation, token routes, session identity, spending, continuation Stop,
revive, client folding, the shipped skills' shape and their arrival in an installation approved one version earlier, a real authenticated WebSocket IDE initialization/catalogue/zero-based selection/action
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

Still required for the full port: complete source-by-source parity audit, the remaining IDE action
and failure cases, the pane-driven halves of the configuration cases (composer dialogs, narrow-pane
scrolling, source links after unsaved edits, account/usage and the MCP health row), the review
round trip over MCP, the terminal facts cases, terminal picker and sealing
input behavior, a real macOS notification seen and clicked in the signed bundle (the checks stop
at the notifier seam), compiled static pane/dialog layouts, remote
UI parity, native appearance and the full repository verification gates. Current focused checks
do not establish these remaining requirements.

## Shared agent presentation

Account and terminal fact, usage, plan, picker, attachment and IDE-toggle components
come from `SharpRail.Plugins.Agent.UI`. The plugin adapts its own contract values and
retains all provider state and commands; generic settings controls remain kit-owned.
