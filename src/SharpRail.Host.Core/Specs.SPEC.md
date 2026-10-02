---
id: submodule-host-specs
type: submodule-design
status: active
title: Specs — ownership moved to the spec dialect plugin
parent: module-host-core
references: [module-plugin-spec-dialect]
---

Upstream: packages/server/src/spec/SPEC.md @ e8a751bd7 (CommanderTvis fork)

The spec graph, Specs panel and all seven `spec_*` MCP tools belong to the builtin
[spec dialect plugin](../SharpRail.Plugins.SpecDialect/SPEC.md). Core no longer has a
`SpecCatalog`, spec records or a `ListSpecsAsync` operation. The plugin runtime composes
its host half and the app loader mounts its UI half through the ordinary plugin API.

Core's `McpServer` owns the stateless MCP JSON-RPC protocol and serves only the active
tools supplied by the runtime for the calling terminal. It implements initialize,
ping, tools/list and tools/call; notifications return 202, invalid frames -32600,
unknown methods -32601 and unknown tools -32602. Tool failures are `isError` results.
The loopback route authenticates the terminal token and supplies its workspace cwd.
Initialization instructions are plugin-neutral and never require a spec workflow.

Serve the Specs panel and the Welcome suggestion: the specs found in the active workspace as `SpecDocument`
records (`Id`, `Title`, `Path`, `Parent`, `Type`, `Status`) through `IProjectServices.ListSpecsAsync`, the
whole graph with its validation through `GetSpecGraphAsync`, and `HasDurableSpecsAsync`. The filesystem is
the source of truth; the index is derived and revalidated on every read, so specs edited in the editor, a
terminal or by Git are current on the next read. The workbench re-reads after a Markdown file changes (see
[Files.SPEC.md](Files.SPEC.md)); the UI builds the parent tree.

## Boundary

- Owns: `SpecIndex` (traversal, is-a-spec rule, per-file cache, graph and validation), `SpecFrontmatter`
  (the frontmatter dialect), `SpecCatalog` (one index per workspace root) and `SpecAuthoring` (create,
  update, delete and the path rule).
- Forbidden: depending on an agent or tool package; writing a spec anywhere the traversal cannot see.
  Authoring is a Core API only: no host operation or panel edits specs.
- `McpServer` serves read-only spec tools to terminal agents.

## Rules

- A Markdown file is a spec when its frontmatter carries `id`, or when it is named `SPEC.md`. Frontmatter
  is the block between a first line of `---` and the next `---`, tolerating a leading byte order mark and
  CRLF. It holds top-level `key: value` scalars (plain or quoted, with trailing comments dropped), flow
  lists (`[a, b]`) and block lists (`- a`); nested maps are not interpreted. `parent` written as a list
  means its first item.
- A spec without `id` takes its relative path as id; without `title` it takes a first-line `# ` heading,
  else its path, so the panel never shows an untitled node. `type` defaults to `spec`.
- The traversal never follows symbolic links and skips `.git`, `.tools`, `.bench`, `.sharprail`,
  `node_modules`, `bin`, `obj`, `dist`, `build`, `artifacts`, `vendor`, `target` and `.next`, plus files over
  512 KiB. In each directory the candidates are filtered first and then sorted by NFC-normalized name with
  a code-unit tie-break, directories and files in one list, so the walk is the same on every filesystem.
- On a duplicate id the first spec in that walk wins; the result is ordered by title.
- The graph has one `SpecEdge(From, To, Kind)` per `parent`, `depends-on`, `references` and `implements`
  link of each winning spec; reverse edges are the same list read by target (`SpecGraph.Sources`).
  Validation rides on the graph: links whose target is no spec, ids claimed by several files (all paths,
  winner first) and rings of specs that reach themselves through `parent`.
- The index is cached per workspace root. A read walks the tree, revalidates each Markdown file by
  modification time and size, parses only changed files and rebuilds the graph only when a file was
  parsed or left; an unchanged workspace returns the same graph object. The index is dropped when its
  worktree is removed. An unreadable folder below the root hides only its own specs; an unreadable root
  fails the read.
- `HasDurableSpecsAsync` is true when any spec's type is not the ephemeral `task-spec`, and false rather
  than an error when the workspace cannot be read. It is asked lazily for the one project Welcome shows.
- The read runs off the UI thread and is cancellable; Specs loads progressively after the workspace is
  mounted, never on the startup critical path.

## Authoring

- One path rule (`SpecAuthoring.ResolvePath`): root-relative, ending in `.md`, no `..`, no component in
  an ignored directory, no symbolic link on the way, not a directory. Everything it accepts the traversal
  can see.
- Create writes `id`, `type`, `status`, `title`, `parent` and inline lists in that order, and refuses an
  existing file or an id the index already knows. Delete and update find the file by id.
- Update edits the frontmatter block only: set or remove scalars, add to or take from the list fields
  (`depends-on`, `references`, `implements`, `covers`, `tags`). Untouched keys, comments, the body, the
  line ending and a byte order mark survive byte for byte. `id` cannot be set, `id` and `type` cannot be
  removed, and a list field is never set as a scalar.

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


- A full YAML parser for frontmatter (anchors, nested maps, multi-line scalars); the dialect above covers
  what specs use.
- Case-folded path matching in the path rule for case-insensitive filesystems, and the content search and
  query helpers of upstream's spec tools, which serve agents.
