---
id: module-plugin-agent-ui
type: module-design
status: active
title: Agent UI — shared Codex and Claude Code components
parent: module-plugin-api
depends-on: [module-plugin-ui-kit]
tags: [ui, plugins, public-surface-checked]
---

# Agent UI

Shared presentation code for the Codex and Claude Code plugins lives in
`SharpRail.Plugins.Agent.UI`. Both plugin UI projects reference this module;
neither depends on the other plugin.

## Boundary

The module depends on Avalonia and the general UI kit. It has no host, app,
plugin API or provider-specific dependency. Providers supply facts and callbacks;
the module owns no session, account or terminal state.

- `Account` and `AccountSeverity` present account headings, rows, usage windows,
  reset times and reading timestamps. Account rows and usage windows use compiled XAML.
- `TerminalFacts` presents directory and model facts, token usage, plan progress,
  pickers, file attachment and the session IDE-context toggle.
- `TokenUsage`, `TerminalTodo` and `TerminalTodoStatus` are presentation values,
  adapted from each provider's contract by its UI half.
- `AgentAttention` words an away notification for an `AgentAttentionReason` (blocked,
  done, failed): "*agent* needs you", "*agent* finished" or "*agent* hit an error",
  scoped " — *project*" when one is known, with the first detail the agent reported as
  the body or "Open the terminal for details." It returns text only; the provider decides
  when to ask and hands the result to the plugin API.

Generic scoped settings and value composers remain in the general UI kit.
Provider protocols, configuration, launch behavior and state remain plugin-owned.
The public surface is recorded in `PublicAPI.Unshipped.txt` and checked by
`Microsoft.CodeAnalysis.PublicApiAnalyzers`.

## Acceptance

Moving the components preserves control names, callbacks, formatting, theme brushes
and compiled layouts. Release compilation checks both consumers and the public
surface; `--codex` and `--claude-code` exercise the provider UI integrations.

## Public surface

`Account`, `AccountSeverity`, `AgentAttention`, `AgentAttentionReason`, `TerminalFacts`, `TerminalTodo`, `TerminalTodoStatus`, `TokenUsage`.
