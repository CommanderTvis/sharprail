---
id: module-plugin-air-gateway
type: module-design
status: draft
title: Air Gateway — Codex and Claude Code through JetBrains Central
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins, submodule-ui-panels]
tags: [plugins, central, governance]
---

# Air Gateway — Codex and Claude Code through JetBrains Central

Upstream: central-integration.md, packages/shared/src/jbcentral.ts (adapter), packages/server/src/auth/jbcentral.ts
(lifecycle), apps/web/src/panels/JetBrainsAiCard.tsx (card) and e2e/fixtures/bin/central (fixture) @ the fork's
`central-access-api` line. The fork routes Central into `pi` through one extension file; pi is out of scope here, so
this plugin keeps the fork's detection, sign-in, proxy, update and access-source behaviour and replaces the pi
extension with Central's `central run <agent>`, one launch at a time, for Codex and Claude Code. omp, which
`central run` (1.13.0) cannot start, gets Central's Pi wire through its environment (see Mechanism).

Air Gateway was called Air Governance until JetBrains renamed the product; the plugin id `air-governance` is migrated to `air-gateway` when host state loads (HostState.SPEC.md), and the fork's and STATUS.md's older entries keep the old name.

## Responsibility

Lets the user choose where Codex and Claude Code get their AI, from Settings › Air Gateway:

- Direct (default): each agent keeps its own account. The plugin contributes nothing.
- JetBrains AI through Central: each enabled agent (Claude Code, Codex, omp; all on by default) is started through
  JetBrains Air Gateway by the user's own `central` CLI.

The chosen source and the per-agent switches are the plugin's settings namespace (`source`, `claudeCode`, `codex`, `omp`),
persisted by the host and effective at once for the next launch. The plugin ships disabled, since governing agents is
the user's choice.

## Mechanism

`central run <agent> [flags] [-- extra-args...]` launches an agent through JetBrains Air with settings passed for that
launch only; Central states that the agent's configuration is not changed. The plugin therefore never edits any agent
configuration and never calls `central add` or `central remove`: nothing under `~/.claude`, `~/.codex`,
`CLAUDE_CONFIG_DIR` or `CODEX_HOME` is written, and switching back leaves nothing to undo.

With Central chosen, the plugin contributes, through the Plugin API's agent launch seam (W22 in the UI half for
launchers and starts, H18 in the host half for revived terminals), a replacing wrapper
`<central> run <claude-code|codex> --` to launcher ids `claude` and `codex`. The agent plugins apply it to the
executable word of the command they build: the launcher, its right-click flags, the Create-workspace and Project Home
starts, resumes and the host's revive offer, so the agent's usual arguments follow the `--`. SharpRail's own
environment prefixes stay outermost. A custom command in the agent's settings (not the default `claude` or `codex`)
is never rewritten and so is not governed; the pane says so. Running terminals keep the command they started with.
`<central>` is the path the host found, else the bare name: if Central is missing the launch fails in the shell
rather than silently starting an ungoverned agent. The pane shows the exact command shape each agent will get.

omp is governed without `central run`, which (Central 1.13.0) has no omp agent and no way to name another binary.
Central's own Pi integration (`central add pi`, installed at `~/.pi/agent/extensions/jetbrains-central.ts`) routes
Pi's built-in providers to the proxy's Pi wire `http://127.0.0.1:<port>/wire/<id>/pi/...` with the key `wire-proxy`
and runs `central proxy start --ensure-updated --caller agent` at session start. Loading that file into omp with `-e`
does not work: in a real run (Central 1.13.0, omp 18.12.6) omp applied the key but not the base URL and the request
went straight to Anthropic (401). Setting `ANTHROPIC_BASE_URL=<wire>/anthropic` and `ANTHROPIC_API_KEY=wire-proxy`
answered through Central's proxy. So with omp governed, the host reads (never writes) the wire from that file's
Anthropic `registerProvider` line (loopback only, at most 64 KiB) and publishes it in the status; the contribution to
launcher id `omp` sets those two variables and a non-replacing wrapper
`sh -c '"$0" proxy start --ensure-updated --caller agent >/dev/null 2>&1; exec "$@"' <central>` that starts the
proxy best effort, as the integration does, and execs omp with its usual arguments. Only Anthropic models are
governed: omp documents `ANTHROPIC_BASE_URL` but no base-URL variable for OpenAI or Vertex, so the integration's
OpenAI and Vertex routes are not mirrored, and an omp model from another provider runs on omp's own account; an
`ANTHROPIC_OAUTH_TOKEN` in the environment takes precedence in omp over the key. Nothing under `~/.pi` or `~/.omp` is
written and `-e` is not passed. When the integration is missing, its wire cannot be read, or the UI has not yet
received the status, the governed omp launch runs a command that cannot exist
(`air-gateway-needs-central-pi-integration--run-central-add-pi`), failing in the shell like a missing Central; on
Windows, whose shells get no contributed environment, it runs `air-gateway-cannot-govern-omp-on-windows`. The pane
then says "Air Gateway needs Central's Pi integration: run `central add pi`" with the command and a Copy button;
the plugin never runs `central add`.

