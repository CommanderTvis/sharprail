---
id: module-plugin-env-javascript
type: module-design
status: active
title: JavaScript Environment — node_modules/.bin on PATH in every shell, as a builtin plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins]
tags: [plugins, terminals]
---

# JavaScript Environment — node_modules/.bin on PATH in every shell, as a builtin plugin

## Responsibility

A new worktree starts without what the main checkout had set up. This plugin (`env-javascript`, order 400) is one step of
the host's workspace environment pipeline (Plugin API H22): it edits the environment the shell of a workspace starts
with, so terminals, and the agents run inside them, are ready in a worktree. It is builtin and **enabled by default**,
independent of the other environment plugins (disabling one leaves the rest working), and with nothing to apply it
runs nothing and contributes nothing. There is no UI half and no setting: Settings › Plugins is the off switch.

Two projects, `Contract` (`SharpRail.Plugins.EnvJavaScript`: manifest, empty settings, the order) and `Host`
(`SharpRail.Plugins.EnvJavaScript.Host`). The namespace avoids `Environment`, which would shadow `System.Environment` in
every plugin. The pipeline itself (ordering, the time budget, the cache, running tools, stripping the host's token) is
the host's: [Plugins.SPEC.md](../SharpRail.Host.Core/Plugins.SPEC.md).

## Behaviour

Applied in this order, each step putting its directory first on PATH, so the project's own binaries end up first:

- Deno: a `deno.json`, `deno.jsonc` or `deno.lock` and an existing `$DENO_INSTALL/bin` (default `~/.deno/bin`).
- Bun: a `bun.lock`, `bun.lockb` or `bunfig.toml` and an existing `$BUN_INSTALL/bin` (default `~/.bun/bin`).
- Node: a `package.json` with a `volta` object and an existing `$VOLTA_HOME/bin` (default `~/.volta/bin`) puts Volta's
  shims on PATH and sets `VOLTA_HOME`; otherwise a `.nvmrc` or `.node-version` selects the highest installed nvm
  (`$NVM_DIR/versions/node`) or fnm (`$FNM_DIR/node-versions`) version. A number is a version prefix; for nvm the
  aliases work too: `node` and `stable` (the newest installed), `lts/*` (the newest LTS line), `lts/<name>`, `default`
  and any other alias file under `$NVM_DIR/alias`, followed through aliases of aliases. A selected nvm version also
  sets `NVM_BIN`. An unknown alias or a version that is not installed selects nothing. nvm itself is a shell function,
  so nothing is run: the alias files and version directories are read.
- `node_modules/.bin` (npm, pnpm, yarn's node-modules linker and bun). Yarn Plug'n'Play has no directory to add.

## Trust in a new worktree

No tool runs, so there is no trust to carry over.

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
