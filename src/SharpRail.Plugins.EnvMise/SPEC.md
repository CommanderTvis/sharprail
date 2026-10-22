---
id: module-plugin-env-mise
type: module-design
status: active
title: mise Environment — mise tools and variables in every shell, as a builtin plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins]
tags: [plugins, terminals]
---

# mise Environment — mise tools and variables in every shell, as a builtin plugin

## Responsibility

A new worktree starts without what the main checkout had set up. This plugin (`env-mise`, order 100) is one step of
the host's workspace environment pipeline (Plugin API H22): it edits the environment the shell of a workspace starts
with, so terminals, and the agents run inside them, are ready in a worktree. It is builtin and **enabled by default**,
independent of the other environment plugins (disabling one leaves the rest working), and with nothing to apply it
runs nothing and contributes nothing. There is no UI half and no setting: Settings › Plugins is the off switch.

Two projects, `Contract` (`SharpRail.Plugins.EnvMise`: manifest, empty settings, the order) and `Host`
(`SharpRail.Plugins.EnvMise.Host`). The namespace avoids `Environment`, which would shadow `System.Environment` in
every plugin. The pipeline itself (ordering, the time budget, the cache, running tools, stripping the host's token) is
the host's: [Plugins.SPEC.md](../SharpRail.Host.Core/Plugins.SPEC.md).

## Behaviour

When the workspace has mise configuration (`mise.toml`, `.mise.toml`, the `.local` variants, `.tool-versions`, `.config/mise*`, `.mise/config.toml`) and `mise` is found, applies `mise env --json`: the tool versions' PATH and the `[env]` table.

In zsh and fish the step also adds `eval "$(mise activate zsh)"` (fish: `mise activate fish | source`) to the shell, guarded by `command -v mise`, so the shell keeps the
environment in sync as it changes directory; the host runs it after the user's own start-up files (see `Terminals.SPEC.md`).
On success it reports a `mise` chip.

mise is a dependency of this plugin, not something it installs. It is found on PATH, at `$MISE_INSTALL_PATH`, or in a
well-known directory (`/opt/homebrew/bin`, `/usr/local/bin`, Linuxbrew, MacPorts, `~/.local/bin`, `~/.local/share/mise/bin`,
`~/.cargo/bin`), because an app started from the dock or Finder has only the system's PATH. The shell hook names the
binary by absolute path. A workspace with mise configuration and no mise is logged and shows a `mise missing` chip
(`brew install mise`); an untrusted configuration shows `mise untrusted`, and a failing `mise env` shows `mise failed`
with its first error line. mise runs against the user's own configuration and data directory, so a repository's `mise.toml` must not depend on
variables only its own wrapper sets, and the tools only appear once installed in that data directory (`mise install`;
SharpRail's `scripts/mise` uses a system mise and that default directory too).

## Trust in a new worktree

mise refuses unreviewed configuration per path. A worktree inherits the default workspace's decision only when every file of the list above is byte-identical to the default workspace's and `mise env --json` already succeeds there; the plugin then runs `mise trust` in the worktree and logs it. Anything else stays untrusted and is logged with the command to run. Edited configuration is never trusted by the plugin.

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
