---
id: module-plugin-env-direnv
type: module-design
status: active
title: direnv Environment — the .envrc environment in every shell, as a builtin plugin
parent: architecture
depends-on: [module-plugin-env]
references: [module-plugin-api, submodule-host-plugins]
tags: [plugins, terminals]
---

# direnv Environment — the .envrc environment in every shell, as a builtin plugin

## Responsibility

A new worktree starts without what the main checkout had set up. This plugin (`env-direnv`) is one step of
the workspace environment pipeline (Plugin API H23), a dependent of `Env`: it edits the environment the shell of a workspace starts
with, so terminals, and the agents run inside them, are ready in a worktree. It is builtin and **enabled by default**,
independent of the other providers (disabling one leaves the rest working) but not of `Env`: it registers exactly one step and `dependsOn` `Env`, so it goes off with it, and with nothing to apply it
runs nothing and contributes nothing. There is no UI half and no setting: Settings › Plugins is the off switch. Its position in the pipeline and its chip are `Env`'s to place; the step supplies the chip data.

Two projects, `Contract` (`SharpRail.Plugins.EnvDirenv`: manifest, empty settings, the step id) and `Host`
(`SharpRail.Plugins.EnvDirenv.Host`). The namespace avoids `Environment`, which would shadow `System.Environment` in
every plugin. The pipeline itself is the host's mechanism with `Env`'s policy: [Plugins.SPEC.md](../SharpRail.Host.Core/Plugins.SPEC.md), [Env](../SharpRail.Plugins.Env/SPEC.md).

## Behaviour

When the workspace has an `.envrc` and `direnv` is found (on PATH or in a well-known directory such as `/opt/homebrew/bin`), applies `direnv export json` (a null value removes the variable from the step's copy). It runs after mise, so `use mise` sees the same tools; nix and devenv setups work through the `.envrc`.

In zsh and fish the step also adds `eval "$(direnv hook zsh)"` (fish: `direnv hook fish | source`) to the shell, guarded by `command -v direnv`, so the shell keeps the
environment in sync as it changes directory; the host runs it after the user's own start-up files (see `Terminals.SPEC.md`).
On success it reports a `direnv` chip.

A workspace with an `.envrc` and no direnv shows a `direnv missing` chip, a blocked one `direnv blocked` (with the
`direnv allow` to run) and a failing export `direnv failed`.

## Trust in a new worktree

direnv refuses an unreviewed `.envrc` per path. The step reads `direnv status --json`; for a blocked `.envrc` that is byte-identical to the default workspace's and allowed there, it runs `direnv allow` in the worktree and logs it. Anything else stays blocked and is logged with the command to run. A denied `.envrc` is left alone.

## Limits

- A login shell's own start-up files run after the host passes the environment. On macOS `path_helper` can move
  system directories ahead of contributed PATH entries until the user's shell hook re-applies them.
- Language servers and tasks the host starts itself do not get this environment yet; terminals (and the agents in
  them) do.

## Verification

`--environment` (gate case `environment`), shared by the environment plugins: fake `mise` and `direnv` scripts on
PATH (neither is a prerequisite of the checks), composition order, trust inheritance, each plugin working alone, and a
real `/bin/sh` whose first shell has the result. The real tools' JSON formats were taken from their documentation and
not run against the real binaries here.
