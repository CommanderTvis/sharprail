---
id: module-plugin-env-jvm
type: module-design
status: active
title: JVM Environment — the workspace's chosen JDK in every shell, as a builtin plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins]
tags: [plugins, terminals]
---

# JVM Environment — the workspace's chosen JDK in every shell, as a builtin plugin

## Responsibility

Gradle, Maven, language servers and agents build with whichever `JAVA_HOME` their shell has, so a worktree whose
project pins another JDK builds with the wrong one. This plugin (`env-jvm`, order 500) is one step of the host's
workspace environment pipeline (Plugin API H22): it points `JAVA_HOME` and PATH at the JDK the workspace chooses. It is
builtin and **enabled by default**, independent of the other environment plugins, has no UI half and no setting
(Settings › Plugins is the off switch), and runs nothing when the workspace chooses nothing.

Two projects, `Contract` (`SharpRail.Plugins.EnvJvm`) and `Host` (`SharpRail.Plugins.EnvJvm.Host`). The pipeline is
the host's: [Plugins.SPEC.md](../SharpRail.Host.Core/Plugins.SPEC.md).

## Behaviour

- `.sdkmanrc`: its `java=<identifier>` line names the sdkman candidate `candidates/java/<identifier>` under `$SDKMAN_DIR`,
  else `~/.sdkman`, else Homebrew's sdkman (`<prefix>/opt/sdkman-cli/libexec` for `/opt/homebrew`, `/usr/local` and
  Linuxbrew), because an app started from the dock has no `SDKMAN_DIR`.
- Otherwise `.java-version`: its first entry names a jenv version (`$JENV_ROOT/versions/<name>`, default `~/.jenv`),
  whose link is followed to the JDK's home; a name jenv does not have selects the highest sdkman candidate it is a
  prefix of (`21` selects `21.0.5-tem` over `21.0.2-tem`).
- A candidate counts only when its `bin/java` exists. The choice sets `JAVA_HOME` and puts its `bin` first on PATH,
  replacing a `JAVA_HOME` the host inherited but not one an earlier step (direnv, mise) set.
- A choice that is not installed selects nothing and the log says so, with `sdk env install` for sdkman.
- Nothing is installed or downloaded. JDKs managed through `.tool-versions` or `mise.toml` arrive through the mise plugin.

## Limits

- Language servers the host starts itself do not get this environment yet; terminals (and the agents in them) do, and
  an IntelliJ language server's own `projectJdk` setting is separate.
- Only sdkman and jenv are read; other JDK locations (`/Library/Java/JavaVirtualMachines`, `~/.jdks`) are reached only
  through a jenv link.

## Verification

`--environment` (gate case `environment`): a fake home with sdkman candidates and a jenv link covers `.sdkmanrc`, a jenv
name, a prefix match, an uninstalled choice, an earlier step's `JAVA_HOME` kept and a host-inherited one replaced.