Central chooses its own AI access source and proxy; `central access <id>` (switch, then `central proxy restart`)
stays the way to change it, as in the fork. The pane states persistently that switching affects every app using the
host OS user's Central.

## Detection and actions

The adapter is the fork's, bounded the same way: the binary is the first `central` on the host's PATH or
`~/.local/bin/central`; `--version` (5 s) must parse as `central X.Y.Z` and be at least 1.4.0 (outdated otherwise,
malformed and probe-failed distinguished); `central status` (15 s) is read only for its `Auth` and `Proxy` rows,
and only a positively observed `not connected` or `stopped` sets a flag. Output is capped at 16 KiB, timeouts kill
the process tree, stderr is discarded.

Only local, read-only checks run automatically: `central --version`, `central status` and, with Central chosen and
signed in, `central limit --json` (15 s, 16 KiB), never `login`, never a browser, never the OAuth or Cloud endpoints. The pane runs it when it opens, on window
activation (at most once per 15 s), after Sign in and Sign out, and, while a sign-in is awaited after Sign in, every
5 s for at most 3 minutes, stopping once signed in, on Sign out, or when the pane closes. Checks never overlap, run
off the UI thread in the host half under the 5 s and 15 s bounds, drop results arriving after the pane closed, and
do not run while the pane is not attached and visible or the plugin is disabled (nothing then runs Central at all).
The pane shows checking, signed in, signed out (Sign in enabled, Sign out disabled), not found, outdated and
error states. There is no manual check button. Everything else is an explicit button: Sign in (`login`, browser, waits 1.5 s for a fast failure), Sign out (`logout`), Start proxy
(`proxy start`), Update Central (`update --install`) and Switch (`access <id>`, on a source the automatic listing below shows,
then `proxy restart`; a failed restart is a warning). A missing CLI shows the platform install command from
central-cli.labs.jb.gg as text; the plugin never runs it.

With Central chosen and signed in, the pane also shows the chosen source's remaining credits, for example `16.27 of 20.00 credits remaining, refills 2027-01-15` (plus the top-up when nonzero). They are read from `central limit --json` with every automatic check and after a successful Switch: `tariffQuota.available` and `maximum` (numbers or numeric strings; else `maxDollars` less `usedDollars`), `topUpQuota.available` and `refillNext` (epoch ms). Status and the pane carry only those numbers, never the email or licence name. Missing, malformed, negative, non-finite or inconsistent values, a failed or timed-out command, signed out and the direct source all show nothing.

## Credits in the header

While Central is the chosen source and governs at least one agent (`AirLaunch.Wired`: an enabled Claude Code or Codex
switch), the UI half shows the remaining credits as `x / y credits` at the trailing end of each window's header,
before the connection status and Settings, through the Plugin API's header item (W24); otherwise it shows nothing.
The numbers are the status channel's credits (above), so the host that runs `central` supplies them, including a
remote one; the host stamps each successful read with its time (`ReadAt`). The tooltip names the governed agents,
the plan's remaining and total credits, the top-up when nonzero, the next refill and when the credits were read
(the licence name and email stay off the status channel, so the plan is not named). Below a tenth of the plan left,
top-up included, the item uses the warning colour. Clicking it opens Settings › Air Gateway.

