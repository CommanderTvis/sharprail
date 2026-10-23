# Codex builtin

Ports fork `packages/plugin-codex` at `177abd30c`, with hook report fields refreshed against
fork tip `1c7c90a5a` (workspace naming and its prompt/last-message report fields were removed), and
session-scoped context and launch instructions from fork tip `867bffea5` (wire version 3).
Disabled by default. Host and UI communicate only through plugin contracts. Pi subscription
invitation and bundled AI chat are excluded by the SharpRail product contract.

## Acceptance contract

- The host persists the latest session model and effort from status reports and rollout turn
  contexts. Sparse reports preserve known choices for that session; a different session id starts
  fresh. Revival restores its recorded model and effort, replacing older launch overrides.
- Settings supplies optional model and effort defaults for new SharpRail launches. Empty choices
  use Codex's defaults. Model uses `--model`; effort uses the per-launch
  `-c model_reasoning_effort="…"` override. Generic launcher selections, model presets and custom
  arguments take precedence without editing Codex's saved configuration or a running session.
  Unset defaults persist as empty strings so merged settings updates clear earlier overrides.
  SharpRail wire version 4 covers this settings extension: mismatched host/UI halves stay dormant
  rather than having the older strict schema silently fall back to defaults.

- Configuration pane has Context, Settings, Capabilities and Account surfaces. Trust gates the
  project TOML layer; source provenance and shadowed values remain visible. Edits preserve comments
  and bookkeeping and reject changes beyond the requested leaf. Instructions follow override/base
  precedence and never overwrite existing files. External file access uses the generic plugin API.
  Each workspace retains its pane and selected surface; unchanged results preserve the body and local
  input/scroll state. The Account surface retains its control until an explicit account refresh.
