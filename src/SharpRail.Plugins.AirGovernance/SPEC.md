---
id: module-plugin-air-governance
type: module-design
status: draft
title: Air Governance — Codex and Claude Code through JetBrains Central
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins, submodule-ui-panels]
tags: [plugins, central, governance]
---

# Air Governance — Codex and Claude Code through JetBrains Central

Upstream: central-integration.md, packages/shared/src/jbcentral.ts (adapter), packages/server/src/auth/jbcentral.ts
(lifecycle), apps/web/src/panels/JetBrainsAiCard.tsx (card) and e2e/fixtures/bin/central (fixture) @ the fork's
`central-access-api` line. The fork routes Central into `pi` through one extension file; pi is out of scope here, so
this plugin keeps the fork's detection, sign-in, proxy, update and access-source behaviour and replaces the pi
extension with Central's `central run <agent>`, one launch at a time, for Codex and Claude Code.

## Responsibility

Lets the user choose where Codex and Claude Code get their AI, from Settings › Air Governance:

- Direct (default): each agent keeps its own account. The plugin contributes nothing.
- JetBrains AI through Central: each enabled agent (Claude Code, Codex; both on by default) is started through
  JetBrains Air governance by the user's own `central` CLI.

The chosen source and the per-agent switches are the plugin's settings namespace (`source`, `claudeCode`, `codex`),
persisted by the host and effective at once for the next launch. The plugin ships disabled, since governing agents is
the user's choice.

## Mechanism

`central run <agent> [flags] [-- extra-args...]` launches an agent through JetBrains Air with settings passed for that
launch only; Central states that the agent's configuration is not changed. The plugin therefore never edits any agent
configuration and never calls `central add` or `central remove`: nothing under `~/.claude`, `~/.codex`,
`CLAUDE_CONFIG_DIR` or `CODEX_HOME` is written, and switching back leaves nothing to undo.

With Central chosen, the plugin contributes, through the Plugin API's agent launch seam (W22 in the UI half for
launchers and starts, H18 in the host half for revived terminals), a replacing wrapper
`<central> run <claude-code|codex> --` to launcher ids `claude` and `codex`. The agent plugins apply it to the
executable word of the command they build: the launcher, its right-click flags, the Create-workspace and Project Home
starts, resumes and the host's revive offer, so the agent's usual arguments follow the `--`. SharpRail's own
environment prefixes stay outermost. A custom command in the agent's settings (not the default `claude` or `codex`)
is never rewritten and so is not governed; the pane says so. Running terminals keep the command they started with.
`<central>` is the path the host found, else the bare name: if Central is missing the launch fails in the shell
rather than silently starting an ungoverned agent. The pane shows the exact command shape each agent will get.

Central chooses its own AI access source and proxy; `central access <id>` (switch, then `central proxy restart`)
stays the way to change it, as in the fork.

## Detection and actions

The adapter is the fork's, bounded the same way: the binary is the first `central` on the host's PATH or
`~/.local/bin/central`; `--version` (5 s) must parse as `central X.Y.Z` and be at least 1.4.0 (outdated otherwise,
malformed and probe-failed distinguished); `central status` (15 s) is read only for its `Auth` and `Proxy` rows,
and only a positively observed `not connected` or `stopped` sets a flag. Output is capped at 16 KiB, timeouts kill
the process tree, stderr is discarded.

Opening the pane runs `central --version` only. Everything else is an explicit button: Check sign-in
(`status`), Sign in (`login`, browser, waits 1.5 s for a fast failure), Sign out (`logout`), Start proxy
(`proxy start`), Update Central (`update --install`), List access sources (`access`) and Switch (`access <id>`,
then `proxy restart`; a failed restart is a warning). A missing CLI shows the platform install command from
central-cli.labs.jb.gg as text; the plugin never runs it.

## Untrusted output

Central's output is data. Only the version, the sign-in and proxy verdicts, exit codes (as fixed messages) and
access-source display names reach the pane; names are the one Central-derived text shown (as in the fork), stripped
of control characters and capped at 80 characters. Selection ids must match `workspace:<id>[:<id>]` or
`license:<id>` before they are passed back as an argument. The plugin stores and logs no credentials, tokens or
Central output, and never passes `--accept-tos`.

## Threading

All process and filesystem work is in the host half behind plugin RPC; the pane only builds controls and applies
replies. Mutating calls are serialised and publish a busy status on the `status` channel before and after.

## Verification and limits

`--air-governance` runs the host and pane checks against a fake `central` modelled on the fork's fixture (version,
status rows, access, proxy, login, update) plus a `run` that records its argv, under a throwaway home: detection,
the contributions per source and toggle, the Claude and Codex launch lines executed by a real shell so prompts with
quotes and `$(...)` arrive as single arguments, a fake HOME whose `.claude`, `.codex`, `CLAUDE_CONFIG_DIR` and
`CODEX_HOME` stay byte- and time-identical across every action and source switch, failures, access switching, the
host seam over plugin RPC (including disable) and the pane and real launchers through headless input.
Not verified against the real CLI: the exact `central status` and `central access` output formats, that
`central run <agent> -- <args>` forwards SharpRail's prompt, plugin-dir and MCP arguments and environment to the agent
unchanged, how `central run` picks the access source, and the ToS prompt of `central access`. The parsers follow the
fork's.
