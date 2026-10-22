---
id: module-host-language
type: module-design
status: draft
title: Workspace language services
parent: module-host-core
---

# Workspace language services

## Providers are plugins

Every language server comes from a plugin (Plugin API H21): one builtin plugin per server,
`src/SharpRail.Plugins.Lsp.<Name>` with id `lsp-<server id>`, all enabled by default. The catalog is the
servers that active plugins provide, in roster order, which decides file ownership (Java and Kotlin before
IntelliJ). Disabling a plugin removes its server from listings and agent instructions and stops its sessions;
settings, `.sharprail/language.toml` entries and project files naming it stay valid. Until the plugin runtime's
first reconcile settles, the host serves the builtin servers that are about to activate, so startup requests are
not refused; a host without a plugin runtime (checks, a bare `HostStateStore`) serves the builtin servers.

The catalog is one immutable snapshot shared by every caller (the Code Intelligence pane, dependency probes, the
workspace language census, Settings, file routing and the lsp tools): the servers in roster order with lookups by
id, extension and file name that keep the first server in roster order, and the servers that can replace others.
The plugin runtime hands out the same spec list until an active plugin's language server table changes (a roster,
enablement or activation change), and the snapshot is rebuilt only when that list changes; queries allocate
nothing. Host tool paths are parsed again only when their settings text changes; dependency probes keep their
2-second reuse. `--lsp-catalog` checks the rebuild counter, that indexed detection matches the roster-order scan,
and an allocation budget per query. Measured on 2026-10-22 with three 1,800-file workspaces, one `servers` request
per workspace (one pane refresh) allocated 4.1 MiB before the snapshot and 0.63 MiB after, with gen0 collections
per refresh round falling from 1.53 to 0.22; the remainder is the file scan and row building, not the catalog.

Each provider owns its identity, pinned version and pins (archives with SHA-256, NuGet packages with SHA-512,
embedded npm or Go lock files), owned files, aliases, configuration controls, project manifests, project kinds
and scope text, language ids, initialization and settings defaults, and its install and launch steps. The host
keeps what is shared: sessions and the protocol, the installation lock and `.ready` marker, verified archive and
npm installs, overrides and the PATH policy. No host or UI code names a server: a provider's protocol
adaptations, typed options, terms, expiry and presentation are all spec hooks.

A server that `Replaces` others (IntelliJ for `java` and `kotlin`) serves their files once chosen, host-wide by
the provider-choice setting (`standard` or the replacing server's id) or, without one, per workspace by its
`useForJavaAndKotlin` option; while it is not chosen it does not start, and while it is the replaced servers stay
stopped and their drafts move to it. A server with `TermsAsync` is installed and launched by its provider: the
host never searches PATH for it, records acceptance under `agreements/<server>/<artifact>.json`, and starts it only
with the exact accepted terms; a vendor refusal (`ExpiredExitCode`) invalidates results, runs `OnExpired` and is
not restarted automatically. Listings carry each server's presentation (name, icon, option groups, replaced
servers, terms) so the user interface shows what the plugin declares.

The host owns language-server processes and installation storage. Editors and terminal agents
query the same session for a workspace and server. Every call explicitly identifies its workspace;
neither a client's current project nor an agent's shell directory can silently retarget it.
Processes initialize with that directory as cwd, root URI and workspace folder. Local adapters
move all work off the dispatcher; remote clients use the same service through WebSocket RPC.

## Analysis and navigation

Support definitions, type definitions, implementations, references, document symbols and workspace
symbols according to advertised server capabilities. File locations outside the checkout grant
read-only access to those exact dependency files; they do not grant arbitrary filesystem access.
Non-file locations retain their exact URI. IntelliJ jar:/jrt: source is read through its public
workspace/executeCommand decompile protocol when advertised. Other schemes/providers remain
visible with source-unavailable explanations. Read access requires the exact URI returned by the
current workspace/provider session and an explicit source server; restart invalidates the grant.
Virtual sources reject edits and arbitrary URI/command execution. No filesystem extraction occurs.

Document synchronization accepts full editor drafts and emits the server's full or incremental
change form. Each editor has a stable identity and increasing version. Different dirty drafts for
one URI are rejected; branches never share mutable document state. Disk requests use an active
dirty draft when present and say so. Closing it returns analysis to disk. Older requests and
versioned diagnostic reports cannot replace newer document results.

Diagnostics expose locations, severity, source, freshness and coverage. Pull reports refer to the
requested version. Push reports without versions remain explicitly unversioned. An empty pending,
partial or unversioned result never asserts a clean workspace. Server detection discovers one
representative file per server, skips dependency/build directories and symbolic directories, and
visits at most 5,000 directories. Only servers advertising workspace diagnostics can promise that
coverage; other servers expose their reported/open-document diagnostics.

Semantic tokens use UTF-16 coordinates and are decoded to complete token spans. The editor replaces TextMate colors with
supported semantic roles when tokens arrive; missing capabilities, startup and failures retain syntax
highlighting. Server work and parsing stay off the UI thread. Edits, workspace changes, cancellation
and disposal reject stale results. Inspection does not apply code actions or edit source.

## Provisioning and index reuse

