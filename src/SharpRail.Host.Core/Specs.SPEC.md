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

The fork's independent project-level durable-spec Welcome suggestion is not part of
SharpRail's current Welcome surface. Its graph and authoring operations are provided
by the plugin rather than reintroduced in core.
