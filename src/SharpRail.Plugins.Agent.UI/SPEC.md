---
id: module-plugin-agent-ui
type: module-design
status: active
title: Agent UI — shared Codex and Claude Code components
parent: module-plugin-api
depends-on: [module-plugin-ui-kit]
tags: [ui, plugins]
---

# Agent UI

Shared presentation code for the Codex and Claude Code plugins lives in
`SharpRail.Plugins.Agent.UI`. Both plugin UI projects reference this module;
neither depends on the other plugin.

## Boundary

The module depends on Avalonia, the general UI kit and the base plugin API's value
types (for the instruction sections). It has no host, app, contract or provider-specific dependency. Providers supply facts and callbacks;
the module owns no session, account or terminal state.

- `Account` and `AccountSeverity` present account headings, rows, usage windows,
  reset times and reading timestamps. Account rows and usage windows use compiled XAML. `Account.Governed`
  presents the account of an agent whose launches another plugin governs (source, identity, plan, credits bar and
  line, or one actionable problem message) from plain values; the provider maps the plugin API's governed account onto it.
- `TerminalFacts` presents directory and model facts, token usage, plan progress,
  pickers, file attachment and the session IDE-context toggle. File-reference quoting escapes control
  characters so a dropped filename cannot submit or split the prompt.
- `InstructionSectionsView` lists SharpRail's instruction sections for one agent and workspace, grouped by
  source like Blueprint's checkbox controls: a checkbox per section with its title, description, id and a
  token estimate (characters / 4), and a collapsed read-only preview. The provider supplies the list and toggle
  calls (run off the UI thread) and refreshes it on its change channel; stale results are dropped, a refused
  toggle re-reads the file, and a file with diagnostics disables the checkboxes. It shows the project file's path,
  that it is shared with the team, that running sessions keep their instructions, and a note while earlier hand
  edits are kept aside. It has no text editing.
- `ManualLaunchNotice` is a compiled terminal notice explaining intentionally absent launcher
  additions and the workspace launcher alternative. Providers show it only for a known manual origin;
  independently configured hooks and MCP/IDE connections remain available.
- `TokenUsage`, `TerminalTodo` and `TerminalTodoStatus` are presentation values,
  adapted from each provider's contract by its UI half.
- `AgentAttention` words an away notification for an `AgentAttentionReason` (blocked,
  done, failed): "*agent* needs you", "*agent* finished" or "*agent* hit an error",
  scoped " — *project*" when one is known, with the first detail the agent reported as
  the body or "Open the terminal for details." It returns text only; the provider decides
  when to ask and hands the result to the plugin API.

`PromptComposer` decides whether an agent's prompt holds typed text, for the guards that refuse to drive a slash
command over a draft ("Send or clear what you typed first"). It reads the line after the agent's prompt glyph
(tolerating a box edge and no-break spaces) and returns null for an empty prompt, an open picker, the agent's own
placeholders and any hint of a kind every agent shows: a key to press (`Press up to edit queued messages`, `Press
Esc twice to clear`), a quoted suggestion (`Try "…"`), `? for shortcuts`, `/ for commands`, `Type a message`. Hints
are matched by family, not whole sentence, so a new wording of an existing kind needs no release; a draft that merely
starts with `Press` or `Try` is still a draft. The primary defence is cell styling: where the renderer can blank faint
cells (`BufferTail(omitFaint: true)` on Skia) hints never reach this text at all, whatever they say. Only the glyph
line is read, so a multi-line draft is reported by its first line and one whose first line is empty is not seen.
The cursor position is not available from text, which is why the styling rule carries the cursor case.

Generic scoped settings and value composers remain in the general UI kit.
Provider protocols, configuration, launch behavior and state remain plugin-owned.
The public surface is recorded in `PublicAPI.Unshipped.txt` and checked by
`Microsoft.CodeAnalysis.PublicApiAnalyzers`.

## Acceptance

Moving the components preserves control names, callbacks, formatting, theme brushes
and compiled layouts. Release compilation checks both consumers and the public
surface; `--codex` and `--claude-code` exercise the provider UI integrations.
