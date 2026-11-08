---
id: module-plugin-lsp-intellij
type: module-design
status: draft
title: Java / Kotlin (IntelliJ) — a language server as a builtin plugin
parent: group-plugins-language-servers
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# Java / Kotlin (IntelliJ) language server plugin

The plugin id is `lsp-intellij`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`intellij` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: IntelliJ Java and Kotlin · JetBrains · requires a JetBrains license (a free 30-day trial per build, then an IntelliJ IDEA Ultimate subscription).

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

The plugin owns the pinned distribution (SHA-256 per platform, JetBrains hosts only, bounded and traversal-safe
extraction), its terms review (the EULA bytes and a fingerprint of the whole installation) and the acknowledged
launcher (`--stdio --system-path <session directory> --eula <hash prefix>`, launcher debugging variables removed).
It replaces `java` and `kotlin` when chosen, accepts an installation directory, project JDK and build importer,
refuses overrides and its protocol arguments in configured ones, reads `jar:`/`jrt:` sources through the public
`decompile` command, and treats every server exit alike (the released build is licensed through the protocol, not an exit code). A build directory marked `.expired` by an older SharpRail carries its own build number, so the marker never applies to the current build, which installs beside it. The Kotlin plugin keeps its own copy
of the shared JVM initialization.


The build importer is the server's own: given `pom.xml` it runs the project's Maven Wrapper (`mvnw`, logged as
`Using Maven: <checkout>/mvnw`), and given a Gradle build it uses the Gradle wrapper's pinned distribution through the
Tooling API, so a project with a wrapper needs no `mvn` or `gradle` on PATH; one without a wrapper needs them there.

## License and consent

The server licenses itself over `jetbrains/licensing/*` requests and the `jetbrains/licensing/state` push (the client
advertises `experimental.licensingUi`, which makes the server push whole states and stop its own warnings; it also accepts
`window/showDocument` for the sign-in page). A build has a free 30-day trial, then needs an IntelliJ IDEA Ultimate
subscription, an activation code or a license server. The license store belongs to the operating-system user and only the
server writes it; SharpRail never reads or writes it. The plugin tracks the last state per session, publishes a projection
(`kind`: `licensed`, `session-expired`, `grace`, `parked`, `unlicensed` or `authenticating`, with the message, source, days left,
valid-through date, grace deadline, account, license server, subscriptions, a notice and the sign-in page) and, while
`parked`, `unlicensed` or `authenticating`, holds language answers with "No active license. Activate a license to use Java and
Kotlin features. Open Settings, Code Intelligence, Java / Kotlin (IntelliJ) to activate a license." A server that never pushes
a state is not held. The notice reads "The trial ends in N days" at 7 days left or fewer (tomorrow, today), and the grace
and parked messages as JetBrains words them. A server without any license is offered `discovery/autoActivate` once.

Actions: `refresh` (`state/get`), `trial`, `signIn` (`jba/login`; the server waits up to five minutes in the browser, so the
request waits six; cancelling it signs out so the server stops waiting; one valid subscription is used at once, several are
offered through `useSubscription`), `cancelSignIn`, `signOut`, `code` (`activationCode/use`, whitespace removed),
`serverAdd` (http/https only), `serverReconnect`, `serverRemove` and `removeLicense` (by license source: unuse subscriptions,
disable the activation code, or remove the license server). A license set by `INTELLIJ_SERVER_LICENSE`,
`INTELLIJ_SERVER_LICENSE_FILE` or `JETBRAINS_LICENSE_SERVER` overrides the store, so activation actions refuse and say so.
The Code Intelligence workspace settings show the state, trial days, the notice and the actions, and open the sign-in page
in the browser of the machine running the UI.

First-start consent: the terms review asks the region (`europe`, `africa`, `americas`, `apac`, `china`, `middle_east`,
`oceania`) and data sharing (`full`, `anonymous`, `none`), unanswered until chosen. The launch passes them as
`INTELLIJ_REGION` and `INTELLIJ_DATA_SHARING` next to `--eula <hash prefix>`, and refuses to start without recorded answers.

## Superseded builds

When a managed build installs and passes its checksum, older managed builds of the same platform beside it, and their
`.expired` markers left by earlier SharpRail versions, are removed in the background. A user-selected installation
directory, the installed build, other platforms and any build a session of this process still runs from (known by its
session directory) are kept; a deletion that fails is ignored and retried at the next install. A session of another
SharpRail process on the same machine is not known, so its build can be removed.

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
