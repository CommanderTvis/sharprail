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
- Hooks install idempotently without granting trust. Status comes from lifecycle events; rollout
  facts carry actual token counts and update_plan records. Nested agents preserve the outer record.
- Launch and editable revival preserve quoting and supported options, refresh terminal MCP ownership
  and apply permission defaults. IDE slash commands submit Enter separately after 250ms.
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
  The appendix permits `shipping-a-pr` only when the user's prompt indicates pull-request work;
  a request to commit and/or push alone does not trigger the skill.
- POSIX UI launches carry the command-scoped `SHARPRAIL_UI_LAUNCH=codex` marker even when MCP and
  the prompt appendix are disabled. Process detection records the launch origin independently of those
  settings and rechecks it when the process changes. Manual sessions show a persistent terminal notice:
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
  `<stateDir>/codex/thinkrail-prompt-codex.md` while it still matches the text SharpRail last wrote (a
  SHA-256 stamp beside it); once the user edits it, it is theirs and is never overwritten. Settings › Codex
  and the Context pane open it in an editor tab, and Settings offers Reset to SharpRail's text while it
  is edited. Spec tools are suggested only when available and the project uses a spec graph;
  projects without specs require no spec workflow or unsolicited spec creation.
  The file describes SharpRail's tools and the project's
  worktree folder (`SHARPRAIL_WORKTREES_DIR`), like Claude Code's appended system prompt. Each terminal
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

Agent LSP guidance prefers qualified outline queries over guessed positions, dependency scope and
bounded source retrieval with the returned URI/source provider. Reuse warm sessions and poll import
readiness; retain text-search fallbacks for incomplete or unsupported semantic results.


SharpRail prompt appendices advertise context_search for unfamiliar code, context_status/index for readiness and refresh, and LSP for known symbols/current drafts. Launch commands preapprove these three workspace-gated tools through the terminal-local MCP override; global Codex configuration is unchanged. Capabilities describes explicit workspace cloud enablement. Core triggers indexing only for recognized UI-launched agents.

Revival removes and regenerates the three Context approval overrides with the existing SharpRail MCP flags, so repeated resumes cannot accumulate them.

Generated MCP flags use shell continuation lines below the canonical PTY input limit, preserving individual overrides. Revival recognizes those standalone continuations; initial agent launch does not depend on the shell entering raw input mode first.

Air Context prompt guidance explains meaning-based code discovery and directs agents to
use it proactively as the first step for unfamiliar behavior whenever workspace-enabled.
Disabled or unready indexing falls back to LSP and targeted text search without blocking;
user-only enablement/sign-in and current-draft limitations remain explicit.
