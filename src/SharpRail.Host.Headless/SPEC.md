---
id: module-host-headless
type: module-design
status: draft
title: Headless host: stdio MCP for one workspace, for AI research
parent: module-host-core
depends-on: [module-host-core, module-plugin-api]
tags: [headless, mcp, research]
---

# Headless host

Design only, not built yet. The reviewed proposal is [docs/develop/headless-design.md](../../docs/develop/headless-design.md).

## Responsibility

A console executable, `sharprail-headless`, that starts the host core and the non-UI builtin plugins and serves MCP over
stdio for exactly one workspace, `/workspace` in the container. It exists so an agent such as Claude Code can use
SharpRail's language, spec and Blueprint tools from a `.mcp.json` entry, and so a researcher can switch those tools on and
off and log every call.

## Boundary

- Owns: the `mcp`, `warm`, `check-config`, `schema`, `manifest` and `report` commands, the stdio loop, the tool decorator
  (switches and the opt-in run log), the Dockerfile and the image publish workflow.
- Allowed deps: `SharpRail.Host.Core` only.
- Forbidden: Avalonia, Skia or any UI project, Kestrel or any network listener, terminals, per-terminal tokens, and any
  behaviour that belongs in Core. The tool table and tool behaviour come from Core and the plugins unchanged.

## Behaviour

- One implicit workspace. No terminals. Terminal-only tools (`workspace_*`, `set_title`) are not offered.
- stdout carries MCP frames only. Everything else goes to stderr. End of stdin or SIGTERM stops the host cleanly with exit 0.
- Exit 2 means a configuration error, printed before any frame is written.
- The run log is off by default. When on, it records tool, operation, timing, size and outcome, and no argument or result
  text unless the detail level says so. See the design for the event schema.
- Configuration comes from `.sharprail/*.toml`, container environment and flags, in that increasing order of precedence.
  Existing `language.toml`, `instructions.toml` and `workspace.toml` keep every key and meaning.
