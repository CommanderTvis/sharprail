---
id: submodule-host-specs
type: submodule-design
status: active
title: Specs — workspace spec catalog
parent: module-host-core
---

# Specs — workspace spec catalog

Upstream: packages/server/src/spec/SPEC.md @ 4a65ed7f
Upstream: packages/spec-graph/SPEC.md @ 4a65ed7f
Upstream: packages/spec-graph/core/SPEC.md @ 4a65ed7f
Upstream: packages/server/src/mcp/SPEC.md @ b047c8f2 (CommanderTvis fork)

## Responsibility

Serve the read-only Specs panel: the specs found in the active workspace, as `SpecDocument` records
(`Id`, `Title`, `Path`, `Parent`, `Type`) through `IProjectServices.ListSpecsAsync`. The filesystem is the
source of truth; the catalog is derived on demand and holds no state of its own, so specs edited in the
editor, a terminal or by Git are current on the next read. The workbench re-reads after a Markdown file
changes (see [Files.SPEC.md](Files.SPEC.md)); the UI builds the parent tree.

## Boundary

- Owns: `SpecCatalog.ReadAsync(root, ct)` — the traversal, the is-a-spec rule and the frontmatter read;
  `McpServer`, which serves the catalog to agents in a terminal as MCP tools.
- Forbidden: editing specs, writing any file, or depending on an agent or tool package.

## Rules

- A Markdown file is a spec when its frontmatter carries `id`, or when it is named `SPEC.md`. Frontmatter
  is the block between a first line of `---` and the next `---`, read as flat `key: value` scalars (at
  most 80 lines); lists and nested values are not interpreted beyond stripping brackets from `parent`.
- A spec without `id` takes its relative path as id; without `title` it takes a first-line `# ` heading,
  else its path, so the panel never shows an untitled node. `type` defaults to `spec`.
- The traversal never follows symbolic links and skips `.git`, `.tools`, `.bench`, `.sharprail`,
  `node_modules`, `bin`, `obj`, `dist`, `build`, `artifacts`, `vendor`, `target` and `.next`, plus files over
  512 KiB.
- On a duplicate id the first spec found wins; the result is ordered by title.
- The read runs off the UI thread and is cancellable; Specs loads progressively after the workspace is
  mounted, never on the startup critical path.

## Agent tools over MCP

- `McpServer.HandleAsync(message, cwd)` is a minimal, stateless MCP server over single JSON-RPC request
  objects: `initialize` (echoes `2024-11-05`, `2025-03-26` or `2025-06-18`, else answers the latest, and
  advertises only tools), `ping`, `tools/list` and `tools/call`. A notification is acknowledged with 202
  and no body; a batch or a non-JSON-RPC frame is `-32600`, an unknown method `-32601`, an unknown tool
  `-32602`.
- Tools: `spec_grep` (substring or regex, case-insensitive by default, narrowed by `type` or `parent`,
  `path:line: snippet` results capped at 200 by default) and `spec_get` (type, title, path, and parent links
  in both directions; no body). Each tool's published input schema is the enforced contract: arguments
  that do not match it, and any tool failure, come back as an `isError` result the agent can read, never
  as a protocol error.
- The host decides the `cwd` from the calling terminal's token (see the Remote SPEC); the tools read that
  workspace only.

## Not yet ported

- A cached per-workspace index that revalidates each file by modification time and size, re-parses only
  changed files, rebuilds the graph only when the spec set changed, and is dropped when its workspace is
  removed.
- A deterministic traversal order (candidates filtered first, then sorted by NFC-normalized name with a
  code-unit tie-break, directories and files in one list) so the duplicate-id winner is the same on every
  filesystem.
- The full graph: `depends-on`, `references` and `implements` edges with reverse edges, validation of
  dangling links, duplicate ids and parent cycles, and a YAML frontmatter dialect that tolerates CRLF and a
  leading BOM.
- A project-level "has durable specs" query (ignoring ephemeral `task-spec` nodes) for a Welcome
  suggestion.
- Spec authoring (create, frontmatter-only lossless update, delete) with a single path rule refusing
  locations the catalog could never see, and with it the `spec_create`, `spec_update`, `spec_delete`,
  `spec_graph` and `spec_validate` MCP tools and `spec_grep`'s `tag`/`dependsOn` filters.