- Where Codex draws OSC 133 marks itself (`TerminalPromptChanged` with `Foreground`, for a terminal whose agent record
  is Codex's), they set the status: a prompt mark Idle, a command mark Running; an idle mark leaves the hook's Blocked
  or Done. The shell's own marks around Codex and terminals without marks change nothing, and hooks keep reporting.
- Hooks install idempotently without granting trust. Status comes from lifecycle events; rollout
  facts carry actual token counts and update_plan records. Nested agents preserve the outer record.
- Launch and editable revival preserve quoting and supported options, refresh terminal MCP ownership
  and apply permission defaults. Both run as the terminal's program through the login shell, so nothing
  is typed (Host Terminals.SPEC, Terminal programs); Windows types the revival offer as before. The
  codex CLI (0.163.0) has no way to preassign a session id, so Codex keeps discovering its id from hooks
  and the rollout, and revival without one keeps `resume --last`. IDE slash commands submit Enter separately after 250ms.
  The embedded MCP server is registered as `sharprail`, matching Claude Code and the protocol identity.
  Revival replaces launcher-owned overrides under both the current name and legacy `thinkrail`;
  the existing `THINKRAIL_MCP_URL` environment contract remains compatible with running shells.
  Launcher MCP configuration always approves `set_title`, `spec_get`, `spec_graph`, `spec_grep`,
  `spec_validate`, `blueprint_check`, `lsp_navigate`, `lsp_diagnostics` and `lsp_status` for that session. Revival refreshes these overrides without
  accumulating them; disabling launcher MCP drops them. Other tools retain their configured policy.
  The launcher's right-click menu offers With custom arguments: Start passes the typed shell arguments
  to this run, Cancel starts nothing, and the last submitted arguments are prefilled during this app session.
  On Android, where nothing right-clicks, a tap opens that menu and Start Codex is its first item.
- The prompt appendix directs workspace creation through the host's `workspace_create` tool with a
  visible task description and encourages cleanup of the agent's own completed workspaces. Reuse a
  workspace the user already prepared; create another only for additional isolation or parallel work.
  When unrelated tasks or bug reports accumulate, suggest a subagent and separate workspace per
  independent task before processing the backlog sequentially, even in a session not started as an
  orchestrator. Explain the focused context and separate review benefits; delegation requires
  authorization and available subagent tools.
  It also asks the agent to title its terminal's tab through the host's `set_title` when the task is clear;
  the first title also names an untouched generated managed workspace; manual names are preserved. Cleanup goes through the host's `workspace_delete`
  rather than `git worktree remove`, so Projects follows immediately.
  The appendix also documents the project's `.sharprail/workspace.toml` (schema, root-relative patterns, edit it when adding a shareable dependency or cache directory, effective for workspaces created afterwards).
  The appendix permits `shipping-a-pr` only when the user's prompt indicates pull-request work;
  a request to commit and/or push alone does not trigger the skill.
- POSIX UI launches carry the command-scoped `SHARPRAIL_UI_LAUNCH=codex` marker even when MCP and
  the prompt appendix are disabled. Process detection records the launch origin independently of those
  settings and rechecks it when the process changes. It skips zombie processes (`ps` state Z), so an exited agent not
  yet reaped clears its record. Manual sessions show a persistent terminal notice:
  SharpRail intentionally does not apply launcher settings, tools or the prompt appendix to typed CLI
  commands; the workspace launcher applies the user's settings. Existing hooks and separately configured
  MCP/IDE connections may still work. Unknown origins (including unavailable process inspection on Windows)
  are not labelled manual. Origin persists and crosses local/remote host state; status reports retain it.
- Instructions follow Codex's AGENTS.md discovery: the first non-empty `AGENTS.override.md` /
  `AGENTS.md` in `$CODEX_HOME`, then one file per directory from the nearest repository root down to
  the visible Codex tab's CWD, with `project_doc_fallback_filenames` appended; without a repository
  only the CWD counts. The pane describes the first shown Codex terminal (its process CWD, then its last
  hook-reported CWD, then the worktree), including a terminal visible beside the focused editor, under
  "Context of Codex in tab …" with the directory, or "No
  Codex in tab …" / "No terminal open" with "Showing what a new session here would load". Files inside
  the worktree keep relative editor paths; others open by their registered absolute paths, and the
  external-file registry covers every live Codex tab's chain. Edits and creation offers stay
  workspace-scoped.
- **Tell Codex it is running in SharpRail** defaults on for POSIX launches. Activation refreshes
  `<stateDir>/codex/thinkrail-prompt-codex.md` with the running build's text; it is generated, never the
  user's. The Context pane ends with the shared instructions view (Agent UI `InstructionSectionsView`): the
  sections Codex sessions in the workspace get, by source, each with a checkbox, description, size estimate
  and read-only preview; ticking writes the project's `.sharprail/instructions.toml` disable-list through the
  host (`instructionsList`, `instructionsSet`, `instructionsChanged`; schema in
  [Plugins](../SharpRail.Host.Core/Plugins.SPEC.md#agent-instruction-sections)). Nothing offers free-text
  editing. A file edited under an earlier version is copied once to `thinkrail-prompt-codex.custom.md`
  before it is regenerated; the copy is never used or deleted, and the view notes it while it exists. Spec tools are suggested only when available and the project uses a spec graph;
  projects without specs require no spec workflow or unsolicited spec creation.
  The file describes SharpRail's tools and the project's
  worktree folder (`SHARPRAIL_WORKTREES_DIR`), like Claude Code's appended system prompt. Prompted launches also pass
  `--add-dir "$SHARPRAIL_SCRATCH_DIR"`: the workspace's host-side scratch directory (`<state>/scratch/<project>/<workspace>`,
  created by the host, nothing inside the project) stays writable under Codex's workspace-write sandbox. Each terminal
  gets the file's text as the JSON string `SHARPRAIL_CODEX_PROMPT_JSON`, which the launch line expands
  into Codex's additional `developer_instructions`; a launcher-supplied prompt continues the same
  string rather than adding a second override. Only these launches tag their inherited status URL with
  `thinkrail_prompt=1`, so the first hook records the fact in the agent's command; process detection
  also recognizes the injected header before hooks report. The Context pane lists the file with a
  **launch flag** chip. Revival strips the generated override and reapplies the current setting once;
  changing the setting never affects a running session. Windows keeps the launcher prompt alone.
- Workflow skills: activation copies `choosing-a-workflow`, `brainstorming`, `asking-user-questions`, `spec-graph`,
  `writing-specs`, `setting-up-a-project`, `importing-a-codebase`, `starting-a-new-project`, `shipping-a-pr`
  and `add-highlighting` to `$CODEX_HOME/skills/<name>/`, where Codex discovers a
  user's skills; there is no session-scoped skill root to point a launch at, so every Codex session sees
  them while the plugin is active. The text is the fork's `packages/pi-thinkrail-workflow/skills` and JetBrains ThinkRail's
  workflow and spec-graph skills as adapted in `src/SharpRail.Plugins.Agent.Skills`, the one source shared with Claude Code (see that
  plugin's spec for the adaptation); the fork ships skills to pi only. `<stateDir>/codex/skills.sharprail-default`
  holds a SHA-256 stamp per installed file. A file matching its stamp is SharpRail's: refreshed when the
  shipped text changes, restored when missing, removed when no longer shipped. An edited file is the
  user's and is never overwritten or removed, and a skill directory whose `SKILL.md` SharpRail never wrote
  is left whole. Disabling the plugin removes the unedited files and the directories that leaves empty.
  The disposer cannot tell disabling from host shutdown, so the files may also go when the app quits and
  return on the next start; a second running host is not considered.
- While another plugin governs Codex launches (an `Account` on its launch contribution, Plugin API H18; Air Governance with Central chosen and Codex enabled), the Account method returns that source's account instead and never starts the OpenAI account read or `codex app-server`; the tab shows the source's identity, credits and refill time, or its actionable problem. Ungoverned launches are unchanged. The terminal fact chips show session token totals and the plan, not vendor quota, so they are unchanged.
- Account and model reads use retained bounded app-server connections. Failures invalidate children
  and pending requests; disable and command changes stop descendants. No credential cache is saved.
  POSIX app-server launches enter a private session/process group through a plugin-owned exec helper.
  The host sends TERM to that group, then KILL after one second, including after the leader crashes.
  The helper ships beside the host assembly; Windows retains process-tree termination. Acceptance
  covers normal stop and leader exit with a descendant that ignores TERM, without using a shell to
  interpolate the configured executable or its arguments.
- IDE v0 IPC owns or joins a router, isolates workspaces, rebases subdirectories, requests fresh
  frontend editor snapshots and rejects unsupported versions. Unavailable frontends time out.
- File drops on an active, held terminal insert file/folder references without submitting the prompt.
  The plugin formats paths relative to the agent's working directory and quotes spaces/control characters;
  the workbench supplies host paths and uploads client-local files for remote sessions.
- UI lifetimes preserve status, selection, terminal focus and per-session model choices. File attach
  is relative to reported cwd without Claude's @ prefix. Static layouts use compiled XAML.

## Current implementation and remaining gates

The registered launcher supplies fresh Codex glyph controls at the consumer's requested size and
theme tint, retaining its own asset reader across plugin boundaries. SVG tint resolves on
attachment and follows theme changes, including for glyphs created before a theme is applied.

Instruction-context template acceptance preserves the fork's three missing-file offers, the summed
byte count, ordered instruction rows, user/project scope, full source-path tooltips and source links
that open the reported relative path. Offer creation still opens project files and refreshes the
snapshot. The static offers, total and instruction-row geometry belong in compiled XAML; C# supplies
snapshot values and callbacks. Hidden offers are removed from the rendered list as in the fork.
Capability templates preserve hook installation/trust messages and the install action, each configured
MCP server's target, scope and source link, and the launcher's own MCP explanation. Static geometry
and fixed copy belong in compiled XAML; installing hooks still refreshes the host snapshot.

Recovered contract/host/UI draft is registered with its asset and Tomlyn2.10.1 dependency. Shared
setting calls are adapted to the current kit. Shared account rows and usage windows now use compiled
XAML in the kit. The settings section, pane header/navigation, instruction offers/totals/rows and
capability frames also use compiled XAML. Context checks cover all three offers, override precedence,
user scope and external source links; capability checks cover installation, manual trust and configured
MCP targets/source links. Configuration value cells, row actions, the trust line, the no-match line, notices and the terminal
accessory row (model chip, slots for the kit's usage/plan/IDE/attach chips and the driving status) are
compiled frames too; the kit's shared setting rows and chips stay kit-owned. Settings observers restart on remount, with host changes both while detached and after
reattachment covered by WebSocket RPC checks.
The draft guard shares the kit's `PromptComposer`; Codex's own example prompts (`Explain this codebase`, `Write tests
for @filename`, …) and its follow-up line are matched case-insensitively as placeholders. The list follows Codex's
rotating examples as remembered, not read from a live build, so an unlisted new example is the one wording the
Metal renderer can mistake for a draft; Skia drops it as faint text.
The accessory's driven entries (`/ide on|off`, the `/model` picker) hold the user's keys out of the terminal until
the command has been typed and submitted, or the drive gives up, throws or is cancelled; keys typed meanwhile are
dropped. `--codex` covers the release on every outcome through a fake TUI.
The earlier descendant snapshot teardown passed its resistant-child regression. Its replacement
uses a private POSIX process group, with new leader-crash and immediate-stop regression cases.
The .NET build and focused account/configuration/IDE/UI checks pass with that replacement, including
the leader-crash regression. The helper copies transitively beside the checks executable; macOS
compilation follows the requested arm64 or x64 RID with separate native caches. Published placement
and teardown remain to be verified. Account and usage controls carry the upstream licenses.

Focused `--codex` logic and real WebSocket RPC UI checks pass: skill installation (idempotent, edits and the
user's own skills kept, refresh, removal on disable), account, configuration edits/external files,
context offers, IDE request/reply, launcher options, session-only model selection, lifecycle badges,
separate IDE submission, rollout usage and plan. Raw terminal fixtures own their mode and read through
libc so .NET's console reader does not restore canonical input. The model menu retains its trigger
while the catalog arrives and refreshes an open menu when choices change.

IDE context replies follow the fork's one-reply-per-frontend model as closely as one app context allows:
the active window answers for its workspace and is focused only while the app is; when the active window
shows another workspace (or Home), a background window showing the requested workspace still answers
unfocused, so the host's 1200 ms fallback finds it. Open tabs are the union of every window's editors for
that workspace, where the fork lists only the answering frontend's. The fork's "ChatGPT in Pi" subscription
notice is not ported because Pi is out of scope.

A live status push that settles a terminal blocked (`PermissionRequest`) or done (`Stop`) asks core for
an away notification through `NotifyAttention`, worded by the shared `AgentAttention` ("Codex needs
you", "Codex finished", scoped to the reported CWD's folder). Codex has no failed status, its hooks
carry no summary, and `Interrupt` settles idle, so a cancelled run stays silent. The fork's Codex plugin
raises no desktop notification; this follows its Claude Code plugin so both agents behave alike.
`--notifications` covers it with injected pushes and a recording notifier.

Full source-to-draft audit, reactive Start work presentation, editor reload, Windows IPC, native/published/full-suite
gates remain open. The actual fork index exposes reactive launcher models despite stale prose saying otherwise.
Private protocol fixtures do not establish compatibility with an installed real CLI.

## Shared agent presentation

Account and terminal fact, usage, plan, picker, attachment and IDE-toggle components
come from `SharpRail.Plugins.Agent.UI`. The plugin adapts its own contract values and
retains all provider state and commands; generic settings controls remain kit-owned.

Start work passes host image paths through the launcher options to `--image`, shell-quoted separately from the prompt. The first agent task title names an untouched generated managed workspace through `set_title`.

IDE snapshots include selected diff files as read-only editor context. Continuous Changes uses the selected file within its owner tab; source-side paths, ranges and selected text are retained while the terminal has focus, and closing the owner forgets them. An empty Changes review does not invent a file context.

The 2026-10-14 packaged malformed-response recovery check expected two child processes but observed
one. Inspection found one successful initialize/account-read sequence and no requests from the first
rejected attempt; the fixture discarded its initial exception and used a one-second connection timeout.
This leaves recovery verification unresolved: it does not establish that a malformed-response process
was reused. A later source-gate pass does not replace this packaged acceptance.

Context labels delimit the active tab name with quotation marks, including the no-session fallback, so it is distinct from the explanatory sentence.

## Language tools

The prompt appendix directs navigation and post-edit inspection through `lsp_navigate` and
`lsp_diagnostics`, with `lsp_status` for initialization and recovery. Every call carries the edited
workspace, including delegated agents inheriting the parent's MCP connection. Delegation passes
that path and the inspection instructions. Launch and revival approve these three tools for the session.

Capabilities explicitly lists `lsp_navigate`, `lsp_diagnostics` and `lsp_status` under the injected
thinkrail MCP server, states whether new-launch MCP is enabled, and explains workspace/subagent routing.
Its guidance follows the [workbench’s LSP configuration guidance](../SharpRail.UI/SPEC.md#language-intelligence).
The Windows launcher limitation is explicit; this description does not claim a live connection.

Agent instructions request `lsp_diagnostics` with `paths` for every edited file or
`scope: "workspace"` for project source files, including unopened files. Agents must inspect
per-file checked/pending/skipped/error coverage; scans use running servers only.

Agent guidance explains discovering language servers and explicitly starting each needed server through MCP before mixed-language diagnostic scans, while respecting disabled settings.

System instructions prefer LSP over grep for available semantic navigation, advertise hover,
qualified symbol outlines/body reads and paged source excerpts, and prefer edited-file diagnostic
scans. Text search remains available for strings, configuration, unknown owners and incomplete
or unsupported semantic results; delegated agents are told to write the workspace path, the `workspace` rule and the LSP guidance into the subagent's brief (a subagent inherits the MCP connection, not the prompt). Agents start any language server a task needs without asking, unless language services are disabled.

The system prompt advertises workspace LSP configuration inspection, save and targeted restart,
including library settings and clangd compiler configuration. Saved configuration is visible in
the workspace LSP pane and copied independently from Default for new workspaces.

A separate paragraph of the system prompt documents the project's `.sharprail/language.toml` (portable
server options shared through the project; see the [language services](../SharpRail.Host.Core/Language/SPEC.md#project-language-file)
spec): agents edit it directly, keep comments and unknown keys, and put no host paths or secrets in it.

The instruction text is composed from the host's and the enabled plugins' prompt contributions (Plugin API H20, `PromptTexts`): the plugins' tool items ahead of the text above, and sections after it. The Code Intelligence paragraphs (`lsp_*` tools and `.sharprail/language.toml`) are the host's own text, contributed by the host as a section after this file's text only while language services are enabled. The file is rewritten on activation and for each started terminal, so toggling a plugin, that setting or a project section reaches the next terminal. The Code Intelligence section is composed per workspace: it names only that project's language servers (at most four, ranked by file count from the language pane's bounded census, with their ids for `lsp_status retry`), says that `lsp_status servers` lists the others instead of listing the catalog, and gives examples on a real type, member and file of the primary language (picked from the file names most files share, read without starting a server). The census runs in the background and is cached per workspace (in memory and under the host's language storage); a launch never waits for it, so a workspace's first session gets the generic text and later sessions the project's. Without a workspace (the shared file) the section names no server and uses placeholder examples. Each terminal's `developer_instructions` carry its workspace's composition, less the sections its project turns off.

Agent LSP guidance prefers qualified outline queries over guessed positions, dependency scope and
bounded source retrieval with the returned URI/source provider. Reuse warm sessions and poll import
readiness; retain text-search fallbacks for incomplete or unsupported semantic results.


SharpRail prompt appendices carry the tool guidance enabled plugins contribute (Air Context's context_search/status/index paragraph while that plugin is enabled), then LSP for known symbols/current drafts. Launch commands preapprove the three Air Context tools through the terminal-local MCP override whether or not the plugin is enabled, since the override names tools and the plugin may be turned on later; global Codex configuration is unchanged. The Air Context plugin triggers indexing only for recognized UI-launched agents.

Revival removes and regenerates the three Context approval overrides with the existing SharpRail MCP flags, so repeated resumes cannot accumulate them.

Generated MCP flags use shell continuation lines below the canonical PTY input limit, preserving individual overrides. Revival recognizes those standalone continuations; initial agent launch does not depend on the shell entering raw input mode first.

The prompt file is composed from the template and the enabled plugins' tool guidance (H20) on activation and for every terminal the plugin
starts, so a plugin turned off leaves nothing in it. The Air Context paragraph is the plugin's own.

## Launch contributions

Other plugins may contribute a wrapper, variables or arguments to this agent's launch (Plugin API W22 and H18). The
launcher, its start action and right-click flags, the Create-workspace and Project Home starts, and the host's revive
offer all pass the executable word of the command through `AgentLaunchComposition.Apply` for agent id `codex`, so a
contribution reaches every new or revived terminal. A custom command that is not the `codex` executable is never
rewritten, and a disabled contributor changes nothing.

The appendix is strict: it says an agent MUST use `lsp_navigate` for symbol questions before grep or whole-file reads, MUST run
`lsp_diagnostics` with `paths` on every edited file before declaring done (a build is no substitute), and CAN and MUST start any
missing or stopped server itself through `lsp_status` catalog/retry without asking the user, waiting while it initializes; only
user-disabled settings are respected and reported. A delegating parent MUST put the workspace path, the `workspace` rule, these
rules and a requirement to report its `lsp_navigate`/`lsp_diagnostics` call counts into the brief, and MUST reject a report with
none for code work. Air Context's paragraph (only while enabled) adds MUST `context_search` first. `PromptChecks` asserts these
phrases in both agents and their absence with the feature or plugin off.
