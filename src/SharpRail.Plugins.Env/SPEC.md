---
id: module-plugin-env
type: module-design
status: draft
title: Env — the workspace environment pipeline policy and chips, as a builtin plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins, submodule-ui-plugins]
tags: [plugins, terminals]
---

# Env — the workspace environment pipeline policy and chips, as a builtin plugin

## Responsibility

What a workspace's shells start with is invisible and, in a new worktree, missing. `Env` (`env`, enabled by default,
category Environment) owns the abstraction of the workspace environment; the mise, direnv, Python, JavaScript and JVM
plugins are its dependents, each providing one step. It owns:

- Order. The steps run mise, direnv, Python, JavaScript, JVM, so direnv sees mise's tools and the project's own
  binaries end up first on PATH. A step id the policy does not know runs last, ties by plugin id.
- Merge. Each step sees the earlier steps' edits; the result is the variables that differ from the host's own. PATH
  entries a step adds go in front; a removed variable affects later steps only.
- Failure and timeout. A step that throws is logged against its plugin and skipped; a resolution is bounded as the
  host's pipeline says (10 second shell wait, 30 second tool deadline, last good result).
- Chips: under each terminal, one chip per step that reported, in the order the steps ran, hidden while none applies.
  A chip reads like `mise`, `direnv`, `venv`, `pyenv 3.12.1`, `node 20.11.1`, `JDK 21.0.2-tem`; its tooltip holds what the
  step said and `Sets <variables>`. The step supplies the chip data (label, detail, variables it set); `Env` merges them.

It does not spawn processes or read project files: the steps do, through the host. The mechanism stays in
Host.Core, because terminals need the environment before plugin UI loads (startup performance): the host runs the
steps, `Env` supplies the policy ([Plugins](../SharpRail.Host.Core/Plugins.SPEC.md#workspace-environment-pipeline-h22-h23)).

## Hooks

H22 (the policy and the resolution observer), H23 (a step, on each dependent), W11 (the chips accessory) and the
state channel `changed` (keyed by `workspaceId`, snapshot `get`, published only when the chips differ). H23 and the
`Env` side of H22 are a host-hooks surface increase over the former single H22; `dependsOn` is the existing manifest
mechanism ([Plugins](../SharpRail.Host.Core/Plugins.SPEC.md#settings-namespaces)), not a new one.

## Switching off

Switching `Env` off switches its dependents off with it (the settings cascade); workspaces then launch with the plain
environment and no chips. Turning `Env` back on does not bring the dependents back. Chips describe the last resolution
of a workspace, not one shell: a shell that started earlier keeps the environment it was given.

## Limits

- A workspace whose first resolution has not finished shows no chips until it does.
- Chips are read-only.

## Implementation checklist (nothing below exists yet)

- New projects `SharpRail.Plugins.Env` (`Contract`, `Host`, `UI`), absorbing `SharpRail.Plugins.EnvChips`; delete the
  latter and its builtin registration.
- Plugin API and Host.Core: `WorkspaceEnvironmentStep` (H23) replaces the ordered `WorkspaceEnvironment(order, step)`;
  `WorkspaceEnvironment(policy)` and `OnWorkspaceEnvironment` (H22) move to `Env`; no active policy means no steps run.
- Each of the five step plugins: drop the order, register one step, add `dependsOn: env` (exact wire version).
- Builtin registration in `BuiltinPlugins.cs` and the Android csproj: add `Env`, remove `EnvChips`.
- Settings: `env-chips` keys under `plugins` migrate to `env` (it had none besides `enabled`); a profile that had
  `env-chips` off gets `env` off, which turns the dependents off, so migrate only that case.
- `--environment` and `--plugins` checks: cover the cascade and the plain environment while `Env` is off; update
  `docs/develop/agent-reference.md`.
