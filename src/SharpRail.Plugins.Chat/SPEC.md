# Chat (shared contract and view)

The conversation records and the drawn chat view that an agent plugin's chat mode uses. It has no plugin of its own: each agent plugin
serves the wire contract from its host half and registers its own editor document for the view. The Claude Code plugin is the only
one today; the seams below are what a second agent protocol (the Agent Client Protocol, ACP, is the intended one) plugs into.

## Boundary

- `SharpRail.Plugins.Chat` owns `ChatContract` (items, header, push kinds, the methods and the `chatUpdates` state channel) and `Ack`.
  It depends only on the Plugin API. A plugin lists `ChatContract.Methods` and `ChatContract.Updates` in its own `PluginContract`, so
  every method keeps the plugin's id as its namespace on the wire.
- `SharpRail.Plugins.Chat.UI` owns `ChatView`, the transcript list, composer, pickers and dialogs (internal; visible to the Claude Code
  UI project and the checks), `ChatDocuments` (registering a plugin's chat document, opening chats) and `ChatAgent`.
- A `ChatAgent` is how a plugin describes itself to the view: display and short name, document scheme, icon, the terminal agent kind (so a
  session a terminal already runs is not resumed in a chat), a help link, the end of the "host has no chat handler" message, effort levels,
  and the permission modes it offers with their words (`Modes`, `DescribeMode`). `ChatMode.Dangerous` marks a mode offered only while the
  session already runs in it.
- Allowed dependencies: Chat -> Api; Chat.UI -> Chat, Api.UI, Kit, Agent.UI. Plugins depend on Chat (and their UI on Chat.UI); the host
  core, UI, Android, Browser and the checks reach it through them. The host never depends on Chat.UI.
- The launcher (which presets start a chat, the resume menu) stays in the plugin: it knows the agent's command line, the view does not.

## Semantics

The semantics of the records and the view are those of the Claude Code chat (`src/SharpRail.Plugins.ClaudeCode/SPEC.md`, "Chat mode"
sections). A host that cannot do something the contract offers (queue editing, send-now on a queued message, rewind, fork, permission requests) answers that method with
an error and never produces the items that would make the view offer it: without the header flag `CanSendQueuedNow` there is no Send now button, with no message `Uuid` there are no rewind or fork buttons, and
with no permission or question items there is nothing to approve. The view's wording is neutral where it cannot know the agent
("Assistant"); anything else comes from the `ChatAgent`.

## Mention completion

`Files` searches the workspace for an `@` query. `CompletePath` (`ChatPathParams`: workspace, chat, the text as typed) lists one directory for a path query (`/`, `~`, `./`, `../`, a drive): directories first, then files, filtered by the last segment, at most 50, resolved against the chat's working directory and the host user's home; a missing directory is an empty list. A host that cannot serve it fails the call and the view uses `Files`. The method is additive, so the host protocol version does not change.

## Shared draft

The composer's unsent text is host state, so one user on several clients (a desktop and a phone) sees and edits the same draft. It is
text only: no carets, selections or typing indicators are shared, and two people are never assumed to type at once. Attachments are not
shared: they may be bytes held in the client's memory (`ChatAttachment.Data`, up to 8 MB), so each client keeps its own and the draft
record's `Attachments` stays null (reserved for path-only attachments later).

- Record. `ChatDraft(Text, Revision)` with `Attachments` (reserved, null) and `Origin`. `Revision` is a monotonic long per chat that
  never goes back, also across a clear and a host restart; revision 0 is "the host has never stored a draft for this chat".
- Reading. The `chatSnapshot` list starts with a `ChatDraftPush` (kind `draft`: workspace, chat, draft), also for a chat whose child is not running; later changes
  arrive on `chatUpdates` as the same push. A host that does not serve the draft answers `chatSetDraft` with an error and never pushes one;
  the view then keeps its draft client-locally (a static per-process map) and stops syncing.
- Writing. `chatSetDraft(ChatSetDraftParams: workspace, chat, BaseRevision, Text, ClientId)` returns the new `ChatDraft`, whose `Origin`
  is the `ClientId`. Today the host is last-writer-wins on the whole text: it ignores `BaseRevision` for ordering, assigns the next
  revision and broadcasts, unless the text equals the current one, when it changes nothing and returns the current record. Text over
  100,000 characters is refused. A client ignores a push whose `Origin` is its own id (the reply already told it the revision) and a
  push whose revision is not newer than the highest it knows.
- Reserved extension (not implemented). Host-ordered edit operations replace last-writer-wins without changing clients that already send
  `BaseRevision` and read `Revision`: `ChatSetDraftParams` gains an optional `Edit` (`replace range [Start, End) with Text, against
  BaseRevision`); the host transforms it over the edits applied since `BaseRevision`, applies it, and the push carries the edit with the
  new revision. A client that sends whole `Text` keeps working because the host treats it as one replace of the whole range.
- Persistence. The Claude Code host keeps the drafts in the plugin's user state (see the Claude Code SPEC), bounded and saved after a short
  debounce; a client reload, another device or a host restart shows the draft again. It is cleared when the message is sent and when the
  chat's workspace is removed.
- Send. `ChatSendParams` carries `DraftRevision` and `ClientId`. The host claims the revision atomically: a send whose `DraftRevision` is
  at or below the revision a previous send already consumed is refused with `ChatContract.AlreadySent` and sends nothing; otherwise it
  sends what the client typed (also from a stale revision), and then clears the draft and pushes an empty draft with a new revision. A
  send that fails releases the claim and keeps the draft. A send without `DraftRevision` (a launcher prompt, a queued edit) neither
  checks nor clears.
- Client apply rule. The client sends its text about 250 ms after the last keystroke, on focus loss, on detaching and before a send, with
  the async request, never on the UI thread's I/O. A remote draft replaces the input only when the input equals the text the client last
  sent or applied and nothing is in flight; the caret keeps its logical place (at the end stays at the end). Unsent or in-flight local text
  wins for that moment and is sent again as the next revision. While the input has focus and the user typed within the last 400 ms the
  replacement waits (the text box exposes no composition state cheaply), then applies only if the input is still clean. History recall
  (Up/Down) and its stash make the input differ from the last sent text, so a remote draft never replaces a recalled prompt. The view
  shows a quiet note under the input when a send is refused as already sent.

## Serving another agent protocol (ACP)

An ACP agent needs no view change: its plugin supplies a `ChatAgent` and a host half that serves `ChatContract` over the ACP connection
(stdio JSON-RPC to the agent process). The mapping:

| ACP | ChatContract |
| --- | --- |
| `initialize`, `session/new`, `session/load` | `Open` (new, resume by `ResumeSessionId`); `Sessions` lists loadable sessions; capabilities decide which items and methods exist |
| `session/prompt` turn | `Send`; `ChatStatus` Running for the turn, Idle on its stop reason (`end_turn`), a Failure notice for `refusal` or an error; `session/cancel` is `Interrupt` |
| `agent_message_chunk` | a `ChatAssistantItem` with `Streaming` growing in place by id, then final |
| `agent_thought_chunk` | `ChatThinkingItem` |
| `user_message_chunk` (replay on load) | `ChatUserItem` |
| `tool_call` / `tool_call_update` | `ChatToolItem` keyed by `toolCallId`: `kind` and `title` give `Name` and `Summary`, `status` maps to `ChatToolState`, content and locations to `Output` and `FilePath`; `ParentId` nests nothing unless the agent reports a parent |
| `session/request_permission` | `ChatPermissionItem` (options become the answers, `RequestId` the JSON-RPC id), answered with `Answer`; the call stays open until answered |
| `plan` | an assistant item or a notice; the contract has no plan record |
| `available_commands_update` | `ChatHeader.Commands` |
| session modes and `current_mode_update` | `ChatHeader.PermissionModes` and `PermissionMode`, set by `SetPermissionMode`; the words come from the agent's `ChatAgent.DescribeMode` or from the mode names the agent sends |
| session models (config options) | `ChatHeader.Models` and `Model`, set by `SetModel` |
| `usage_update` | `ChatHeader.Usage` |

The ACP client methods the agent calls (`fs/read_text_file`, `fs/write_text_file`, `terminal/*`) are served by the host half from the
workspace, never by the view.

### ChatContract gaps for ACP

- Permission options: `ChatPermissionItem` has allow, deny and "always" answers with destinations (Claude's settings files). ACP offers
  an arbitrary list of options (`allow_once`, `allow_always`, `reject_once`, `reject_always`, each with its own id and label). The
  host half can map `allow_once` and `allow_always` onto the two answers and the rejects onto Denied, but the labels are lost until
  the item carries a list of `{id, label, kind}` options.
- Plans: no record for an agent's task plan with entry status; today it is text.
- Content blocks: user and tool content are text, images and documents (`ChatAttachment`); ACP resource links, embedded resources and
  audio have no record. Tool diffs (`Diff` content) reach the card only as `FilePath`, not as old and new text.
- Tool kinds: tool cards pick their layout by Claude Code tool name (Bash, Edit, Read, Grep, ExitPlanMode...). An ACP tool has a `kind`
  (read, edit, execute, search, fetch...) but its own title; the card falls back to the generic layout until the kind drives it.
- Header fields that only Claude Code has (`Chrome`, `ChatQuestionItem`'s AskUserQuestion shape, rewind and fork by message uuid,
  `StopTask`) are simply never set by another host; ACP has no equivalent of rewind or fork except `session/fork` where offered.
- Session list: `ChatSessionInfo` has id, title and time, which fits `session/list`; paging and `cwd` filtering are not modelled.
- Model and mode discovery assume the header carries the lists; ACP's session config options (with categories and values) would need
  the host half to flatten them into models, effort and modes.
- Authentication (`authenticate`, agent auth methods) has no contract method; a host half would surface it as a Failure notice.

## Checks

The view is covered by `--claude-chat-ui` and `--claude-chat-e2e` (two views of one host for the shared draft), the records and the Claude Code host by `--claude-chat-host`, and the
project and dependency rules by `--conformance` (the boundary table).
