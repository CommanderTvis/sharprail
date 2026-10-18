---
id: module-host-language
type: module-design
status: draft
title: Workspace language services
parent: module-host-core
---

# Workspace language services

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

The catalog pins managed server versions. Existing commands on the host PATH take precedence;
Swift and Dart follow the selected SDK. The app does not install global SDKs or change project
package manifests. Missing prerequisites and unsupported platform archives remain actionable errors.

Storage is `<stateDir>/language`: `servers/<id>/<version>` for installations, `cache` for reusable
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

Host administrators may set `<stateDir>/language/servers.json`, keyed by catalog id, with
`command` and an `arguments` array to override a launcher, `managed: true` to prefer the pinned
installation over PATH, or `disabled: true`. This file is host-owned, never read from a repository.
`SHARPRAIL_LSP_DISABLE=1` disables language startup, including in ordinary check fixtures.

Idle sessions without drafts expire after ten minutes. Workspace removal and host shutdown end
their processes. Stopping a listener does not stop an embedded host's language sessions.

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
Enablement, managed-install preference, command and arguments apply on next start/retry, without
restarting active servers. Arguments require an executable override. A null argument list uses server defaults; an empty list passes none.
The host-wide `catalog` request exposes supported ids, versions and storage without a workspace
or process startup, including when language execution is disabled.

The host-wide LSP enablement setting defaults to on and persists across restarts and transports.
Disabling it prevents new starts and stops all active sessions before the setting operation returns.
Dirty drafts survive suspension; closing a document while disabled removes its saved draft.
Re-enabling permits lazy startup with the existing per-server configuration.

Automatic TypeScript selection checks for a compiler in the workspace ancestry or beside the
resolved PATH launcher. A standalone language-server executable falls back to the managed pair
of TypeScript and its language server. Explicit executable overrides remain authoritative.

The workspace-free catalog identifies the default implementation/provider in each server detail. Agent MCP exposes catalog and workspace inventories plus explicit server-id startup/status, allowing multiple language sessions concurrently without UI interaction. Mixed-language file scans route each file separately and never implicitly install servers.

## Agent retrieval

Navigation returns a stable, paged list (offset plus limit, maximum 200) with total count
and explicit truncation. Definitions, types, implementations and references include bounded
source excerpts. Dirty editor drafts supply source when present; unavailable source is explicit.
Hover exposes inferred types, signatures and documentation when the server supports it.
Document outlines retain qualified parent/child names and full symbol ranges. `symbolBody`
selects an exact qualified name or the smallest symbol containing a UTF-16 position; ambiguous
names return candidates. Bodies are capped at 12,000 characters with an explicit truncation flag
and source range. Location-only servers do not pretend their symbol locations are body ranges.
Navigation also accepts exact qualified outline names for hover, definition, type definition,
implementation and references. Resolve documentSymbol selectionRange on every request after buffer
synchronization. Missing/ambiguous names or absent selection ranges never fall back to guessed
positions. Existing cursor navigation remains available for overload selection.
The source operation reads file/dependency text in 12,000-character pages (offset and nextOffset
count UTF-16 characters); documents over 1 Mi characters are rejected. Dependency source is fetched
fresh per request and shared within a result page; no stale decompiler cache survives project changes.
Workspace-symbol searches can span languages; narrow to a file for single-server pagination.

MCP tools advertise separate diagnostic, navigation and lifecycle schemas/descriptions rather than
repeating every configuration field. Older explicit operation dispatch remains compatible.
Status/start/restart expose freshness and project coverage. Existing ten-minute idle retention
remains, but importing/indexing sessions and dirty buffers are not evicted. Repeated requests use
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

The host-global Java/Kotlin provider choice selects either `intellij` serving both languages or
standard `java` (JDT) and `kotlin` providers for every project and client. Switching suspends competing
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
saving. Remote clients obtain these schemas from the workspace host. Managed provisioning source is included as descriptive metadata, not proof of the active executable’s origin. Configuration windows separate implementation, publisher, version and source into distinct lines, pair the source with a vector download mark, and explain that managed versions are pinned for configuration-structure compatibility. SDK toolchains and custom/PATH binaries are explicitly distinguished. Startup rows animate a vector spinner only while installing or initializing.

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