Reads are the status method with the probe (so the same `--version`, `status` and `limit --json` as the pane), one
shared reader per activation however many windows show the item: when an item attaches or its window activates,
when a terminal whose agent ran through `central run` leaves the host projection, and every 5 minutes, at most once
per 15 s (the pane's throttle); and at once when the source or an agent switch changes. Reads never overlap; a forced
read during one runs after it. Nothing is read while no item is attached, Central governs no agent or the plugin is
disabled. Status pushes (the pane's checks, actions and the agents' Account reads) update the item without a read.
Before the first read nothing is shown; after a read without credits the item shows `credits ?` in the warning
colour with the reason in the tooltip (not installed, outdated, cannot be used, not signed in, sign-in unknown, no
credits reported, or the call's error). A compact (phone) workbench shows no header items; the credits stay in
Settings › Air Gateway.

## Account for governed agents

A host-side launch contribution also carries `Account` (Plugin API H18): source `Central`, read on request. Codex and
Claude Code call it from their Account tab while their launches are governed, and it runs a fresh probe and
`central limit --json` (the read above), then returns the `email` and `licenseName` of that output, the credits and
the refill time. They are passed on for display only, never stored or logged, and never put on the plugin's status
channel. An unavailable Central is reported as one actionable message instead of numbers: not installed, cannot be
used, not signed in (sign in under Settings › Air Gateway), outdated, or credits unreadable. The UI-side
contribution carries no account.

## Listing access sources


`central access` output is not parsed. Listing follows JetBrains/thinkrail#708 and runs automatically, with no button, only while Central is the chosen source, supported and signed in: when the pane opens, when the source becomes Central, after sign-in, after a Switch (which also refreshes the credits) and on the throttled activation check (at most once per 15 s). Until then the pane says what it waits for (checking, Central not installed or unavailable, sign in to Central) and the direct source shows nothing. The listing runs in the host half; a result arriving after a newer request, the source changing or the pane closing is dropped. It runs:
`central login export` (16 KiB, 15 s) yields a version-1 `central-<base64url>` envelope with `environment`,
`refresh_token` and the current `ai_access` selection. The refresh token is exchanged (`client_id=central-cli`) at
the fixed OAuth endpoint of that environment and the access token is sent to JetBrains Cloud's
`user-management/api/ai-access-options`. Only `prod` and `staging` are known; both endpoint pairs are a fixed HTTPS
table in the host half, so nothing in the envelope or a response can name a host. Enabled workspaces and licences give
the selection ids (`workspace:<org>:<ws>`, `license:<id>`) and display names; the exported selection gives the
current marker.

Everything fails closed with a specific pane message: no export (signed out), a malformed envelope, an unknown
environment (nothing is sent), a redirect (never followed), any HTTP error, a response over 64 KiB, a malformed token
or options response, a timeout (15 s), and a refresh token that differs from the exported one. Only a well-formed
empty list says Central listed no selectable sources. The envelope and endpoint are internal interfaces of Central
and JetBrains Cloud, not a documented listing contract, and may need updating when they change. Tokens stay in
locals and byte buffers that are cleared after use (strings from the JSON reader cannot be cleared and are dropped
for collection); nothing is logged, stored, thrown or sent to the pane. Listing runs one at a time.

Compatibility risk (from #708): listing refreshes Central's credential independently of Central. If the OAuth server
rotates the refresh token and revokes the original, rejecting the replacement does not undo that server-side change;
Central would keep an invalid credential and every app sharing its session could be signed out. This is conditional,
not a reproduced failure: #708's live check on 2026-10-13 with Central 1.13.0 saw two exchanges return the identical
refresh token (RFC 6749 section 6 permits rotation). A Central-owned authenticated listing operation or a supported
credential synchronisation contract would remove the assumption.

Switches serialise behind the action gate; a request for the source queued last shares that switch, any other source
queues its own, so A, B, A runs in order and ends on A.

## Untrusted output

Central's output and the endpoint responses are data. Only the version, the sign-in and proxy verdicts, exit codes and
failure reasons (as fixed messages) and access-source display names reach the pane; names are the one derived text
shown (as in the fork), stripped of control characters and capped at 80 characters. Selection ids must match `workspace:<id>[:<id>]` or
`license:<id>` before they are passed back as an argument. The plugin stores and logs no credentials, tokens or
Central output, and never passes `--accept-tos`.

## Threading

All process and filesystem work is in the host half behind plugin RPC; the pane only builds controls and applies
replies. Mutating calls are serialised and publish a busy status on the `status` channel before and after.

## Verification and limits

`--air-gateway` runs the host and pane checks against a fake `central` modelled on the fork's fixture (version,
status rows, `login export`, access switching, proxy, login, update) plus a `run` that records its argv, under a throwaway home: detection,
the contributions per source and toggle (omp's wire, proxy start and missing-integration block included; `--omp`
runs the governed omp line through a real shell), the Claude and Codex launch lines executed by a real shell so prompts with
quotes and `$(...)` arrive as single arguments, a fake HOME whose `.claude`, `.codex`, `CLAUDE_CONFIG_DIR` and
`CODEX_HOME` stay byte- and time-identical across every action and source switch, failures, access switching, the
host seam over plugin RPC (including disable), the pane and real launchers through headless input, and the header
item against a stand-in host: hidden for direct and for Central with no agent, credits after a toggle, the warning
colour, the reason when credits cannot be read, a pushed status, the click into Settings, and removal on disable.
The listing runs against a loopback fake of the OAuth server and Cloud (a checks-only endpoint override that production
code never sets): success with the current marker, signed out, unknown environment, redirect, malformed and oversized
responses, rotation, secrets absent from every surfaced string, switching with proxy-restart failure and A, B, A.
Not verified against the real CLI or production endpoints (never called from checks): the live envelope and endpoint,
the exact `central status` output format, that
`central run <agent> -- <args>` forwards SharpRail's prompt, plugin-dir and MCP arguments and environment to the agent
unchanged, how `central run` picks the access source, and the ToS prompt of `central access`. The parsers follow the
fork's.
