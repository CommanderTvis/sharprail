# Codex builtin

Ports fork `packages/plugin-codex` at `177abd30c`, with hook report fields refreshed against
fork tip `1c7c90a5a` (workspace naming and its prompt/last-message report fields were removed), and
session-scoped context and launch instructions from fork tip `867bffea5` (wire version 3).
Disabled by default. Host and UI communicate only through plugin contracts. Pi subscription
invitation and bundled AI chat are excluded by the SharpRail product contract.

## Acceptance contract

- Configuration pane has Context, Settings, Capabilities and Account surfaces. Trust gates the
  project TOML layer; source provenance and shadowed values remain visible. Edits preserve comments
  and bookkeeping and reject changes beyond the requested leaf. Instructions follow override/base
  precedence and never overwrite existing files. External file access uses the generic plugin API.
- Hooks install idempotently without granting trust. Status comes from lifecycle events; rollout
  facts carry actual token counts and update_plan records. Nested agents preserve the outer record.
- Launch and editable revival preserve quoting and supported options, refresh terminal MCP ownership
  and apply permission defaults. IDE slash commands submit Enter separately after 250ms.
- Instructions follow Codex's AGENTS.md discovery: the first non-empty `AGENTS.override.md` /
  `AGENTS.md` in `$CODEX_HOME`, then one file per directory from the nearest repository root down to
  the visible Codex tab's CWD, with `project_doc_fallback_filenames` appended; without a repository
  only the CWD counts. The pane describes the first shown Codex terminal (its process CWD, then its last
  hook-reported CWD, then the worktree) under "Context of Codex in tab …" with the directory, or "No
  Codex in tab …" / "No terminal open" with "Showing what a new session here would load". Files inside
  the worktree keep relative editor paths; others open by their registered absolute paths, and the
  external-file registry covers every live Codex tab's chain. Edits and creation offers stay
  workspace-scoped.
- **Tell Codex it is running in SharpRail** defaults on for POSIX launches. Activation writes
  `<stateDir>/codex/developer-instructions.md`, describing SharpRail's tools and the project's
  worktree folder (`SHARPRAIL_WORKTREES_DIR`), like Claude Code's appended system prompt. Each terminal
  gets the file's text as the JSON string `SHARPRAIL_CODEX_PROMPT_JSON`, which the launch line expands
  into Codex's additional `developer_instructions`; a launcher-supplied prompt continues the same
  string rather than adding a second override. Only these launches tag their inherited status URL with
  `thinkrail_prompt=1`, so the first hook records the fact in the agent's command; process detection
  also recognizes the injected header before hooks report. The Context pane lists the file with a
  **launch flag** chip. Revival strips the generated override and reapplies the current setting once;
  changing the setting never affects a running session. Windows keeps the launcher prompt alone.
- Account and model reads use retained bounded app-server connections. Failures invalidate children
  and pending requests; disable and command changes stop descendants. No credential cache is saved.
  POSIX app-server launches enter a private session/process group through a plugin-owned exec helper.
  The host sends TERM to that group, then KILL after one second, including after the leader crashes.
  The helper ships beside the host assembly; Windows retains process-tree termination. Acceptance
  covers normal stop and leader exit with a descendant that ignores TERM, without using a shell to
  interpolate the configured executable or its arguments.
- IDE v0 IPC owns or joins a router, isolates workspaces, rebases subdirectories, requests fresh
  frontend editor snapshots and rejects unsupported versions. Unavailable frontends time out.
- UI lifetimes preserve status, selection, terminal focus and per-session model choices. File attach
  is relative to reported cwd without Claude's @ prefix. Static layouts use compiled XAML.

## Current implementation and remaining gates

The registered launcher supplies fresh Codex glyph controls at the consumer's requested size and
theme tint, retaining its own asset reader across plugin boundaries.

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
reattachment covered by gRPC checks.
The earlier descendant snapshot teardown passed its resistant-child regression. Its replacement
uses a private POSIX process group, with new leader-crash and immediate-stop regression cases.
The .NET build and focused account/configuration/IDE/UI checks pass with that replacement, including
the leader-crash regression. The helper copies transitively beside the checks executable; macOS
compilation follows the requested arm64 or x64 RID with separate native caches. Published placement
and teardown remain to be verified. Account and usage controls carry the upstream licenses.

Focused `--codex` logic and real gRPC UI checks pass: account, configuration edits/external files,
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

Full source-to-draft audit, reactive Start work presentation, editor reload, Windows IPC, native/published/full-suite
gates remain open. The actual fork index exposes reactive launcher models despite stale prose saying otherwise.
Private protocol fixtures do not establish compatibility with an installed real CLI.