The baseline is [RedMonk January 2025's twenty languages](https://redmonk.com/sogrady/2025/06/18/language-rankings-1-25/).
Install on first use, reuse installations across workspaces, and expose installing, initializing,
ready, stopped and error states. Stop cancels provisioning or the process; Retry explicitly restarts
it. Installations are serialized within a host and by a filesystem lease across host processes.
Partial downloads are removed and successful installs have a completion marker.

| Languages | Default server | Provisioning prerequisite |
| --- | --- | --- |
| JavaScript, TypeScript | typescript-language-server plus TypeScript | Node/npm |
| Python | Pyright | Node/npm; project Python environment for imports |
| PHP | Intelephense | Node/npm; features depend on its license |
| CSS | VS Code CSS language server | Node/npm |
| Shell | bash-language-server | Node/npm |
| C# | Roslyn language server | .NET SDK |
| C, C++, Objective-C | clangd | Release archive; project compile commands improve analysis |
| Java | Eclipse JDT LS | JDK 21+ through JAVA_HOME |
| Kotlin | JetBrains Kotlin LSP | Installed server or platform release archive |
| Ruby | Ruby LSP | Ruby/gem |
| Go | gopls | Go |
| Rust | rust-analyzer | Release archive; project Rust toolchain |
| Scala | Metals | Coursier and JDK |
| R | languageserver | R/Rscript |
| PowerShell | PowerShell Editor Services | PowerShell |
| Swift | SourceKit-LSP | Swift/Xcode SDK includes the server |
| Dart | Dart language server | Dart/Flutter SDK includes the server |

The catalog pins managed server versions. A PATH executable is used only by per-server opt-in
(see below); Swift and Dart follow the selected SDK. The app does not install global SDKs or change
project package manifests. Missing prerequisites and unsupported platform archives remain
actionable errors.

Storage is `<stateDir>/language`: `servers/<id>/<version>-<hash>` for installations, `cache` for reusable
data, and `workspaces/<root-hash>` for exclusive server state. gopls uses its supported `GOPLSCACHE`
for content-addressed semantic-index reuse. Dart can use an explicitly supplied shared analysis cache.
clangd only shares semantic data through an explicitly mounted external index; its ordinary background
index is workspace-local. Kotlin's native RocksDB index and Metals' `SqlSharedIndices` may reuse
immutable imported/library data, but require their real Gradle/Maven or BSP prerequisites before a
probe can assert that behavior. Metals' `COURSIER_CACHE` is dependency artifacts, never semantic-index
evidence. JDT's `-data`, IntelliJ's system path and the remaining provider state are workspace or
process scoped. LSP itself supplies no portable index-sharing API, and sharing mutable databases or
rewriting branch paths would give incorrect results. The opt-in integration inventory classifies every
catalog provider and records the exact native probes; package downloads and compiler artifacts never
count as semantic reuse.

### Managed by default, PATH by opt-in

The managed installation is the default for every server that has one. A PATH executable is
unpinned and unverified, and a workspace can influence what PATH finds (an activated virtualenv,
`node_modules/.bin`, relative entries, repository-planted binaries), so PATH is never a default
and never a silent fallback. Running one is a per-server opt-in in host state
(`LanguageServerOptions.AllowPath`, Settings > Code Intelligence "Use the executable on PATH instead
of the managed installation"), shown with an "unverified binary" warning. There is no global switch.
Swift and Dart have no managed installation (the SDK ships the server), so they report
`needs an installation: ... allow PATH for this server` until the user opts in. R, PowerShell and
IntelliJ are not PATH-capable: their PATH executable is only a runtime or has its own distribution.
`LanguageServerOptions` replaced the former `Managed` flag without migration; a stored `managed`
property is ignored.

Whenever PATH is used (opt-in, or to find the tools installers need such as `npm`, `go`, `gem`,
`dotnet`) the search ignores empty and relative entries and never uses the workspace as working
directory. A candidate must be a regular executable file and, with every symbolic link resolved, must
not lie inside the workspace, its Git root (unless that root contains the home directory), its
project or that project's worktrees. The same check refuses an explicit executable override from host
state that resolves inside the workspace; any other explicit override is the user's own choice and is
used as given. The `resolution` operation (workspace plus server id; `Configuration` carries JSON with
`state`, `source` of `managed`, `path` or `override`, `path`, `version`, `warning`, pinning text) reports
what a server would run without starting or installing anything; for PATH and override executables
it asks `--version` with a three-second limit. Settings reads it off the UI thread.

Project files cannot choose executables. `.sharprail/language.toml` copies only the portable keys
(`autoStart`, `arguments`, `buildTool`, `compilationDatabasePath`, `compilerFlags`, `includePaths`,
`initializationOptions`, `settings`); host-specific keys and anything named like a command,
executable, path, `allowPath` or `managed` is ignored with a diagnostic. `arguments` from any source
replace the default arguments of a server that is the program itself, but are appended to the
launcher arguments of runtime-hosted servers (node packages, JDT, PowerShell, R) so that they can
never name the script, jar or program to run.

### System dependencies

A provider declares the host tools its managed launch or installation needs (`LanguageServerSpec.Dependencies`,
each with a name, a one-line install hint, whether only the installation needs it, and an optional home variable):

| Server | Tools |
| --- | --- |
| typescript, css, python, php, shell | `node` (launch), `npm` (install only) |
| go | `go` (install only) |
| powershell | `pwsh` |
| r | `Rscript` |
| ruby | `gem` (install only) |
| scala | `cs` (install only) |
| csharp | `dotnet`, `DOTNET_ROOT` first |
| java | `java`, `JAVA_HOME` first |

A tool resolves from the user's host-wide path for it (host setting `LanguageTools`, JSON of executable to absolute
path, changed through `language-tool:<executable>`; empty clears), then its home variable (`home/bin`, `home`), then the
hardened PATH; the workspace rule above applies to all three. `RequireTool`, `FindTool`, `FindDotnet` and the host's npm
and node lookups all use it, so a path is honoured by the probe and the launch alike. The `servers` operation probes
every declared tool on the host (answers reused for two seconds) and returns them on each row: a tool is required
only for the managed launch, and an install-only tool only until the managed installation exists. A start refuses
before installing while a required tool is missing, with `Needs <tool> (<name>). <hint> Or choose an existing <tool>
with Use path…`. SharpRail never downloads these tools or runs a package manager for them. `--lsp-dependencies`
covers the probe with a fake PATH, the refusal, a tool appearing on PATH, and setting, persisting, launching with
and clearing a path.

### Pinning

Every managed installation is pinned by exact version. How completely differs, and the Settings page
and `ManagedArtifacts.Describe` say exactly which:

| Providers | Acquisition | Pinning |
| --- | --- | --- |
| TypeScript, Python, PHP, CSS, Shell | `npm ci --ignore-scripts` from the plugin's embedded `package-lock.json` (`Locks/`) | Version and sha512 integrity of the whole dependency graph, registry URLs only |
| clangd, JDT, Kotlin, PowerShell, rust-analyzer | Release archive from the plugin's `Artifacts` (JDT from `jdtls/milestones/<version>`, not the rolling snapshots) | Version and SHA-256 per platform, source of the hash recorded beside it, verified before extraction |
| IntelliJ | The IntelliJ plugin's own distribution code (`TermsAsync` and `LaunchAsync`) | Version and SHA-256 per platform, plus acceptance of its terms recorded by the host |
| C# | The pointer package and the platform's runtime pack of `roslyn-language-server`, fetched by SharpRail from `api.nuget.org` and installed by `dotnet tool install --source <local feed>` with a private package cache | Version and SHA-512 (NuGet catalog `packageHash`) of every package, verified before dotnet reads it; no Linux musl pack is pinned |
| Go | `go install golang.org/x/tools/gopls` in a module made from the Go plugin's embedded `go.mod` and `go.sum`, `-mod=readonly`, `GOSUMDB=sum.golang.org`, `GOTOOLCHAIN=local`, and `GONOSUMDB`, `GONOSUMCHECK`, `GOPRIVATE`, `GOINSECURE`, `GONOPROXY`, `GOENV` and `GOWORK` neutralised | Version and `h1:` hashes of the whole module graph; the Go toolchain itself is the host's |
| Ruby, Scala, R | Package manager (`gem`, Coursier, CRAN) | Managed, partially pinned: exact version only. Ruby: RubyGems resolves ruby-lsp's dependencies at install and checks no stored hash (a frozen `Gemfile.lock` with checksums needs Bundler 2.6+ and Ruby 3.x to generate and verify). Scala: Coursier checks Maven Central's own published checksums but no jar hash is stored here (a lock would need `cs fetch` output pinned per platform). R: `remotes` is installed unpinned to reach `install_version`, and CRAN dependencies float. Not verified on the 2026-10-19 pass because the host had no Ruby 3 or R |
| Swift, Dart | Host SDK | Not managed |

Downloads are HTTPS only. Redirects are followed manually and each hop must stay on the artifact's
allow-listed hosts. The archive is hashed before anything is extracted, a missing or mismatching hash
fails closed with nothing installed and the partial download removed, and extraction refuses absolute
or `..` paths, backslashes, hard links and symbolic links that leave the installation, and bounds
size and entry count. Installation directories are `servers/<id>/<version>-<hash prefix>` (npm: the
lockfile's hash), so a bump never reuses a directory; installed archive files are made read-only. The
`--managed-artifacts` check lists every provider with its version and hash presence and fails when a
managed one lacks a version, a SHA-256 or an integrity hash, or when a NuGet or Go module pin is incomplete, or when a partially pinned provider is not
declared as such; fixtures prove the tamper, redirect, traversal and symbolic-link refusals without any
download.

Host administrators may set `<stateDir>/language/servers.json`, keyed by catalog id, with
`command` and an `arguments` array to override a launcher, `allowPath: true` to allow PATH for that
server. This file is host-owned, never read from a repository.
`SHARPRAIL_LSP_DISABLE=1` disables language startup, including in ordinary check fixtures.
There is no per-server allow/deny setting: every catalog server may start, and a server is in the catalog
only while its language server plugin (`lsp-<id>`) is enabled. On load, host state migrates the retired
per-server `disabled: true` once into that plugin disabled (an explicit plugin choice wins) and drops the
flag from the stored server options; a `disabled` key in `servers.json` is ignored.

### Server lifetime and start origin

A language server runs while its workspace uses it, within the limits below. It ends when the user
stops it, the limits stop it (it then restarts on its next request), its workspace is removed (exit
reason "workspace removed") or its project is closed ("its project was closed"), its configuration
changes and the user restarts it, the host shuts down or language services are turned off, or it
crashes (an unexpected exit record). Stopping a listener does not stop an embedded host's language
sessions.

### Server limits

Many workspaces each running Roslyn (1–3 GiB each) exhausted a 24 GiB machine, so memory is bounded
(`LanguageService.Limits.cs`, `LanguageMemory.cs`). The host setting `language-limits` (JSON
`{"idleMinutes","memoryBudgetMb","caps":{id:n,"*":n}}`, validated and normalized by the host) holds them;
every decision reads the current value, and a change triggers an enforcement pass.

- Memory budget, the primary limit, across all servers of every kind. Default (`memoryBudgetMb` 0): 30% of
  physical memory (or the container limit) clamped to 3–24 GiB — 7.2 GiB on 24 GiB, 4.8 GiB on 16 GiB,
  9.6 GiB on 32 GiB: room for about 8–12 Roslyn servers at the measured ~0.5–0.8 GiB settled footprint,
  far below the 40 GiB seen, while the floor still fits one large server and the ceiling stops a big
  machine handing everything to servers. Memory is sampled every 15 s on a timer thread: macOS physical
  footprint (`proc_pid_rusage`, what Activity Monitor shows; resident size undercounts compressed
  memory), resident set elsewhere. Child processes (Roslyn's build host) are not counted.
- Count cap per server kind, the secondary guard: `csharp` 4, `intellij`, `kotlin` and `scala` (JVM) 2,
  every other kind 6 (`*`).
- Idle timeout, default 10 minutes (0 never): a server no request used for that long stops.
- Memory pressure: when macOS reports a warning or critical pressure level
  (`kern.memorystatus_vm_pressure_level` ≥ 2), Linux has under 10% `MemAvailable`, or Windows passes the
  runtime's high-memory-load threshold, servers not used within the last 2 minutes stop.

A server is used by every request that reaches its entry (editor, pane, agent tools); listing servers does
not count. The limits never stop a server with a request in flight or with an editor buffer open (a
document with an owner; the host does not know which window has focus, so every editor protects). Over
budget, the enforcement pass stops the least recently used other servers until the total is within the
budget. Before a server starts, it is admitted only if its kind is under its cap and the total is under
the budget; otherwise the least recently used stoppable server (of the same kind for the cap) stops
first. When nothing can stop, the start waits: its entry reports `initializing` with "Waiting for a free
language server slot…", which the on-demand wait and `lsp_*` answers pass through unchanged, and it retries
when a server stops or goes idle (and at least every 5 s); the wait does not count against the 15 minute
startup deadline. A stop by the limits keeps drafts, writes an expected exit record whose reason names
the limit ("stopped by the language server limits: unused for 10 min", "...exceeded the 7372 MiB memory
budget", "...the system is short of memory"), and remembers no stop, so the next request starts the
server again.

The host-wide `running` operation lists every running server (workspace, state, busy/editor open/idle,
idle seconds, memory, started by) with a summary (budget, whether automatic, total, physical memory,
pressure, limits, default caps); `release` (workspace + server) stops one the same way, refusing a busy
one. Settings › Code Intelligence shows both (UI SPEC).

Process hygiene: every launch carries `SHARPRAIL_LANGUAGE_REGISTRY`, and its pid, start time and process
name are recorded in `language/processes/<host pid>-<host start ms>.pids` until it exits. On Unix the first
launch starts one `/bin/sh` watchdog per host that polls the host pid every 2 s and kills the recorded
servers once the host process is gone, however it ended. Each host start sweeps the directory off the
caller's thread: records of hosts that are no longer running are killed when the pid still has the
recorded name and a start time within 2 s (a reused pid is spared), then deleted; records of a running host
(another SharpRail on the same storage) are kept. Windows has no watchdog: servers end with their closed
stdin, and the next start sweeps.

Footprint: C# launches set `DOTNET_gcServer=0` and `DOTNET_GCConserveMemory=7` unless the user exported
them (Roslyn ships with server GC). Measured on SharpRail.slnx (74 projects, macOS arm64, physical
footprint after the project graph loaded, a document opened and a workspace symbol search, settled):
server GC 810–855 MiB; workstation GC 519–556 MiB; workstation GC with conserve-memory 514 MiB (about −38%; conserve-memory
adds little over workstation GC alone).
`DOTNET_GCHeapHardLimit` is not set: a hard limit turns a large solution into out-of-memory failures.
Roslyn's analyzer and compiler diagnostics already default to open files, so no scope setting changes;
references and diagnostics are unchanged (the freshness checks pass).

Each running server records who started it, as information only (it never changes how long the server
runs): `human` (the pane, Settings, an editor request), `config` (`autoStart` in a workspace's
settings or the project's `.sharprail/language.toml`, or the project's local autostart list),
`workspace creation` (`workspace_create` with `languageServers`) or `agent` (an `lsp_*` tool call that
started it on demand). It is `LanguageServerState.StartedBy`, `startedBy` in `lsp_status` operation
`servers`, and `LanguageExit.StartedBy` plus a "Started by" line in the exit details.

Project `autoStart` starts in every workspace of the project: the Default workspace when its project
is first opened, every workspace created later (also by `workspace_create`) and every agent launch.
Removing a workspace stops only its own servers.

### Agent tool calls

The `lsp_*` tools never depend on an agent starting servers first. `lsp_diagnostics` (files or
workspace scope), `lsp_navigate` and symbol and outline operations start the server that owns the
requested files when it is not running, all needed servers in parallel, and wait up to 60 seconds
(`LanguageLimits.OnDemandStartWait`); past that the file is `pending` or the result state is
`initializing` with "N s elapsed; retry or wait". A server the user stopped explicitly stays stopped
and disabled language services say so (the file is `skipped` with the exact `lsp_status retry` call or the
Settings hint); terms that need the user keep blocking with their actionable message. Every `lsp_*`
call has a 90 second deadline (`LanguageLimits.ToolDeadline`); past it the call is cancelled and
returns state `initializing` with the servers still starting under `starting`, never a hang.

`lsp_navigate` hover, definition, type definition, implementation and references with a symbol
`query` and no `path` resolve the name through workspace symbols (the server is sent the last
segment; a simple name, `Type/Member` or `Namespace.Type.Member` all work) and run at the one
matching declaration, naming it in `detail`. Several same-named declarations are narrowed by a
qualified query through each candidate file's outline (at most 20 files); otherwise the result is
`ambiguous` with the candidates as locations and a working path/line/column call in `detail`.
Candidates that are all declarations of one type are one symbol, not a choice: the partial parts of a
type (confirmed by the server's definition of the first listing them all) and that type's own
constructors in the same project (server container names) resolve to the type. Types in different
projects, or anything else sharing the name, stay ambiguous.
No match is `notFound` naming the `workspaceSymbols` call. Other operations with a query and no
path search workspace symbols and say so in `detail`; that search is `ready` when any server
answered, with servers missing for other languages listed in `detail` rather than failing the
call (`CombinedState`). An open, clean document whose file changed on disk is resynchronized
before the next request, so dependents do not report phantom missing members. A call that needs a path and has none is rejected with the exact working call as JSON. A
positional call that finds nothing says positions are zero-based while preview prefixes are
one-based, and names the query form. The tool description gives a call shape per operation. A response carries a one-line `hint` when a server
for the requested language exists but is stopped, disabled or unsupported.

Usage is counted per workspace and server (calls, successes, failures, last used; a failed or
cancelled call is a failure; no caller identity is kept) in memory and in
`<state>/language/usage.json` (atomic replace, at most 128 pairs, least recently used dropped, removed
with the workspace). It is `LanguageServerState.Usage`, `usage` in `lsp_status` operation `servers`, the
"Agent calls: N (last: 12 min ago)" line of the pane row, and an "Agent calls" line in the exit details.

`workspace_create` takes `languageServers` (ids, language names such as `csharp` or `ts`, or `"auto"`
for every server whose files are present). The servers start in the background, the tool returns at
once with each requested server and its state (`starting`, `ready`, `needs-acceptance`, `failed`
with the reason), and `lsp_*` calls then wait on them with the bounded wait.

### Loading project graphs

A server that loads a project graph (Roslyn) answers from whatever part of it is loaded, so the host
holds back answers that could be silently incomplete. The graph counts as loading during startup until
`ProjectLoadedNotification`, while any `$/progress` (such as Roslyn's own restore) or tracked restore
runs, for `SettleGrace` (2 s) after either ends (the restore's outputs reach the watcher and start a
reload), and from sending a project-input change (a created or deleted watched file, or a file the
provider's `IsProjectInput` names, such as `project.assets.json` or a `.csproj`) until the server logs
its `ProjectReloadedMessage`, at most `ReloadWait` (30 s). While loading:

- references, implementation and workspace-symbol searches (and so every by-name lookup) return
  `initializing` with "still loading the project graph ... call again", never a partial list;
- an agent's file diagnostics (`fileDiagnostics`, used by `lsp_diagnostics`) report the file `pending`
  with no diagnostics, since a type merely not loaded yet reads as "does not exist";
- outlines (`symbols`, `symbolBody`) and `source` answer at once: they are syntax;
- definition, type definition and hover answer when they found something (a found declaration is
  right); an empty answer is `initializing` instead of "nothing here".

Agent tool calls wait for the graph within the same bounded on-demand wait (`OnDemandWait`, 60 s,
counted from the start of the call and inside the 90 s deadline) before answering `initializing`. The
editor's `inspect` and the pane's workspace problems are unchanged.

### Watched files

Each session subscribes to the host's shared workspace watcher (`WorkspaceWatches`, the one behind
`WatchFilesAsync`) before `initialize`, so a file created by an agent, Git or the editor while the
graph loads is still reported. Changes are debounced (250 ms, a steady stream at least every second)
and sent as `workspace/didChangeWatchedFiles` once the server serves, and before any request that
finds changes pending. The client declares dynamic registration with relative-pattern support and
honours the server's `client/registerCapability` watchers (string or relative glob patterns with `*`,
`**`, `?`, `{a,b}`, `[...]`, and the create/change/delete kind mask), including restore outputs
under `obj` and reference assemblies. A server that registered none gets changes to files with its
extensions, its manifests and project inputs, outside build and tool directories. A file born after
the session's watch began is reported once as created (the server does not know it), a missing one
as deleted, anything else as changed. Creating or deleting a file with one of the provider's
`ReloadingExtensions` (Avalonia `.axaml` for C#), which the server watches no glob for, is reported
as a change of the nearest enclosing project manifest so the project reloads. Lost events (overflow,
restarted watcher) report every project manifest as changed. Open, clean documents changed on disk
are resynchronized after the notification.

### Process exit records

Every server process that ends leaves a `LanguageExit` in its `servers` row, so no death is silent.
The connection watches the process itself (not stdout EOF) and, once it has exited and stderr is
drained (two seconds at most), records the time, uptime, exit code, the signal that code names
(Unix exit codes above 128 are 128 plus the signal; Windows fault statuses are named), the last 200
stderr lines, the last 60 JSON-RPC method names with times (never payloads), the requests still
unanswered, the last sampled resident memory (once a minute by the memory sampling timer, never a new
thread) and the memory the host process may use. Reasons separate facts from suspicion: exit 137
reads "killed by SIGKILL ... often the OOM killer or jetsam, but also a manual kill -9; the cause is not
confirmed". A server may also exit with such a number by itself.

Every stop SharpRail causes names its reason before the kill (a stop or restart request, a Java/Kotlin
provider switch, suspension, workspace removal ("workspace removed"), host shutdown, a failed start, a failed draft restore, or a stdin write that failed or was cancelled and
left the JSON-RPC stream unusable). Those records are `Unexpected = false` except failures. Anything
else, including exit 0 nobody asked for, is `Unexpected = true`. SharpRail sends no `shutdown`/`exit`
handshake; it kills the process tree.

The newest record per workspace and server is stored in `<state>/language/exits.json` (atomic
replace, at most 64 records, details capped near 48 KiB each, dropped with the workspace) and read
by `servers` after a pane reopens or the host restarts. `servers` shows it as the row detail
("Stopped unexpectedly: ..." for an exited session, an unrequested record on an idle row, or "Last
run: ..." for requested stops) and in `LanguageServerState.Exit`; `lsp_status` operation `servers`
returns it as `lastExit` (time, reason, unexpected, requestedBy, exitCode, signal, uptimeSeconds,
lastMemoryBytes, details). The record travels in `LanguageServerDto.Exit`. An unexpected death is
also published once as `HostState.LanguageDeaths` (never persisted, `workspace NUL server` to
`time|reason`); each client window raises one error toast per new value. Requested stops raise none.
The LSP pane shows red detail text with Details (inline copyable report), Copy details and View logs.
The log keeps an `exit:` line with the same reason. The server's own log files are not read: SharpRail
passes no log directory, and Roslyn's `window/logMessage` output already reaches the logs.

## Agents

Terminal-authenticated MCP exposes `lsp_navigate`, `lsp_diagnostics` and `lsp_status`. An omitted
workspace targets the terminal's original workspace; an explicit override must belong to the same
Git project. Subagents inheriting the connection must pass their own worktree path. Results carry
freshness and coverage, including an explicit notice when they use an editor's dirty buffer.
Codex and Claude launch instructions direct agents to these tools and require passing the workspace
and instructions to delegated agents. Checks after edits supplement the project's compiler/tests.

## Management pane

The workspace LSP tool lists all supported servers without starting them. Listing is cancellable,
bounded by the same discovery limit, and reports stopped and failed sessions. Start/Retry and Stop
target a catalog server in the selected workspace; polling never restarts a stopped server.

The inventory includes every supported server, with detected languages first. A blank detection path
means no matching file was found. Lifecycle requests can select a catalog server id without a source
file; no dummy file is created. Workspace diagnostics query only initialized sessions and never start
or provision a server. With no running servers they report unavailable coverage, not a clean workspace.

The agent instructions (Plugin API H20) read the same bounded discovery walk as a census
(`ProjectLanguages.cs`): files per server, a chosen replacement standing in for the servers it replaces,
ranked by count, and an example type and member from the primary language's most shared file stem
(regex over at most 256 KiB of one file; no server is started). Results are cached per workspace in
memory and in `<language storage>/profiles/`; a lookup returns the cached profile at once and starts a
background census at most once a minute; only a change of the top servers or the example replaces it.

## Explicit diagnostic scans

Agents can select `paths` containing every edited file, or `scope: "workspace"` to enumerate
project source files, including unopened files. MCP diagnostics with no path defaults to the latter;
`path` is a one-file selection. Only already-running sessions are used. The pane's passive Problems
request retains its existing behavior and never starts servers.

Project scans use tracked and non-ignored untracked files in Git, or visible files outside Git,
and select recognized language files. Explicit selections can include ignored files. Validate
containment before dispatching an explicit selection; workspace symlinks are reported skipped.
Each file is synchronized from disk or an active dirty editor buffer and queried separately,
without requesting semantic tokens or treating other open-file reports as its results. Push reports
have a bounded one-second wait; unversioned/pending results remain incomplete. Stop beginning new
file analysis after two minutes and list remaining files as pending for explicit retry. Cancellation
flows to enumeration, synchronization and server requests.

Results expose per-file checked/pending/skipped/error state, freshness and explanation. Missing,
unsupported and non-running-server files never count as checked. Diagnostics are restricted to the
selected files; only a nonempty selection with current reports for every file is current. This
coverage means LSP document analysis, not compiler/build validation or whole-program completeness.

## Host settings

Settings > LSP writes per-server launcher choices through host state. A server-specific change
merges with other servers’ settings under the state gate. UI-configured choices override that
server’s legacy `language/servers.json` entry; untouched servers retain the file-based fallback.
Enablement, PATH opt-in, command and arguments apply on next start/retry, without
restarting active servers. Arguments require an executable override. A null argument list uses server defaults; an empty list passes none.
The host-wide `catalog` request exposes supported ids, versions and storage without a workspace
or process startup, including when language execution is disabled.

The host-wide LSP enablement setting defaults to on and persists across restarts and transports.
Disabling it prevents new starts and stops all active sessions before the setting operation returns.
Dirty drafts survive suspension; closing a document while disabled removes its saved draft.
Re-enabling permits lazy startup with the existing per-server configuration.

When PATH is allowed for TypeScript, selection checks for a compiler in the workspace ancestry or beside the
resolved PATH launcher. A standalone language-server executable falls back to the managed pair
of TypeScript and its language server. Explicit executable overrides remain authoritative unless they resolve inside the workspace.

The workspace-free catalog identifies the default implementation/provider in each server detail. Agent MCP exposes catalog and workspace inventories plus explicit server-id startup/status, allowing multiple language sessions concurrently without UI interaction. Mixed-language file scans route each file separately and never implicitly install servers.

## Agent retrieval

Navigation returns a stable, paged list (offset plus limit, maximum 200) with total count
and explicit truncation. Definitions, types, implementations and references include bounded
source excerpts. Dirty editor drafts supply source when present; unavailable source is explicit.
Hover exposes inferred types, signatures and documentation when the server supports it.
Document outlines retain qualified parent/child names and full symbol ranges. `symbolBody`
selects an exact qualified name, otherwise a name whose segments end the qualified outline name like
the other outline lookups (`Member`, `Type/Member`, parameter lists and ` : type` ignored unless the
query spells them), or the smallest symbol containing a UTF-16 position; ambiguous names return candidates. Bodies are capped at 12,000 characters with an explicit truncation flag
and source range. Location-only servers do not pretend their symbol locations are body ranges.
Navigation also accepts outline names for hover, definition, type definition, implementation and
references: an exact qualified name wins, otherwise a name whose `/`- or `.`-separated segments end
the qualified outline name (parameter lists and ` : type` suffixes ignored unless the query spells
them). Not found names the `symbols` outline call; ambiguous lists the qualified candidates. Resolve documentSymbol selectionRange on every request after buffer
synchronization. Missing/ambiguous names or absent selection ranges never fall back to guessed
positions. Existing cursor navigation remains available for overload selection.
The source operation reads file/dependency text in 12,000-character pages (offset and nextOffset
count UTF-16 characters); documents over 1 Mi characters are rejected. Dependency source is fetched
fresh per request and shared within a result page; no stale decompiler cache survives project changes.
Workspace-symbol searches can span languages; narrow to a file for single-server pagination.

MCP tools advertise separate diagnostic, navigation and lifecycle schemas/descriptions rather than
repeating every configuration field. Older explicit operation dispatch remains compatible.
Status/start/restart expose freshness and project coverage. Repeated requests use
the same process; agents should poll readiness rather than repeatedly restart.
MCP replies omit irrelevant empty collections. Navigation includes paging metadata; diagnostics
include analysis freshness, aggregate coverage counts and independently paged diagnostic/file
lists. The shared result-item character budget is 28,000, separate from envelope metadata;
text fields and symbol bodies are bounded. Output truncation never changes analysis freshness
or silently hides that more results exist. Diagnostic scans remain read-only, do not start
servers, and prefer explicit edited-file selections over repeated workspace scans.

Workspace server configuration is persisted in host metadata and isolated by workspace path and
server id. Agents and clients can read saved/active configuration, save without starting, or restart
with a complete replacement configuration. Restart preserves dirty editor drafts. Server arguments,
initialization options and workspace configuration sections support `${workspaceFolder}` in string
values. clangd additionally accepts workspace-relative include directories, fallback compiler flags
and a compilation database directory; fallback flags apply only without a file compile command.
Global disablement still gates startup. Explicit Start/Restart records the server's start choice
except when restart supplies a complete configuration, whose choice is preserved;
Stop clears it. The pane exposes saved values and the active configuration snapshot.


Language operations include logs with an explicit server id. Logs never provision or start a server, remain readable when LSP is disabled, and retain the newest 32 Ki characters per workspace/server across stop/retry until workspace closure. They include timestamps, launch command/arguments and workspace, stderr, window log/show messages, startup failures, lifecycle and observed exit codes. Protocol payloads and source contents are not logged. Agent log responses retain the newest 12,000 characters and indicate truncation.


Workspace configuration replies preserve scalar/array/object types and return JSON null for absent or explicitly null sections, allowing servers to apply defaults. They never substitute an empty object for an unset scalar option.


## Recorded verification scope

Desktop Release and focused local/WebSocket, MCP, state, registry, editor, pane, design and agent-capability checks passed before the main integration. Real managed TypeScript and installed Pyright/clangd returned diagnostics; TypeScript/clangd returned semantic tokens, while Pyright used syntax fallback. The installed Roslyn/Razor server initialized against workspace-11, returned 20 symbols and remained ready after the missing-configuration null fix, with no JsonValue or empty-object parsing errors. Other catalog recipes were not live-tested.

The latest four-lane gate completed lane 2; LayoutE2E, TerminalChromeChecks and ChangesE2E stopped at existing Git-only Changes entry expectations in non-Git fixtures. A complete full-gate pass is not claimed. Browser/Android compilation, physical-device acceptance and packaged-app replacement were not run for this LSP batch.

Agent-saved workspace arguments use the same persisted host metadata as user saves. Host-state broadcasts expose those saved values immediately in the workspace's configuration window, independently from the running server's active snapshot.

Workspace/server configuration also lives in atomic host-owned metadata files under `language/workspaces/<workspace-hash>/<server>.json`. They are authoritative for saved LSP configuration and survive older builds rewriting state.json without LSP fields. Existing state metadata migrates on opening the host; workspace removal removes its records, and Default inheritance persists independent child records. Reads refresh the host-state projection from metadata, so separate hosts can observe saved values while retaining independent running servers/active configurations. Settings opening and Reload saved request current configuration rather than reusing the last inventory snapshot. Persistence errors propagate instead of reporting a successful configure.

Persistence regressions pass on direct and WebSocket adapters: removing LSP fields from fixture state.json cannot erase metadata after reopening, and another live host’s saved compilation database is observed through configuration reads. Focused editor/pane checks also pass with explicit saved-value reload. The accompanying full run passed LSP cases but stopped in Files and rendered-diff fixtures amid filesystem EventStream startup errors; this does not establish a complete gate or multi-process server sharing.

The final broader run additionally timed out in the Branch Graph UI scenario. Its incomplete-case record is retained; the focused LSP results do not imply that these unrelated UI cases passed.

## Project import and search scopes

Cross-file navigation uses native language-server project models, never a text-search substitute.
Bounded manifest discovery supplies setup and coverage evidence, not a semantic module graph.
Roslyn loads solution/projects, restores through its public client protocol and waits for project
initialization before becoming ready; its SDK directory is also available on PATH. JDT uses separate
workspace configuration and data directories. Nested independent Go modules become workspace folders;
nested Rust manifests are linked unless the user supplied linkedProjects. Metals enables build import.
Import prompts, restore failures and advertised indexing progress prevent successful navigation from
implying complete results. Servers without authoritative index completion retain explicit coverage limits.

References default to the calling workspace. File scope filters to the current file; dependencies scope
retains all native server locations, including outside the workspace (`all` remains an alias). Filtering
precedes totals and pagination. Module scope requires reliable provider ownership: no current adapter
exposes it, so the UI omits that choice and explicit module requests report unsupported. A nearest
manifest folder is not a module. Dependency scope cannot find symbols absent from the server's loaded
graph. clangd needs compilation commands for background indexing; CSS and dynamic-language servers
do not acquire whole-project semantic references merely from a workspace root.

Cross-file acceptance uses real TypeScript path aliases, Python imports and clangd compilation
commands with unopened consumer files. Real Roslyn acceptance covers first-ready references in an
unopened dependent project and five files in the SharpRail solution. Remaining provider policies
are fixture-verified only; no catalog-wide semantic-index completeness claim is made.

## Optional IntelliJ Java/Kotlin provider

The host-global provider choice (`standard` or the id of a server that `Replaces` others) selects either
`intellij` serving both languages or standard `java` (JDT) and `kotlin` providers for every project and client. Switching suspends competing
sessions across all active workspaces while retaining drafts and saved provider settings; it does
not start replacement servers. Explicit global selection overrides legacy workspace flags. On first
migration, unanimous explicit legacy choices are preserved; missing or conflicting choices select
standard providers to avoid broadening an optional evaluation provider's execution implicitly.
Legacy `useForJavaAndKotlin` values remain readable but cannot override the host's choice.

Optional absolute host `installationPath`, absolute `projectJdk` and `buildTool` remain workspace
configuration, persisted through ordinary host metadata and Default inheritance. The project JDK
is distinct from the distribution's bundled JBR. Global selection never bypasses installation-specific
terms acknowledgement, including when another workspace first uses the selected provider.

The provider follows JetBrains' official Neovim client approach, explicitly approved on 2026-10-16:
user EULA acknowledgement, an unmodified native server over stdio, and vendor-enforced build
validity. No independent entitlement or revocation service is claimed. Paid-license activation and
offline entitlements are not implemented. Existing providers remain available through explicit opt-out.

### Artifact and legal boundary

The official catalog was rechecked on 2026-10-16 and still selects `263.6379.0`. Six platform URLs
and published SHA-256 values are pinned from
[JetBrains' catalog](https://github.com/JetBrains/intellij-lsp.nvim/blob/main/lua/intellij-lsp/server_bundles.lua).
The six pins were previously verified against the independent CDN `.sha256` files. The researched
macOS aarch64 archive SHA-256 is `443157ce085dae947637594ab883692fa7e952ae0c7d0baa6eb917fc91cabdd1`;
its exact EULA SHA-256 is `34d850193ee04897094bff4f61396f80e02f21b0a75c22c4cf5f700b265094df`.
The EULA is JetBrains LSP Extension Public EAP Agreement v1.0, effective July 31, 2026: internal
evaluation/feedback only, no redistribution, ending at the earliest of 30 days from vendor build
release, GA, EAP discontinuation or revocation, followed by cessation/deletion. A subscription does
not extend an expired EAP. No local clock or invented grace period substitutes for vendor enforcement.
The server's actual release date and current eligibility are not independently established by
SharpRail; an accessible archive is not proof of authorization. The user reviews the actual artifact's
terms. No production support, paid activation or independent live revocation guarantee is advertised.

Downloads go directly from JetBrains to the workspace host; no server binaries or proprietary
terms are bundled, mirrored or committed. Apache client attribution and notices remain separate in
`licenses/intellij-lsp.nvim.NOTICE.md`. No VS Code process, private licensing patches, credential
handling, clock changes, trial resets or client impersonation are introduced.

### Provisioning and execution

The dedicated UI terms-review operation prepares the managed distribution when no installation
is configured. An absolute user installation is read directly and never deleted by SharpRail.
Managed provisioning validates pinned SHA-256, bounded ZIP/tar contents, paths and links,
product/version/launcher/EULA; preserves executable modes and notices; serializes installs with
an exclusive lease; and publishes atomically with cancellable staging cleanup. Unsupported
platforms fail clearly. Agent LSP calls do not accept terms or download an unreviewed server.

Every process launch rechecks the current installation fingerprint and acknowledgement. Missing or
changed acceptance returns `requiresAcceptance`, with an explicit review action. Start in the pane
prepares/reviews terms and resumes only after the user’s explicit Accept and start action. Settings use the same explicit setup/accept/start flow; reviewing an already accepted agreement still requires an explicit Start action.
Launch arguments are `--stdio --system-path <isolated-cache> --eula <hash>` where hash is the first
16 lowercase hexadecimal SHA-256 digits of the actual EULA bytes. Ordinary overrides cannot replace
the acknowledged launcher or reserved arguments. Unsupported IntelliJ extensions, lazy intentions
and run code lenses are disabled. Project JDK/importer and advanced initialization settings persist.
Workspace/process caches avoid index-lock collisions and are removed when their session is disposed.

The server owns licensing enforcement, as in the official client. Exit 7 is reported as `expired`;
requests return no diagnostics or semantic tokens, and the retained session/failure prevents
polling from restarting it. A managed artifact that reports expiry is marked and deleted; its
same-version automatic download is refused. User installations are preserved. Other disconnections
remain ordinary errors. Handling exit 7 does not establish subscription/revocation/offline validity
or detect a vendor revocation that the server does not report. Select existing Java/Kotlin providers
or configure an authorized newer installation and review its agreement to recover.

### Explicit EULA acknowledgement

The owned agreement window displays the full exact host installation's `EULA.txt`, build, artifact
fingerprint and full digest. An unchecked acknowledgement plus explicit Accept is required. Cancel
records nothing. The authenticated dedicated UI RPC is separate from agent language operations;
MCP, ordinary configuration, defaults and migrations cannot accept terms.

Records bind canonical installation path, deterministic fingerprint of the full file tree without
following links, build version and full EULA SHA-256. Metadata reads are bounded and repeated to
reject changes during review; full-tree review is cancellable and bounded to 100,000 entries/8 GiB.
Accept recomputes identities; changed terms/build/binary require fresh acknowledgement. Atomic
records under `<stateDir>/language/agreements/intellij/<artifact-id>.json` contain identity, version,
digest and acknowledgement time only. They contain no credentials or agreement text and are not
workspace-inherited configuration or broadcasts. Unchanged installations on one host reuse explicit
acknowledgement across workspaces; corrupt records require fresh acceptance.

### Verification limits

Synthetic HTTP/archive fixtures cover integrity, extraction attacks, cancellation, concurrent reuse,
terms identity and cache isolation. Synthetic agreements cover local/WebSocket persistence, changed
terms/build/binary, atomic failure and MCP exclusion. Native fake-server lifecycle checks exercise
acknowledged Java/Kotlin launch, dirty buffers, exit-7 invalidation and no automatic restart; these
process-launch checks run on Unix, while archive/protocol checks are portable. No actual JetBrains
terms were accepted or server executed by the harness. Real server compatibility, current artifact
eligibility, paid activation and live entitlement transitions remain unverified.

The 2026-10-16 official-client-flow batch passed the checkout-local Release build with zero warnings
and errors, touched-file whitespace verification, focused `--lsp`/`--lsp-ui` checks, and the full
141-case four-lane gate. The rendered settings and Accept and start dialogs were inspected. The
expiry fixture also verifies workspace-wide Problems reports expiry rather than a clean/ready result.
No LSP MCP diagnostic tool was exposed in this session, so an all-edited-files LSP scan was not run.
Browser, Android and real JetBrains server acceptance were not run.

## Agent code-intelligence verification — 2026-10-16

Release compilation passed with zero warnings/errors; touched-file whitespace verification,
focused language checks and the 141-case four-lane gate passed. Direct/WebSocket fixtures cover
unopened-file references, workspace/dependency scopes, IntelliJ jar:/jrt: source previews and
character paging, unsupported schemes, rejected arbitrary/cross-workspace URIs, restart grant
invalidation, warm process reuse, overload paging and fresh symbol selection after dirty edits.
The public dependency-source protocol follows intellij-lsp.nvim decompiler.lua at revision
9cfad18005470e4187090013e08f92ec9e437219; attribution is retained in licenses/.
No real JetBrains server smoke test, productivity benchmark or new license acceptance was run.
Provider-reported loaded graphs remain the source of semantic scope; module ownership is still
explicitly unsupported where no reliable provider contract exists. The full gate skipped upstream
Git scenarios requiring SHARPRAIL_TEST_GIT_SOURCE. No LSP MCP tool was exposed to this agent, so
edited-file LSP diagnostics could not be requested. The Release compiler covered edited C# files.

## Headless validation and named settings

`configurationSchema` exposes provider-labelled, catalog-version-labelled named
settings over the existing language service and MCP, including when LSP execution
is disabled. Values map to the same persisted initialization/settings JSON; there
is no second configuration store. Empty fields retain provider defaults. Custom
executables and SDK-bundled servers may differ from catalog controls. The settings
window preserves unknown JSON properties and rejects invalid typed edits before
saving. Remote clients obtain these schemas from the workspace host. Managed provisioning source is included as descriptive metadata, not proof of the active executable’s origin. Configuration windows separate implementation, publisher, version and source into distinct lines, label the source “Source” and pair it with the matching bundled Material Icon Theme registry or toolchain mark (a generic package mark for unknown sources), and explain that managed versions are pinned for configuration-structure compatibility. SDK toolchains and custom/PATH binaries are explicitly distinguished. Startup rows animate a vector spinner only while installing or initializing.

IntelliJ and JetBrains Kotlin send `projects` as an array; legacy empty objects are
normalized, other object/scalar shapes are rejected. Project JDK precedence is an
explicit workspace setting, explicit initialization `defaultSdk`, workspace
`.sdkmanrc`, JAVA_HOME selecting an installed SDKMAN candidate, SDKMAN current,
then another eligible JAVA_HOME. An unavailable requested
SDKMAN candidate fails explicitly. Homebrew/Kotlin-bundled JAVA_HOME is not an
implicit project JDK. Analysis `defaultSdk` and importer `java-home` are distinct:
for a discovered root Maven/Gradle project the selected JDK is supplied to both;
explicit per-project importer overrides remain intact. Logs identify selection
provenance without claiming it explains a build-import failure. The bundled JVM
that runs the server is unchanged.

`tests/SharpRail.Language.Integration` is an opt-in Node runner using actual MCP
calls against the standalone host, pinned third-party project revisions and real
provisioners. It is never registered in the argument-free check gate. Its manifest
states whether a scenario proves cross-file references or document-only behavior;
missing runtimes/acceptance are blocked, not passed. Reports retain diagnostics
freshness and bounded server logs. Ruby cold bundle setup has the same bounded
15-minute initialization allowance as IntelliJ, rather than a normal 45-second
navigation request budget. Expensive corpus runs do not replace fast protocol,
transport, lifecycle and UI fixtures.

Workspace metadata loading enumerates only SharpRail's 64-hex workspace metadata
directories and their immediate JSON records. Provider-owned cache subdirectories
(for example PowerShell `session.json`) are not configuration records and cannot
crash a later host startup. Malformed owned identities report a metadata error.
PowerShell launches include an explicit `-LogLevel Normal`, avoiding a null-valued
parameter validation error in Editor Services 4.7.0.

Language-server warning dismissal prompts are logged without disabling navigation.
Error actions and informational actions during Roslyn's explicit project-load wait
still expose required configuration. A scalafmt dialect warning from Metals is not
proof that semantic indexing is blocked; fake-server startup includes such a
warning to guard this distinction.


R provisioning verifies that the pinned `languageserver` package can actually load
and has the requested version before writing its ready marker. R package installers
can return success after native dependency failures; those failures must remain
provisioning errors. A legacy ready marker without the package DESCRIPTION is
repaired on the next attempt.


2026-10-16 real-provider verification: 17 of 18 catalog scenarios passed against
pinned real repositories through the headless production MCP endpoint. Thirteen
proved cross-file references; R, PowerShell, shell and CSS proved document symbols
and diagnostic publication. IntelliJ was blocked at explicit user acknowledgement,
not passed by fixtures. See `tests/SharpRail.Language.Integration/RESULTS.md` for
projects, source counts, SDK limits and the unresolved broader Markdown UI timeout.


2026-10-17 rebase verification on main a8abf30f: Release, language host/UI fixtures
and the full check gate passed in aggregate after resolving the independent settings
protobuf field collision and one provider-form spacing violation. The previously
recorded Markdown UI timeout did not recur; real-provider corpus results were reused.


2026-10-17 IntelliJ runtime follow-up: the acknowledged macOS ARM64 263.6379.0
artifact initialized on an empty controlled workspace with import disabled after
host FSEvents capacity recovered. Its earlier FileWatcherException was reproduced
under the host-wide client limit and attributed to duplicate plugin watch ownership
in `submodule-host-plugins`. This is initialization-only evidence, not verification
of Rell's Gradle import, project indexing, or the IntelliJ corpus scenario.


## Detected project-root metadata

The read-only `projects` operation exposes detected build roots across providers through
LanguageResult.Projects. A single bounded traversal reuses provider discovery rules,
skipping dependency/build/symbolic directories and checking cancellation. Results carry
root, manifest, provider, kind, detected state and explicit provenance. Detection is not
native module membership or successful project import; freshness stays partial. There
is no standard LSP module graph, and no provider-confirmed graph is synthesized here.

The operation starts/provisions nothing, filters competing JVM providers according to
saved IntelliJ selection, and supports deterministic pagination and provider filtering.
Discovery caches for five seconds per workspace; Query refresh bypasses it and workspace
closure clears it. Files may force refresh on listing changes. The operation uses existing
local/remote transport and lsp_status operation selection. Runtime inventory uses compact
session progress while detailed resolution/manifests remain in analysis coverage.


2026-10-17 verification: project-root discovery and pagination pass on direct/WebSocket
adapters, including exclusions, JVM selection, cancellation and workspace isolation.
Files vector badges, provider settings, build-file navigation and switching pass through
headless UI. Release, scoped whitespace, focused LSP/design and the resumed 144-case
gate pass; a startup timeout required its focused rerun and continuation. Production
Roslyn coverage is current for 25 edited C# files, all diagnostic pages retrieved, with
no errors/warnings. This verifies detected roots and adapter/UI behavior, not native
module-graph completeness or new real-provider compatibility.


Directory annotations distinguish detected build roots from provider-reported roots. An already
running Eclipse JDT server advertising `java.project.listSourcePaths` supplies source roots through
its public read-only command. The adapter follows JDT LS v1.61.0 BuildPathCommand's status/data/path
response, limits results to 500, rejects outside-workspace/nonexistent roots, deduplicates them,
and bounds metadata queries to two seconds. No metadata query starts a server. Warm results are
cached for ten seconds; stopped sessions contribute none. Build-file inventory is cached for one
minute and invalidated by explicit/file-change refresh. Listing remains partial and does not
claim complete imports or package ownership. Files polls only while visible and mounted, every
15 seconds, and stops its timer on detach. Source-root context menus preserve the containing
build-definition action. Standalone IntelliJ directory/package classification is not established
by its public client/extension handlers; no such labels are inferred from folder names.
Reference: https://github.com/eclipse-jdtls/eclipse.jdt.ls/blob/v1.61.0/org.eclipse.jdt.ls.core/src/org/eclipse/jdt/ls/core/internal/commands/BuildPathCommand.java

2026-10-17 directory/coloring verification: focused LSP host/editor, syntax, Context UI
and design checks pass. Synthetic JDT responses cover advertised-command gating,
containment, duplicate roots, non-starting reads, refresh and stop invalidation; source
roots are not proof of package ownership. Native Roslyn covers all 23 edited C# files,
current, all 11 pages retrieved, 310 informational/hint diagnostics and no errors/warnings.
Source-JAR UI navigation is fixture-tested through the existing authorized host reader;
real IntelliJ/JDT compatibility and browser/Android runtime remain unverified.
Final rebase onto main b61c6de1: Release and scoped formatting pass; all source gate
cases completed after one concurrent resource diff-row timeout passed unchanged in
serial continuation. Merged Codex launch/resume checks pass. Additional native Roslyn
diagnostics cover the three conflict-resolved C# files current with no errors/warnings.

Provider configuration schemas include public HTTPS website and repository links, including a label distinguishing proprietary-provider documentation/client repositories from server source. Metadata does not install or execute providers.

Status and inspection expose full semantic-token support from the initialized provider capabilities. Token decoding preserves the modifier bitset using that server's legend (up to 32 bits), without inferring static/extension roles from identifiers. Editor routing remains workspace-scoped.


The 2026-10-18 UI follow-up passed local/remote language checks and native Roslyn
diagnostics for 32 edited C# files, including a final current three-file refresh
with no errors or warnings. Semantic modifier typography was checked using the
protocol fixture and native Scintilla italic set/reset behavior; real IntelliJ
typography and authenticated Context indexing were not exercised.

2026-10-18 global-provider verification: Release and focused local/remote migration, global routing, two-project process teardown, consent, persistence and Settings broadcast checks pass. Existing host language and language Settings/editor setup checks pass. The broader language UI mode stops earlier in the unchanged empty-navigation assertion about unavailable analysis; real provider compatibility and browser/Android runtime were not rechecked.

Named configuration controls distinguish host file/folder paths, path arrays, patterns, argument
arrays, nullable booleans, numeric limits and enumerated strings. The [provider type research](CONFIGURATION.md)
records the exposed fields and primary sources for all catalog providers. Unset and explicit empty
values are distinct; Rust feature arrays and its `all` alternative remain distinct. Structured
values and unknown choice strings survive form refresh and unrelated edits through advanced JSON.
File selection is additive for lists. Native browsing is limited to an embedded desktop host; remote,
browser and Android clients edit explicitly labeled host paths without accessing their local filesystem.
Settings provider pages and workspace configuration expose the same HTTPS project/documentation links;
proprietary providers link to documentation/client sources rather than claiming public server source.

2026-10-18 typed-input verification: Release compilation, browser shared-source compilation,
scoped formatting, language host/local-WebSocket checks and focused settings UI checks passed.
The settings checks visit upstream links for all 18 providers and exercise nullable numeric entry
and the fixed IntelliJ importer selector. The broader language UI mode stopped on an unrelated
empty-navigation availability assertion before the settings cases. No full suite, native picker
interaction, Android runtime or new all-provider semantic acceptance was run. Existing AVLN3001
runtime-loader warnings remain visible.


## Project startup choices

Project language-server autostart selections are host state, shared by all clients and workspaces
of that project. The per-server configuration view saves its project checkbox immediately,
independently of workspace configuration and manual Start/Stop. Existing workspace autostart
inheritance remains separate; a project selection never changes that workspace setting.

Managed command launches and restored agent prefills request selected server startup in the
launching workspace before delivering the agent command. Warm language sessions are reused,
concurrent requests cannot duplicate a start, and failed or stopped sessions are retried on the
next launch. Startup is initiated before the agent; installation and indexing can finish later.
A failed or disabled provider stays visible in the language pane and does not prevent launching
an agent. Arbitrary agent commands manually typed into a shell have no prelaunch integration.

2026-10-18 verification: Release compilation and focused direct/remote regressions cover project preference persistence, sibling-workspace sharing, project isolation, concurrent starts, warm PID reuse, failed/stopped recovery, unchanged workspace autostart, and revival prefill ordering. Headless controls cover managed-command preparation before terminal attachment and immediate project checkbox persistence. Real provider provisioning and browser/Android runtime were not exercised by this change.

## Project language file

`.sharprail/language.toml` in the project root (the Default checkout, so every workspace and every
client of the project agrees; a remote host reads its own copy) holds the project's portable
language-server choices, committed and edited by the team and agents. The directory holds only
shareable configuration: SharpRail writes nothing else into it and never creates a `.gitignore`
there. Nothing is written until Settings > Code Intelligence saves with "Share with the project"
checked; opening a project, saving workspace-local settings and agent `configure` never create it.

```toml
version = 1                        # required; the file's format version

[servers.python]                   # one table per catalog server id
autoStart = true
[servers.python.settings.python.analysis]
typeCheckingMode = "strict"
```

Per server the portable keys are `autoStart`, `arguments`, `buildTool`, `compilationDatabasePath`,
`compilerFlags`, `includePaths`, `initializationOptions` and `settings`, exactly the option names and
types of workspace configuration. Values validate through the same parser and the typed option
catalog (`configurationSchema`): a wrong type or choice, an unknown server id or an option that
belongs to another server ignores that server's entry with a diagnostic. `installationPath`,
`projectJdk`, `useForJavaAndKotlin`, the global Java/Kotlin provider, launcher command overrides and
terms consent are host-specific and stay in host state; they are ignored with a diagnostic if found
in the file. Do not put secrets or absolute host paths in it; `${workspaceFolder}` expands as in
workspace configuration. TOML `null` does not exist, so the file cannot hold JSON nulls.

Precedence is per option key: a key stored in host state for the workspace wins, any other portable
key comes from the file, then defaults. A legacy host-state entry holds every key, so existing
configuration keeps working unchanged and the file only fills what the host never stored. Local
changes (agent `configure`, Start/Stop) store only keys that differ from the file. An explicit
shared save writes the portable keys to the file and drops them from the project's workspace entries
(host-specific keys stay). Project autostart is the host list when one exists, otherwise the servers
with `autoStart = true`; the first local change starts from the file's choices.

The host reads the file lazily off the UI thread, watches it and publishes its status
(`HostState.ProjectLanguageFiles`: path, existence, stamp, configured servers, diagnostics) so
every client and the pane refresh. Edits apply to running servers at their next start (the pane
shows restart required). A malformed, oversized or unsupported-version file is a diagnostic, never
a failure of language services or startup, and is never overwritten.

Saves edit the file's syntax tree (Tomlyn 2.10.1): comments, layout, ordering and unknown keys survive,
only the saved server's managed keys are replaced and a new server's table is appended. The result is
re-read and compared with the intended content before an atomic replace. Format growth is compatible
(new optional keys, tolerated and preserved). An incompatible change would ship as `language_v2.toml`,
created only by an explicit write; the older file stays untouched, readers prefer the highest version
they understand and this version reports newer files it does not read.
