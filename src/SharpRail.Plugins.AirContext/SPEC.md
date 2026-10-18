---
id: module-plugin-air-context
type: module-design
status: draft
title: Air Context — semantic code search through the JetBrains Context CLI, as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins]
tags: [plugins, air-context]
---

# Air Context — workspace semantic search

No upstream counterpart. Original SharpRail builtin plugin; before it was a plugin the same code lived in the host core and the
Code Intelligence page.

## Plugin shape

The plugin id is `air-context`. It ships disabled, like Air Governance. Enabled, it still does nothing by itself: the CLI is not downloaded, no terms are read from the
network and no workspace is indexed until the user acts in the plugin's sections of the Code Intelligence pages. Settings › Plugins is the only switch for the plugin as a
whole. Disabled, the plugin serves no tools, contributes no instructions to agent sessions, shows no section anywhere, accepts no consent calls,
makes no network requests and starts no process; disabling stops running indexing and searches through the activation's disposer, and
the user's workspace enablement and consent survive for when it is enabled again.

The host half (`Host/`) registers three methods and a channel (`consent`, `workspace`, `changed`), the agent tools, one tool-guidance
item (H20) and observers of terminal agent changes and workspace removal. The UI half (`UI/`) contributes no side tool; it uses the slot seam (W23) for two sections of core's Code Intelligence pages,
present exactly while the plugin is enabled. The account section, in Settings › Code Intelligence, holds what is global to the JetBrains
login: terms review and download, Refresh, Sign in and Sign out, and the signed-in state. The workspace section, in the workspace's
language servers pane, holds Enable for this workspace, indexing state, Index now, Stop and Logs; when the login is missing it shows a
one-line notice and an Open Settings button (`OpenSettings`) instead of a sign-in button of its own. The consent window and the logs
window belong to those sections.

The login is the CLI's, so the host keeps one signed-in fact for all workspaces (`ContextOutcome.SignedIn`: true, false, or null when not
yet probed). The workspace operation `check` probes the CLI's `status` (off the UI thread, no indexing, no source sent) and, like `login`
and `logout`, needs no workspace enablement; a change is published on `changed` to every enabled workspace, so another window or client
follows without reopening. Before this, `status` only reported a state cached from earlier index and search attempts and never asked
the CLI, so a completed sign-in was never noticed. The account section probes on opening, on focus (at most every 15 seconds), on
Refresh, and, after the sign-in terminal opens, every few seconds for at most five minutes, stopping when the tab closes or the login
is accepted. The CLI offers no account query, so no account name is shown and no credential is read. Wire types are in `Contract/`.

State lives in the plugin's own files (H14, H19): the enabled workspaces in `plugin-state/air-context/workspaces.json` and the CLI
storage (agreement receipts and the downloaded executable) in `plugin-state/air-context/files`. The user's consent cannot be inferred
from the executable's presence. Host state versions before protocol 8 kept both in the core; opening such a state file moves the
directory and the list once (`AirContextMigration`), sets the plugin explicitly: disabled when the old "hide Air Context" setting was on, enabled when the profile had used Air Context (an
enabled workspace, a consent receipt or a downloaded CLI) and did not hide it, and untouched (so disabled by default) for a fresh profile; an
enabled flag the plugin already has always wins, and removes the old keys in the same save.

The paragraph advertising the tools to agents is the plugin's tool guidance and appears in the Claude Code and Codex instructions
only while the plugin is enabled. The Codex launch line still preapproves the three tool names, whether or not the plugin is on.

SharpRail optionally runs the unmodified JetBrains Context CLI on the workspace host.
It downloads pinned 0.9.16.1161 macOS/Linux ARM64/x64 binaries directly from JetBrains,
verified against publisher SHA-256 sidecars. No server binary, installer, agent hook or
proprietary code is bundled. Windows has no verified artifact in this catalog.
[Official CLI integrations](https://github.com/JetBrains/context) and
[service documentation](https://www.jetbrains.com/help/jetbrains-console/getting-started-with-jetbrains-context.html)
are protocol/product references; the public repository is not the CLI source.

## Explicit user controls

The agreement form binds actual-user acknowledgement to the pinned artifact
identity and current combined CLI EAP/AI terms hash. A download does not run the CLI or
enable indexing. Current terms and an unconditional full executable digest are checked
before every fresh native execution; display-only digest caching never authorizes it.
Changed terms, missing acknowledgement, corrupt binaries or unavailable validation
fail closed. No agent operation records consent or enables a workspace.

Context enablement is a persisted list of absolute workspace paths owned by the plugin's host half, changed only by the plugin's
user-interface methods and published on its `changed` channel to every client. The UI explicitly explains
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

First recognition of a SharpRail UI-launched Codex/Claude process (a terminal agent change the plugin observes) requests background
indexing only for an enabled workspace. A per-host launch set handles restored records
without retriggering on model/status updates. Each workspace deduplicates in-flight
index jobs. The native indexer receives the fixed absolute project path and JSON output;
the CLI owns native/distributed indexing locks. Jobs have a 30-minute bound and stop
through their owned cancellation/process tree. Other hosts' or users' processes are
never killed or globally stopped. The native watch daemon is not started or controlled.

SharpRail's terminal-authenticated namespace and explicit headless MCP route expose, while the plugin is enabled:

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
UI teardown closes views, not host indexing; disabling the plugin or host shutdown ends the activation, which stops owned indexing and
processes. Direct and remote clients use the same host-owned implementation through the generic plugin call.

## Verification limits

Focused fixtures (`--context`, `--context-ui`, the `context-consent`, `air-context-lifecycle` and `air-context-migration` gate cases)
exercise consent persistence, direct and WebSocket plugin calls, launch-triggered indexing, restored launches, deduplication,
tool scoping, path rejection, controlled login recovery, in-flight cancellation, progress/redaction, command arguments, schema
adaptation, reconnect and teardown; that disabling the plugin removes its tools, its instructions and both its sections (and that no side tool exists), stops indexing and
makes agent launches a no-op with no network or process activity; and the migration of consent, enablement and the hidden flag.
Node/fake executables never accept JetBrains terms, authenticate or index real source. Expensive real-provider corpora remain opt-in
and are not added to the full gate.

Current native 0.9.16.1161 help/config/MCP startup were inspected in a sandbox denying
network and writes outside task scratch after confirming the user's existing artifact
receipt and publisher checksum. The signed-out MCP startup requires login. No real
login, cloud upload, indexing or authenticated search has been executed by the agent.
A signed-in smoke test and opaque vendor cache/watcher behavior remain unverified;
fixture results do not establish those guarantees or completely private credentials.
The sign-in terminal tab opens under the plugin's tab key (the plugin cannot see when the typed command ends, hence the probe) without the "Air Context sign-in" title the core pane used to set.
