---
id: module-plugin-claude-code
type: module-design
status: active
title: Claude Code — configuration, IDE bridge and terminal status
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins]
tags: [plugins, claude-code]
---

# Claude Code

Port source: `6401ccae8`, `packages/plugin-claude-code/SPEC.md` and its host/web modules in
CommanderTvis's ThinkRail fork. This plugin integrates the external Claude Code terminal program;
it does not provide bundled AI chat or Pi execution. It is disabled by default.

## Boundary

The root project owns the manifest, typed contract, configuration records and status vocabulary.
Host references only that contract and the plugin host API. UI references the contract, UI API and
shared kit. Neither half imports a host service or app implementation. Core composes the builtin
host; the app composes the builtin UI. Assets publish under `plugins/claude-code/assets`.

The generic plugin call, addressed event and keyed state transports carry this contract through
direct calls or gRPC. Claude Code's own IDE protocol remains an authenticated loopback WebSocket
MCP server: replacing it with gRPC would break the external CLI's protocol.

## Configuration

The pane resolves configuration for the first shown Claude terminal, then the first shown terminal,
then the workspace root. Every configuration/read/write/edit/CLI operation carries the selected
terminal key. The host uses the detected process's current directory, its reported directory, then
the workspace root. Appended system prompt files are additional instruction layers.

Settings retain the winning source and shadowed values. Context includes user and ancestor
instructions, `AGENTS.md` when a directory has no `CLAUDE.md`, local instructions, rules, imports,
memory and appended prompt files. Capabilities include MCP servers, plugins, skills, agents, hooks
and marketplaces with provenance, disabled sources and discovery problems.

Every mutation is composed, scoped to user/project/local, planned as a diff, reviewed and then
applied with the plan's content hash. Changing scope invalidates its preview; stale files require a
new review. Source links expose only discovered files and reveal JSON key paths in current editor
contents. CLI plugin/marketplace changes show the exact command before execution. Merely enabling
the SharpRail plugin does not approve a fresh Claude hook installation.

## Status and recovery

Activation owns the process poll, bridge, terminal environment, revive hook and status route.
Disable removes them and stops reading Claude configuration and polling processes. Nested agents
do not replace an outer terminal agent record.

The status URL includes a host-minted terminal token. Its POST resolves the terminal, reads known
events leniently and publishes a keyed status push. `statusSnapshot` returns an array, one row per
reporting tab. The UI folds all pushes through one store and hydrates workspaces on first use.
Facts-only pushes retain prior status, plan and usage. Closing/clearing a terminal forgets its cache.

Session start is idle; prompt submit/tool completion running; permission request blocked; Stop done;
Stop failure failed. A continuation's Stop still settles status with notifications suppressed.
Interrupts fire no CLI hook, so the host polls the transcript's last conversation turn, ignoring
bookkeeping and rejecting markers older than the current running turn.

Token usage is read from the hook's transcript path, incrementally over complete lines. Streaming
blocks with one message id count once at the last usage; subagent transcripts contribute too.
There is no estimated usage or subscription cost. Older hooks without a transcript path retain the
fork's standard-directory fallback.

Revive retains invocation flags, removes old positional prompts and resume/continue flags, then
offers a known surviving session id or the fork's continue fallback. Blueprint's independent revive
hook contributes submit without consuming this plugin's text.

## IDE and UI

The bridge writes a private discovery lock and checks its token before upgrading a connection.
An agent identifies its process before gaining a workspace. Selection and document-close events
remember the owning app client; IDE actions are addressed to that client. Opening/checking/saving/
closing editors follows the fork exactly. Its `openDiff` opens the target file and answers
`diffShown: false`: Git diff tabs do not represent unsaved proposed content.

UI contributions are the Context/Settings/Capabilities/Account pane, compiled settings section,
workspace launcher, generic `claude` launcher, status tab decorations and one terminal accessory
combining install offer, facts, model/effort pickers, plan, tokens, attach and session `/ide` switch.
The generic launcher supplies a fresh Claude SVG icon through its owning asset reader for
consumer plugins, at the requested size and theme tint.
The four model aliases are opus/fable/sonnet/haiku. Launch flags use shell syntax appropriate to the
host, with agent view disabled by default and the host's prompt file first. Caller prompts stack
independently. The settings switches affect app-started sessions, not commands the user types.

Agent terminals request Shift+Enter as raw Escape followed by Return when neither kitty nor
modifyOtherKeys mode 2 is negotiated. Ghostty handles negotiated protocols itself. Clearing the
agent identity restores its normal encoding; the request survives terminal startup and retry.
Host-state updates retain the accessory mount so an open picker keeps its control identity.

The marketplace contains its hook plugin and MCP declaration. Reports POST rather than emitting
OSC 777, which unrelated terminals interpret as user notifications. Installed cached versions are
inspected and refreshed after prior approval; a new installation requires the offer's confirmation.

## Verification and remaining gates

`--claude-code` checks isolated host enable/disable, configuration provenance, review without writes,
stale preview refusal, preservation, token routes, session identity, spending, continuation Stop,
revive, client folding, a real authenticated WebSocket IDE initialization/catalogue/selection/action
round trip addressed to one app client, and headless configuration/launcher/settings lifecycle,
agent newline mode removal and retained accessory identity. It also runs the host halves of
the fork's `claude-config.spec.ts` mutation cases through the plan/apply methods the pane's composers
call (an `@`-import at depth one, an MCP server added to `.mcp.json` then denied locally, a skill,
hook and plugin written to the project with a plugin move and marketplace add previewed and run
through a recording CLI, connectors parsed from `claude mcp list`, a local override shadowing a
user value) and both `claude-launcher.spec.ts` cases through the real launcher, menu and settings. The native terminal probe checks
agent fallback, default, kitty and modifyOtherKeys input bytes. Hook scripts have
their own shell checks. The integrated `--plugins` runner includes these checks.

Still required for the full port: complete source-by-source parity audit, the remaining IDE action
and failure cases, the pane-driven halves of the configuration cases (composer dialogs, narrow-pane
scrolling, source links after unsaved edits, account/usage and the MCP health row), the review
round trip over MCP, the terminal facts cases, terminal picker and sealing
input behavior, away-window desktop notifications, compiled static pane/dialog layouts, remote
UI parity, native appearance and the full repository verification gates. Current focused checks
do not establish these remaining requirements.
