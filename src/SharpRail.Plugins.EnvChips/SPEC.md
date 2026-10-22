---
id: module-plugin-env-chips
type: module-design
status: active
title: Environment Chips — the environments applied to a terminal, as chips, as a builtin plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins, submodule-ui-plugins]
tags: [plugins, terminals]
---

# Environment Chips — the environments applied to a terminal, as chips, as a builtin plugin

## Responsibility

The environment plugins (mise, direnv, Python, JavaScript, JVM) change what a workspace's shells start with, which is
invisible. This plugin (`env-chips`, enabled by default, category Environment) shows it: under each terminal, one chip
per environment that applied to the terminal's workspace, in the order the steps ran, hidden while none applies. A chip
reads like `mise`, `direnv`, `venv`, `pyenv 3.12.1`, `node 20.11.1`, `node_modules`, `JDK 21.0.2-tem`; its tooltip holds
what the step said (the directory or file it came from) and `Sets <variables>`, the variables that step set, PATH among them.

Three projects: `Contract` (`SharpRail.Plugins.EnvChips`), `Host` and `UI`. It has no setting; turning the plugin off
removes the chips and leaves the environments applied.

## Flow

- Host: `OnWorkspaceEnvironment` (Plugin API H22) delivers each resolution's facts for a workspace directory. The host
  keeps the latest per workspace and publishes `changed` (a state channel keyed by `workspaceId`, snapshot `get`) only
  when the chips differ.
- UI: a terminal accessory (W11) per terminal subscribes to `changed` for the terminal's workspace and redraws its chips on
  the UI thread; the first snapshot supplies the chips of a workspace resolved before the terminal opened. The chips are
  `TerminalFacts.FactChip` from the agent UI kit, so they look like the agent plugins' fact chips.
- Chips describe the last resolution of the workspace, not one shell: a shell that started earlier keeps the environment
  it was given until it is reopened.

## Limits

- A workspace whose first resolution has not finished shows no chips until it does (at most the host's 10 second wait,
  usually before the shell even starts).
- Chips are read-only; there is no click action yet.

## Verification

`--environment`: the plugin knows the labels that applied to a worktree after a real shell started there. `--plugins`:
the roster lists the plugin under Environment and its UI half loads.
