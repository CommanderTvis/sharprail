---
id: module-plugin-lsp-clangd
type: module-design
status: draft
title: C / C++ (clangd) — a language server as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# C / C++ (clangd) language server plugin

The plugin id is `lsp-clangd`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`clangd` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: clangd · LLVM project.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behaviour is the host's ([language services](../SharpRail.Host.Core/Language/SPEC.md)).

It accepts include paths, compiler flags (sent as `fallbackFlags`) and a compilation database directory, and counts `build/compile_commands.json` and the configured database as manifests.

## Primed index in new workspaces

clangd's `--background-index` keeps one shard per source file in `<project root>/.cache/clangd/index`, named
`<file name>.<hex of XXH3-64 of the absolute path, little-endian>.idx` (clangd 23.1), and stores absolute paths and
`file://` URIs in the shard's string table. A shard copied into another checkout is therefore never looked up: a
plain clone of `.cache/clangd` re-indexed all 151 files of a 150-file C++ fixture (35 s, the same as cold).

The plugin's `PrimeWorkspaceAsync` (`ClangdIndexPrimer`), which the host runs after a managed workspace is created,
finds index directories up to four levels below the source checkout (skipping dot directories, `node_modules`, `bin`
and `obj`), and for each shard: reads the RIFF `CdIx` container, rewrites every string where the source root stands as a
whole path prefix (raw or URI-encoded) to the target root, re-compresses the zlib string table, keeps every other chunk
byte for byte, and writes it atomically under the name clangd derives from the new path. The shard's own path is found
by matching the old file name against its strings; a shard that does not match, is malformed, or already exists in the
target is skipped and left for clangd. Content digests are kept, so clangd loads every unchanged file's shard and
indexes only changed ones.

Measured on that fixture (Apple silicon, clangd 23.1): cold 36.9 s to background-index idle; primed 0.29 s with no
file re-indexed and no failed shard load; after editing one file, only that file was indexed (0.89 s). Priming the
151 shards took 57 ms. The target's compilation database must name its own paths: a database that is ignored and
not cloned leaves nothing for clangd to index either way. Check mode `--workspace-clone` covers the rewrite.