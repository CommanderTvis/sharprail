---
id: module-host-context
type: module-design
status: draft
title: Workspace semantic Context search
parent: module-host-core
---

# Workspace Context integration

SharpRail optionally runs the unmodified JetBrains Context CLI on the workspace host.
It downloads pinned 0.9.16.1161 macOS/Linux ARM64/x64 binaries directly from JetBrains,
verified against publisher SHA-256 sidecars. No server binary, installer, agent hook or
proprietary code is bundled. Windows has no verified artifact in this catalog.
[Official CLI integrations](https://github.com/JetBrains/context) and
[service documentation](https://www.jetbrains.com/help/jetbrains-console/getting-started-with-jetbrains-context.html)
are protocol/product references; the public repository is not the CLI source.

## Explicit user controls

The existing agreement form binds actual-user acknowledgement to the pinned artifact
identity and current combined CLI EAP/AI terms hash. A download does not run the CLI or
enable indexing. Current terms and an unconditional full executable digest are checked
before every fresh native execution; display-only digest caching never authorizes it.
Changed terms, missing acknowledgement, corrupt binaries or unavailable validation
fail closed. No agent operation records consent or enables a workspace.

Context enablement is a host-owned, persisted list of absolute workspace paths, shared
through state broadcasts and typed local/WebSocket adapters. The UI explicitly explains
cloud processing and automatic indexing on SharpRail agent launch. New workspaces do
not inherit this permission. Disabling cancels owned requests/indexing and disposes the
workspace runtime. Removing a workspace also drops its retained in-memory logs.

The current CLI uses its existing JetBrains account, secret store and index cache.
A private `-Duser.home` setting did not isolate its configuration and is not used in
production. SharpRail never changes HOME/CODEX_HOME, modifies vendor configuration,
runs setup-agent/refresh-agents/remove-agent/upgrade/logout, or installs global Claude/
Codex instructions/hooks/MCP entries. Interactive sign-in is an explicit user action
in a SharpRail-owned host terminal; credentials never pass through SharpRail's state,
MCP results or ordinary configuration. This is integration isolation, not a separate
JetBrains identity/cache. The workspace confirmation discloses that distinction.

## Runtime and agent workflow

First recognition of a SharpRail UI-launched Codex/Claude process requests background
indexing only for an enabled workspace. A per-host launch set handles restored records
without retriggering on model/status updates. Each workspace deduplicates in-flight
index jobs. The native indexer receives the fixed absolute project path and JSON output;
the CLI owns native/distributed indexing locks. Jobs have a 30-minute bound and stop
through their owned cancellation/process tree. Other hosts' or users' processes are
never killed or globally stopped. The native watch daemon is not started or controlled.

SharpRail's terminal-authenticated namespace and explicit headless MCP route expose:

- context_search: descriptive semantic discovery, an optional contained path filter
  and bounded output. It cannot choose another workspace or an arbitrary upstream tool.
- context_status: cheap managed state, with optional vendor-reported index status.
- context_index: refresh an already-enabled workspace and return immediately.

Search uses a warm stdio MCP connection with exactly one workspace root. The adapter
negotiates supported MCP versions, discovers code_search's schema, adapts text/query
and supported path/limit arguments, handles roots/list and rejects unsupported client
requests. Frames are bounded to 1 MiB and calls to two minutes; visible output is capped
at 30,000 characters with an explicit narrowing hint. Cancellation kills only the owned
connection, and the next query reauthorizes/reconnects. Structured native output remains
structured in SharpRail tool replies. No user-control command field reaches agent tools.

Authentication failures are actionable states, not successful empty search results.
Index command completion is not a proof of complete module coverage. Native search
output/status remains authoritative for snapshot availability. Searches may lag working-
tree edits: SharpRail does not register roots with the vendor's global overlay watcher,
and unsaved editor drafts remain an LSP/editor responsibility. Agent prompt appendices
advertise Context for unfamiliar code and LSP for known symbols, then targeted reads.
Only SharpRail-launched Codex commands receive Context tool approval overrides.

Logs are bounded, workspace-scoped and redacted before retention, including JSON secret
fields, bearer headers and URL queries. Search text/results are not persisted as logs.
UI teardown closes views, not host indexing; host shutdown closes Context with language
services. Direct and remote clients use the same host-owned implementation.

## Verification limits

Focused fixtures exercise host enablement persistence, direct/WebSocket state and RPC,
launch-triggered indexing, restored launches, deduplication, namespace routing, path
rejection, controlled login recovery, in-flight cancellation, progress/redaction,
command arguments, schema adaptation, reconnect and teardown. Node/fake executables
never accept JetBrains terms, authenticate or index real source. Expensive real-provider
corpora remain opt-in and are not added to the full gate.

Current native 0.9.16.1161 help/config/MCP startup were inspected in a sandbox denying
network and writes outside task scratch after confirming the user's existing artifact
receipt and publisher checksum. The signed-out MCP startup requires login. No real
login, cloud upload, indexing or authenticated search has been executed by the agent.
A signed-in smoke test and opaque vendor cache/watcher behavior remain unverified;
fixture results do not establish those guarantees or completely private credentials.


## 2026-10-17 verification

Release build and whitespace verification for 35 touched C# files pass. Focused
Context/core/UI, LSP UI, design, state, agent-launches and Codex checks pass. Production
MCP/Roslyn reports current diagnostics for all 35 edited C# files, with 222
informational/hint diagnostics and no errors or warnings. AXAML has compiler/UI
coverage; browser/Android native acceptance and signed-in vendor search remain untested.
The 144-case gate completed after a terminal focus assertion failed in a concurrent
lane, then passed its focused rerun and last-failed continuation without code changes.
The separate --claude-code mode passes host/UI checks but retains an existing launcher
assertion for the removed center tab strip. The Release build retains three existing
Avalonia URI-loader warnings. Existing spec validation has four BranchGraph dangling
links; it does not validate this workspace's new spec through the Default-bound tool.
