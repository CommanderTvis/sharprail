# ACP builtin (Early support)

Early support for the Agent Client Protocol (https://agentclientprotocol.com): SharpRail is the client, an ACP agent is a child process
speaking JSON-RPC over stdio, and the conversation is shown in the shared chat view (`src/SharpRail.Plugins.Chat/SPEC.md`, "Serving another
agent protocol"). Disabled by default; the manifest declares `Support = early`, its label is "ACP" and
`UnavailableOn = [pureWeb]`, because an agent is a child process of the host and a browser tab has none. The Android and browser clients reach
a host's ACP chats through the chat contract like any other client.

The agent is untrusted code. It is never sandboxed by SharpRail; the rules below limit what the client methods give it.

## Protocol (stable version 1)

- Framing: one JSON-RPC 2.0 message per line (`AcpConnection`); either side sends requests and notifications; requests from the agent run
  concurrently so a waiting permission prompt never blocks reading; a request pending when the agent's output ends fails with
  `AcpClosedException`.
- `initialize` sends `protocolVersion: 1`, the client capabilities (`fs.readTextFile`, `fs.writeTextFile`, `terminal`) and `clientInfo`. An
  agent answering another version is refused by name (`AcpVersionException`). Agent capabilities read: `loadSession`, prompt capabilities
  (`image`, `audio`, `embeddedContext`), `authMethods`, `agentInfo`.
- Session: `session/new` (modes, models and config options of the answer fill the header), `session/load` when the agent advertises
  `loadSession` and the chat has a stored session id (the replayed updates rebuild the transcript; otherwise a new session starts),
  `session/prompt` for a turn, `session/cancel`, `session/set_mode`, `session/set_model`, `session/set_config_option`.
- Updates folded by `AcpFolder`: `user_message_chunk`, `agent_message_chunk` and `agent_thought_chunk` (grow one item until another kind of update
  arrives), `tool_call` and `tool_call_update` (keyed by `toolCallId`; text content, diffs as `-`/`+` lines with the file path for the card,
  terminal references), `plan` (one assistant item replaced in place, entries as a checklist), `available_commands_update` (slash menu),
  `current_mode_update`, `config_option_update`, `session_info_update` (title), `usage_update` (context size and cost).
- Stop reasons: `end_turn` is Idle; `cancelled` adds an interrupt marker; `refusal` and errors add a failure notice; `max_tokens` and
  `max_turn_requests` add a warning.
- Permissions: `session/request_permission` becomes a permission card with the agent's own title; the call stays open until the user answers.
  Nothing is auto-approved. Allow answers with the agent's `allow_once` option, or `allow_always` when the user asks to remember it and the
  agent offered one; deny answers `reject_once`. An agent that offers no option of the wanted kind is answered as cancelled. Cancelling a turn
  answers every waiting request as `cancelled`, as the protocol requires.
- Client methods (`AcpClientMethods`): `fs/read_text_file` (with `line` and `limit`), `fs/write_text_file`, `terminal/create`, `terminal/output`,
  `terminal/wait_for_exit`, `terminal/kill`, `terminal/release`. Every path and every terminal working directory is resolved, symbolic links
  included, and refused when it leaves the workspace root. A terminal runs its command directly (no shell), merges stdout and stderr, cuts the
  front of its output at the agent's byte limit and is killed when released, when the chat closes or when the agent exits.
- Process: `ProcessAcpTransport` starts the agent in the workspace directory (or the entry's directory) with the entry's environment added; closing
  stdin and then killing the process tree ends it. An exit shows the chat as Exited with the last stderr lines and a retryable failure notice;
  opening the chat again starts a new agent and resumes the stored session through `session/load` when the agent can.

## Registry

- Source: `https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json`, schema version `1.0.0`
  (`{version, agents[], extensions[]}`; each agent has `id`, `name`, `version`, `description`, optional `repository`, `website`, `authors`,
  `license`, `license_url`, `icon` and `preview`, and a `distribution` with any of `npx` and `uvx` (`{package, args?, env?}`) and `binary` (per
  `darwin|linux|windows`-`aarch64|x86_64` target: `{archive, cmd, args?, env?, sha256?}`). The per-agent schema is `agent.schema.json` of the
  registry repository. `tests/SharpRail.Checks/Fixtures/acp-registry.json` is a trimmed real copy.
- Cache (`AcpRegistryCache`) under `~/.sharprail/acp/registry`: a copy younger than the registry's 5 minute `max-age` is not refetched; a stale
  one is revalidated with `If-None-Match` and `If-Modified-Since` (a 304 keeps it); offline, or when the answer is not a registry, the saved copy
  is shown with a note; only with no copy at all a built-in two-agent seed stands in, and says so. Files are written beside and moved into place.
- Install (`AcpInstaller`): the settings page asks for a plan first (source, exact command line, SHA-256, license, repository) and shows it;
  `Install` needs `Confirmed` and the agent, distribution kind and version of that plan, or it refuses (the registry may have moved on), so every
  agent and every version needs its own confirmation. npx entries run `npx -y <package>`, uvx entries `uvx <package>`, both pinned by the
  registry. A binary is downloaded over https only, checked against the registry's SHA-256 (a mismatch discards it; a missing checksum is
  said so in the plan), unpacked with the platform's `tar` into `~/.sharprail/acp/agents/<id>/<version>/` and its command must stay inside
  that directory. The registry's `preview` channel is ignored.
- Entries for agents SharpRail supports directly (a small id list in `AcpRegistry`: Claude and Pi adapters) get a Not recommended tag with the
  reason, sort after the others, and say so in the install confirmation. They stay installable. Manually added agents are never flagged.
- Icons: the registry gives each agent an `icon`, an https url of a 16x16 monochrome SVG on the registry's own host that uses `currentColor`.
  `AcpIconCache` (the host half, under `~/.sharprail/acp/registry/icons`, one `.svg` and one `.json` of validators per agent id) fetches only
  urls that are https, on the registry's host and default port, with no credentials, with no redirect followed, a 10 second timeout and a 64 KB
  cap, and revalidates with the ETag like the registry (a copy younger than 12 hours is not refetched). `AcpIconRules` refuses a document that is
  not well-formed SVG XML, declares a doctype, contains `script`, `foreignObject`, `image`, animation or embedding elements, an event handler, an
  `href` that is not a `#fragment`, or a CSS `url()` or `@import` that is not a fragment; an icon that turns unsafe is dropped with its old copy.
  Ids that are not plain file names are skipped. The UI asks the host (`icons`, never with urls) after the list is shown and draws the SVG with
  the theme's text colour for `currentColor` (hard-coded colours stay); until then, with no icon, for a rejected one and for custom agents it
  draws the generic ACP glyph. The New ACP chat menus read icons from disk only. The chat header keeps the plugin's own icon.
- Settings layout: the agents and the registry use the `RegistryRow` of the UI kit, the row shape of the language servers in Code Intelligence
  (status, icon, title, settings and action columns, details under the title). Each registry row says Install, Update or Installed, or that the
  agent is not available on this machine, and the search filters by name, description and id. That page's own rows are bound to its data and stay
  as they are.
- Install confirmation: the plan is shown in the in-window dialog (`DialogWindow`), over the window that has the Install button, with the name
  and version, source, command, checksum, license, repository and the explanation; Escape and Cancel install nothing.
- Default agent: `AcpSettings.DefaultAgent` (the plugin's settings, written through `UpdateSettingsAsync`) names the agent a click on the New ACP chat
  button starts. Without a usable default the click is as before: the only agent, or the picker. A default whose agent was removed is kept and
  ignored (the settings say so), so adding it back restores it. Right-click lists every agent in order with the default marked, and ACP settings;
  on a touch screen a tap opens that menu. This is the split of the Claude Code launcher.
- Custom agents: command, arguments, environment and working directory typed in the settings page; adding one is the confirmation.
- Credentials: SharpRail never shows a field for them. An agent that needs sign-in fails the session with a notice naming its
  `authMethods`; the user signs in with the agent's own tool.

## Not yet supported

- Unsaved editor buffers: `fs/read_text_file` and `fs/write_text_file` read and write the files on disk (confined to the workspace); the host
  half has no access to the editor's document model, so unsaved buffers are not seen and writes do not reach open editors as diffs. Tool
  diffs are shown on the tool card as text.
- The terminal service: agent terminals are plain child processes owned by the chat, not terminal tabs, and are not in the owned-process
  registry (that registry is keyed by a terminal tab).
- `authenticate` as buttons or links in the chat: the method is implemented in the client but the chat contract has no authentication call, so it
  is only listed in a notice. Sign-in in the settings page is not offered.
- The `preview` channel, the registry's `extensions` and per-agent `website` links are not used.
- Permission option labels other than the agent's "always" label (the contract has no option list), a plan record (plans are text), embedded
  resource, audio and resource-link prompts from the composer (images and file attachments are sent when the agent allows them), images in
  agent output, `session/list` and `session/fork`, the agent's `mcpServers` (none are passed), steering or queuing a message while a turn
  runs (the send is refused until the turn ends), effort levels, and a headless UI check of the chat view with streamed ACP output (the shared
  view is covered by `--claude-chat-ui`; the ACP folding is covered by `--acp`).
- Unstable ACP methods and extensions.

## Checks

`--acp`: the manifest and Early support label; framing, version negotiation, session new, prompt streaming, cancel, load and crash against the
in-process fake agent (`tests/SharpRail.Checks/AcpFakeAgent.cs`); the folded transcript; permission answers and cancellation; fs and terminal
methods and their confinement (including a symbolic link and `..`); registry parsing against the fixture, the cache, offline fallback and
seed; registry icons (url rules, unsafe SVGs, size cap, cache and offline, the generic glyph); the row model and search; the install confirmation facts; the launcher's default, removed default and menu; install confirmation, version mismatch, checksum failure and unpacking; the settings page's text forms. Checks never launch a real agent
or download from the registry.
