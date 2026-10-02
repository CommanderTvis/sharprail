---
id: module-plugin-spec-dialect
type: module-design
status: active
title: Spec dialect — the spec graph as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
---

Upstream: packages/plugin-spec-dialect/SPEC.md @ e8a751bd7 (CommanderTvis fork)
Upstream: packages/spec-graph/{core,tools}/SPEC.md @ 0304a543e

## Responsibility

Owns the spec graph, its seven MCP tools, the Specs side tool and `spec:<id>` document links.
The plugin is builtin, enabled by default, has no plugin dependencies and uses wire version 1.
The graph is `plugin.spec-dialect.graph({ workspaceId })`; an unknown workspace fails.
The host re-resolves workspace identity, requests a filesystem watch and caches one index per
workspace. A removed workspace evicts its index. The wire title falls back to the node id.

## Boundary

The Contract assembly contains the manifest and wire records. Host references the plugin host API,
Contract and YamlDotNet, with no dependency on the application or host implementation. UI references
the plugin UI API, Contract and UI Kit. Core owns the MCP protocol and terminal token routing;
the plugin contributes all `spec_*` tools. Neither the panel nor the graph uses a spec-specific
host RPC, application store or core panel implementation.

## Spec files and graph

A spec has nonempty scalar `id` and `type` frontmatter fields. YAML supports flow/block lists,
quoted values, BOM and CRLF; malformed frontmatter is ignored. Unknown types are accepted on
reads. Traversal does not follow symlinks, ignores `.git`, `node_modules`, `dist` and `build`,
and sorts all directory entries together by NFC-normalized name, then exact name. The first
file with an id wins the graph node; validation retains all duplicate paths. A file is cached
by modification time and size; every read discovers additions, edits and deletions.

Edges are `parent`, `depends-on`, `references` and `implements`. List fields also include `covers`
and `tags`. The filesystem remains the source of truth. Graph slices preserve traversal order,
include each edge once, report missing targets and terminate on cycles. Validation reports
all dangling edges, duplicate ids and parent cycles.

## MCP tools

- `spec_grep`: substring or regex, case insensitive by default, filtered by type, tag, parent or
  dependsOn. Returns path, one-based line and snippet; default limit 200, with truncation only
  when a further match exists.
- `spec_get`: frontmatter, path and resolved forward/reverse links across all four edge kinds,
  with missing paths marked. It returns no prose body.
- `spec_graph`: subtree, ancestors or neighbors, optional edge kind and depth (default one).
- `spec_create`: unique id and an unused, root-relative `.md` path; known type/status enums,
  canonical frontmatter order, inline lists and heading stubs chosen by type. Rejects paths
  outside the root, ignored directories, symlinks, non-indexable extensions and collisions.
- `spec_update`: scalar set/remove and list append/dedupe/prune. It never renames id, removes
  id/type or leaves either empty; set on a list field is refused. YAML comments, custom fields,
  BOM, line endings and prose survive the edit.
- `spec_delete`: removes one file by id and leaves inbound links for validation to report.
- `spec_validate`: all graph issues, or an explicit valid result.

Schemas come from the typed parameter records; malformed arguments fail before the tool runs.
Tools return text and structured details, and error outcomes carry `IsError`.

## UI

Activation drives graph reads from the active workspace and filesystem revision, independently
of whether the Specs panel is mounted. Concurrent reads for one revision collapse. The store
retains list identity when values are unchanged, tracks failure per workspace and evicts removed
workspaces. Graph changes invalidate document links; disabling the plugin disables those links.

The panel shows six loading rows, `No specs` for an empty graph, and an inline error with Retry
while retaining the last good tree. Parent trees tolerate dangling links and cycles. Rows start
expanded, show role tags on hover/focus, shorten spaced en/em dashes to middle dots, and mark
the active file with the selected colors and filled role icon. A root goal is `Main spec`.
Click previews; double click or Enter keeps the document. Editor changes update selection
without replacing the panel. A seeded rail falls back to Files if the graph is empty or the
plugin is off; the dormant plugin tab keeps its layout slot. Legacy `specs` tool ids migrate
to `plugin:spec-dialect:specs` in saved layouts, selections and presets.

The headless checks translate the fork's `specs-panel.spec.ts` through the real panel: the tree,
role tags, editor tabs and specs added mid-session; a failed first read showing `Couldn't load
specs.` and recovering on Retry; and a failed update keeping the previous tree under `Couldn't
update specs.` until Retry. Failures are injected at the app's plugin service, where the fork
injects them at its WebSocket.

Pi registration and chat tool cards follow the repository's explicit Pi/chat exclusion.
The terminal MCP tools implement the portable spec dialect without a Pi dependency.

## Checks

`SpecDialectChecks` covers host graph reads, workspace removal, local/gRPC parity, tree/store/sync,
legacy layout migration, real-input plugin toggling, active-file selection and rail defaults.
`SpecToolChecks` translates the spec graph and tool contracts through the real runtime's MCP table,
including all seven tools, authoring, malformed input, path safety, YAML preservation and validation.
The preview, Markdown links, live refresh and docking suites exercise the moved panel in the workbench.
