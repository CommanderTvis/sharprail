# Pi builtin

Early support for the Pi coding agent (https://github.com/badlogic/pi-mono), shown as a chat tab in the shared chat view
(`src/SharpRail.Plugins.Chat/SPEC.md`). Disabled by default; the manifest declares `Support = early`. Pi runs as a child
process of the host, so a browser tab reaches it only through a host. SharpRail never installs Pi:
it runs the `pi` the host finds on PATH (or the configured command).

Facts below come from Pi's RPC documentation (`docs/rpc.md`, `docs/providers.md`, `docs/session-format.md` in `@mariozechner/pi-coding-agent`
0.73.1). They are verified by the checks against a fixture `pi` that speaks the protocol, not against a real Pi run.

## Acceptance contract

- Process: a chat's child is `pi --mode rpc [--session <file>] [--model <pattern>]` in the workspace directory, with the host's
  environment. Commands go to stdin and events and responses come back on stdout as LF-delimited JSON; stdout is read by line (never split on
  Unicode separators). Closing stdin, then killing the process tree, ends it. Stderr is kept (the last lines) to explain an exit.
- Start: `get_state` (model, thinking level, session file and id, name), then `set_thinking_level` when one was chosen, `get_available_models`
  and `get_commands`; a resumed session then `get_messages`, replayed into the same items a live turn folds to. A missing `pi` fails the chat
  with an install hint; each response has a 30 s bound.
- Folding (`PiFolder`): `agent_start`/`agent_end` are Running/Idle; a user `message_start` is the prompt (it releases a message the host showed
  as queued); assistant text and thinking blocks are items keyed by message and content index and grow with their deltas; a tool call is an item
  keyed by Pi's tool call id, Pending at `toolcall_end`, Running through `tool_execution_*` (the update's accumulated output replaces the last) and
  Done or Failed at the end; output over `ChatContract.OutputLimit` is cut and served whole as item detail; `edit` and `write` carry the file path.
  Usage and cost accumulate in the header; compaction shows as the header's activity; retries and extension errors become notices.
- Sending while a turn runs sends `prompt` with `streamingBehavior: steer` and shows the message queued until Pi echoes it. Stop is `abort`. The
  model (`provider/id`) and thinking level changes are confirmed by Pi before the header shows them.
- Not offered: permissions and questions (Pi has none; the one permission mode says its tools run freely), rewind, fork, queue editing and
  background tasks (their methods answer an error). An extension dialog (`select`, `confirm`, `input`, `editor`) is answered as cancelled with a
  notice so Pi never waits; `notify` becomes a notice.
- Sessions: Pi stores sessions under `~/.pi/agent/sessions/--<path with / as ->--/` (or `PI_CODING_AGENT_DIR`). The sessions list shows that
  directory's files newest first, titled by the session's name or first prompt; opening one resumes it with `--session`. The host remembers each
  chat's session file so reopening its tab continues it.
- Launcher: "Pi" in the agent launchers always opens a chat tab (there is no terminal mode yet); Settings › Pi has the command, a default model and
  a thinking level, and the version read from `pi --version`. Model providers and keys are managed in Settings › Model providers.

## Not yet

A terminal launch of Pi, permission-style gating, rewind and fork through Pi's `fork` and session tree, extension dialogs, MCP and SharpRail's
tools for Pi, and running Pi inside a browser tab.

## Checks

`--pi` (gate case `pi`): the manifest, the folding of recorded events and stored messages, a session against a fixture `pi` (start, state, models,
commands, a streamed turn, model and thinking changes, close, a missing binary, the version probe) and the sessions list.
