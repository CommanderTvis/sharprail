---
id: module-plugin-lsp-intellij
type: module-design
status: draft
title: Java / Kotlin (IntelliJ) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# Java / Kotlin (IntelliJ) language server plugin

The plugin id is `lsp-intellij`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`intellij` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: IntelliJ Java and Kotlin · JetBrains · optional evaluation provider. The Code Intelligence settings show, in plain short sentences, "Stops working on <date>" (warning colour within 14 days; "Stopped working on <date>" once passed). The date is the pinned build's release day (product-info.json `majorVersionReleaseDate`, 2026-09-30) plus the 30-day EAP term of its agreement: 2026-10-30. It is derived from the pinned build, not read from a user-selected installation, and JetBrains may end the build sooner.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

The plugin owns the pinned distribution (SHA-256 per platform, JetBrains hosts only, bounded and traversal-safe
extraction), its terms review (the EULA bytes and a fingerprint of the whole installation) and the acknowledged
launcher (`--stdio --system-path <session directory> --eula <hash prefix>`, launcher debugging variables removed).
It replaces `java` and `kotlin` when chosen, accepts an installation directory, project JDK and build importer,
refuses overrides and its protocol arguments in configured ones, reads `jar:`/`jrt:` sources through the public
`decompile` command, and treats exit 7 as JetBrains refusing the build: a managed installation is then removed and
marked so it is not downloaded again; a user-selected one is never deleted. The Kotlin plugin keeps its own copy
of the shared JVM initialization.


The build importer is the server's own: given `pom.xml` it runs the project's Maven Wrapper (`mvnw`, logged as
`Using Maven: <checkout>/mvnw`), and given a Gradle build it uses the Gradle wrapper's pinned distribution through the
Tooling API, so a project with a wrapper needs no `mvn` or `gradle` on PATH; one without a wrapper needs them there.

## Primed index in new workspaces

Each launch passes `indexDir = <session directory>/index` (unless the workspace's initialization options name one), so
the index (a RocksDB database plus `workspace-model-v2.cache`) lives in the launch's own directory and is removed with
it. Without `indexDir` the server puts it under `<system path>/workspaces/<32-hex key of the workspace>/index/intellij-server`;
the shared `system/index` holds nothing (4 KiB), and no shared-index plugin ships, so a copy without the per-workspace
directory gains nothing. The server stores project paths relative to its workspace folders, so an index copied from
another checkout of the same project opens as is: the log reads `Workspace model restored from cache`, indexing takes
a fraction of a second, and workspace symbols resolve to the new checkout's files.

The plugin's `PrimeWorkspaceAsync` (`IntelliJIndexPrimer`) takes the newest session index in the source checkout's
state directory, which exists while that checkout's server runs, and copies it (APFS clones) into
`<target state>/primed-index` through a staging directory. It skips an index with any file written in the last 2 s and
discards the copy when the source's file list, sizes or times changed meanwhile: the server does not recover from a
RocksDB copy missing a file (it fails to open it), so only a copy of an idle, unchanged index is kept. It never
replaces an existing primed index. The workspace's first launch moves the primed index into its session directory as
its `indexDir`. Check mode `--workspace-clone` covers the copy, the idle and replace rules, and the claim.

Measured once each (Apple silicon, build 263.6379.0, Temurin 21, wrapper caches and `~/.m2` warm, time from launch to
`intellij/ready-for-test`, a fresh `--system-path` per run) on a Maven project (guava, jackson-databind, commons-lang3;
Maven Wrapper 3.3.2, Maven 3.9.10) and a Gradle project (Kotlin 2.1.21, guava, kotlinx-coroutines,
jackson-module-kotlin; Gradle wrapper 8.14.3), each a Git repository with a second worktree:

| Run | Maven | Gradle |
| --- | --- | --- |
| (a) main checkout, cold | 38.7 s | 23.7 s |
| (b) worktree, cold | 23.3 s | 22.8 s |
| (c) worktree, system directory copied without `workspaces/` | 19.4 s | 50.5 s |
| (d) worktree, main's `workspaces/<key>` copied under the worktree's key | 5.4 s | 3.7 s |
| worktree, main's index passed as `indexDir` | 5.2 s | — |

Cold indexing took 14–43 s of each cold run and 0.2–0.3 s of each primed one; the per-workspace index was 397 MB
(Maven) and 369 MB (Gradle). (c) is a cold start; its spread is run-to-run noise.
