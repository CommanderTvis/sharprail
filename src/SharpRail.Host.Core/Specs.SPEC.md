---
id: submodule-host-specs
type: submodule-design
status: active
title: Specs — workspace spec catalog
parent: module-host-core
---

# Specs — workspace spec catalog

Upstream: packages/server/src/spec/SPEC.md @ 12830b08
Upstream: packages/spec-graph/SPEC.md @ 12830b08
Upstream: packages/spec-graph/core/SPEC.md @ 12830b08

## Responsibility

Serve the read-only Specs panel: the specs found in the active workspace, as `SpecDocument` records
(`Id`, `Title`, `Path`, `Parent`, `Type`) through `IProjectServices.ListSpecsAsync`. The filesystem is the
source of truth; the catalog is derived on demand and holds no state of its own, so specs edited in the
editor, a terminal or by Git are current on the next read. The workbench re-reads after a Markdown file
changes (see [Files.SPEC.md](Files.SPEC.md)); the UI builds the parent tree.

## Boundary

- Owns: `SpecCatalog.ReadAsync(root, ct)` — the traversal, the is-a-spec rule and the frontmatter read.
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
  locations the catalog could never see.
