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
  characters so a dropped filename cannot submit or split the prompt. The directory chip shows the path in full, as
  the terminal does (a long one loses its middle), with no home abbreviation. Every chip, the companion buttons above
  the rows included, is the kit's `Chips` family and takes its gap from the `ChipGap` layout role: rows share one left
  edge and one row gap.
- `InstructionSectionsView` is SharpRail's instructions as one context-table entry: a row with the PROJECT
  scope chip, a muted count (`8 sections · 1 off · Choose…`) and a size and source line the provider fills
  (`SetEntry`). The row is a button (hand cursor, hover fill, keyboard-activatable) whose flyout popup holds the
  instruction sections for one agent and workspace, grouped by source; nothing expands inline in the table. Each
  section is compact: a checkbox with its title and token estimate (characters / 4), the id only as the checkbox
  tooltip, and its description as a one-line disclosure that opens the read-only text inline; no preview box.
  Sections load when the view is created (for the count) and again whenever the popup opens.
  The provider supplies the list and toggle
  calls (run off the UI thread) and refreshes it on its change channel; stale results are dropped, a refused
  toggle re-reads the file, and a file with diagnostics disables the checkboxes. It shows the project file's path, that running sessions keep their instructions, and a note while earlier hand
  edits are kept aside. It has no text editing. A re-read updates each section's row in place, keyed by section id (checkbox, title, size, text), so an open
  disclosure and the reader's place survive a settings change; the list is laid out again only when the set or grouping of sections changes.
- `TokenAmount` draws a token count as "1 token"/"N tokens", or "tok"/"toks" when offered less than its
  narrow width; it measures both and draws the one that fits, changing no property during layout.
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

`PromptGuard` is the guard every chip that types a slash command into an agent goes through. `Classify` is a pure function over
the screen's bottom rows (`PromptState.Empty`, `Draft`, `NotAtPrompt`): it reads the line after the prompt glyph, tolerating a box edge and
no-break spaces, counts text between that line and the box's bottom border as a multi-line draft, ignores the agent's
placeholders and hints of a kind every agent shows (`Press up to edit queued messages`, `Try "…"`, `? for shortcuts`, matched by
family), and answers `NotAtPrompt` for an open picker row or when no prompt line exists. `MayTypeAsync` reads the tail off the UI thread,
re-checks the tab and notifies the refusal ("Clear the prompt first: it holds a draft." / "Not at the prompt."). It never
types speculatively. The pre-existing terminal-driving pickers it once served are retired (see the Claude Code and Codex specs). `TerminalFacts.ActionFactChip` builds a read-only fact chip whose click
runs a caller action, with no menu and no chevron.

Generic scoped settings and value composers remain in the general UI kit.
Provider protocols, configuration, launch behavior and state remain plugin-owned.
The public surface is recorded in `PublicAPI.Unshipped.txt` and checked by
`Microsoft.CodeAnalysis.PublicApiAnalyzers`.

## Acceptance

Moving the components preserves control names, callbacks, formatting, theme brushes
and compiled layouts. Release compilation checks both consumers and the public
surface; `--codex` and `--claude-code` exercise the provider UI integrations.
