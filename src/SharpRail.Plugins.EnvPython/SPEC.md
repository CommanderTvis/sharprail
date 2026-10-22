---
id: module-plugin-env-python
type: module-design
status: active
title: Python Environment — the workspace .venv in every shell, as a builtin plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins]
tags: [plugins, terminals]
---

# Python Environment — the workspace .venv in every shell, as a builtin plugin

## Responsibility

A new worktree starts without what the main checkout had set up. This plugin (`env-python`, order 300) is one step of
the host's workspace environment pipeline (Plugin API H22): it edits the environment the shell of a workspace starts
with, so terminals, and the agents run inside them, are ready in a worktree. It is builtin and **enabled by default**,
independent of the other environment plugins (disabling one leaves the rest working), and with nothing to apply it
runs nothing and contributes nothing. There is no UI half and no setting: Settings › Plugins is the off switch.

Two projects, `Contract` (`SharpRail.Plugins.EnvPython`: manifest, empty settings, the order) and `Host`
(`SharpRail.Plugins.EnvPython.Host`). The namespace avoids `Environment`, which would shadow `System.Environment` in
every plugin. The pipeline itself (ordering, the time budget, the cache, running tools, stripping the host's token) is
the host's: [Plugins.SPEC.md](../SharpRail.Host.Core/Plugins.SPEC.md).

## Behaviour

Applied in this order; an earlier step that already chose `VIRTUAL_ENV` or `CONDA_PREFIX` (direnv's `layout python`)
keeps it, and nothing is created or installed.

- pyenv: a `.python-version` and an existing `$PYENV_ROOT/shims` (default `~/.pyenv`) set `PYENV_VERSION` (every
  version the file lists, joined by `:`) and `PYENV_ROOT` and put the shims on PATH.
- An in-project venv: `.venv`, `venv` or `env` holding `pyvenv.cfg` sets `VIRTUAL_ENV` and puts its `bin`
  (`Scripts` on Windows) first on PATH. This covers venv, virtualenv, uv, pdm, and poetry or pipenv configured to keep
  the environment in the project.
- A conda prefix: the same directories holding `conda-meta` set `CONDA_PREFIX` and put its `bin` first.
- An environment kept elsewhere: with no in-project one, a `poetry.lock` and `poetry` on PATH ask
  `poetry env info --path`, and a `Pipfile` or `Pipfile.lock` and `pipenv` ask `pipenv --venv` (both found on PATH, in Homebrew's `bin` or in `~/.local/bin`); a returned directory
  holding `pyvenv.cfg` is activated like a venv. Neither tool is asked to create one.

## Trust in a new worktree

No trust is carried over. The only tools run are `poetry env info --path` and `pipenv --venv`, which create nothing. A venv is not relocatable, so a git-ignored `.venv` copied into a worktree still points at its origin; a worktree activates only an environment of its own.

## Limits

- A login shell's own start-up files run after the host passes the environment. On macOS `path_helper` can move
  system directories ahead of contributed PATH entries until the user's shell hook re-applies them.
- Language servers and tasks the host starts itself do not get this environment yet; terminals (and the agents in
  them) do.

## Verification

`--environment` (gate case `environment`), shared by the four environment plugins: fake `mise` and `direnv` scripts on
PATH (neither is a prerequisite of the checks), composition order, trust inheritance, each plugin working alone, and a
real `/bin/sh` whose first shell has the result. The real tools' JSON formats were taken from their documentation and
not run against the real binaries here.
