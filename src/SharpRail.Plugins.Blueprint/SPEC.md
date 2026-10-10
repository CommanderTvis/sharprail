---
id: module-plugin-blueprint
type: module-design
status: active
title: Blueprint — the interactive spec, its author and change reactor
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit, module-plugin-spec-dialect]
tags: [v1, plugins, blueprint]
---

## Goal

Port the fork's `packages/plugin-blueprint` at `b2a6c75a6`. A blueprint is a Markdown
file at the workspace root, `BLUEPRINT.md`, whose real design decisions render as
editable controls. Its author is an interactive Claude Code or Codex terminal beside the
document. The host writes reader changes and tells that author to reconcile the rest.
Pi and bundled AI chat remain outside SharpRail's scope. Their contract vocabulary
can be read from old records, but authoring starts a visible Claude Code or Codex terminal.

## Boundary

The contract assembly owns the manifest, eight methods, keyed channel and document
records. The host owns parsing, serialization, reconciliation, prompts, records,
file changes and `blueprint_check`. The UI owns the store, author opening, start
dialog, file redirect and companion. Each half references only its plugin API entry,
the UI kit where needed and Spec Dialect's contract. Neither half imports app or host
implementation projects. The app and host register their respective builtin halves.

## Document format

The parser reads optional leading YAML frontmatter and alternating prose/control
blocks. `!control select stable-id` accepts `= Selected — reason` and `- Alternative —
reason`. `!control multi stable-id` accepts `[x] Selected — reason` and `[ ] Alternative
— reason`. IDs slug their question or label; duplicates receive numeric suffixes.
The kind can be inferred for legacy markers. Missing/unknown IDs, kinds and reasons
produce the fork's diagnostic notes. Incomplete syntax while the author is streaming
is kept pending rather than exposed as malformed prose. Serialization and block line
spans derive from the same rendered block representation.

The author instructions retain the fork's exact appendix, source-specific opening
and reconciliation prompts. They ask for registered spec metadata, the project's
spec structure, explicit decisions, rejected alternatives, rendered Markdown/Mermaid
and `blueprint_check` after every rewrite.

## Host contract

| Method | Behavior |
| --- | --- |
| `open` | Resolve a known workspace and source; return its state, opening prompt and system appendix. Existing files use the existing-blueprint opening. |
| `setAuthor` | Persist the terminal author and publish the current file. |
| `get` | Restore a persisted record or an existing file; return the same payload shape as the state channel. |
| `select` | Apply a single choice or checkbox toggle, write the file, lock the choice and deliver reconciliation. |
| `edit` | Stage a passage, property block, option label or option reason without writing. Keep the original value across repeated edits. |
| `confirmEdits` | Write the staged document and deliver one reconciliation prompt for its text edits. |
| `discardEdits` | Drop staged text and reread the file. |
| `close` | Forget its live/persisted tracking, leaving the file in place. |

`changed` is a state channel keyed by `workspaceId`, with `get` as its snapshot.
Nullable `state` and `author` are explicitly serialized even when null, because the
fork requires those fields and the strict plugin serializer respects constructor
requirements. All polymorphic sources, authors, edit targets, document blocks and
change records cross the generic plugin RPC as their declared `kind`.

Sources are an idea (nonblank trimmed brief), this project's existing code, or a
document inside the workspace. A takeover document must exist and is stored as a
relative path. Existing `BLUEPRINT.md` avoids revalidating an obsolete source path.

Records under the plugin state namespace persist source, agent, terminal author and
reported agent session. They survive host restarts. Closed records suppress automatic
restoration. The host watches the workspace, rereads the file on matching/truncated
filesystem events and reports differences from the last published document. Reader
choices stay locked through rewrites, reinserting a dropped selected option as needed.

Selecting and confirming deliver exactly the reconciliation text plus Return through
the host's terminal write capability. Delivery does not require an attached client.
Agent-session events update only the matching author terminal. Its revival hook
contributes `submit: true`, composed with the first text offered by another plugin.

`blueprint_check` reads and reports the rendered controls and parser notes. It also
calls Spec Dialect's graph through its declared dependency and warns if `BLUEPRINT.md`
has not been indexed with id/type/title metadata. It changes nothing.

## UI

Two start actions open one dialog: a workspace action resolves its project, and a
project action works from Project Home. Sources are An idea, This project and A
document. Changing sources retains the idea text and selected document. Cancelling
the document picker preserves any previous selection. Remote document selection asks
for a file on the host, with file-specific title, explanation and confirm action.
The author selector offers registered `claude` and `codex` launchers; Claude Code is
the default when both are available, and Codex is selected when it is the only launcher.
The selected agent chip comes from its launcher's label and icon factory. It is absent
when no launcher is registered and updates its disabled state
and reason when the launcher invalidates. Removing and restoring a launcher mounts a
fresh icon; availability changes retain it. Pi/chat is excluded. The chosen agent is
persisted and used for fresh recovery and exact recorded-session resumption; recovery
never sends a Codex session to Claude or vice versa. Entering the project's Default
workspace and checking for an existing
blueprint precede starting a new author. The start button reads `Draft it` for an idea,
`Take it over` for the project or a document, and `Starting…` while it runs. A refused
start, such as a document outside the project, is a `Could not start the blueprint`
notification naming the host's reason, and the dialog stays open. If a session exists, Draft reopens that author
and keeps its original source, ignoring a newly entered idea or document selection.
The workspace action resolves a linked worktree's project and enters that project's
Default workspace; it does not create a separate Blueprint session in the linked worktree.

Opening `BLUEPRINT.md` redirects to its author/companion. Restored detached viewer tabs
close and redirect too. Existing catalog terminals are opened visibly; missing ones
resume only through a recorded agent session and the launcher's resume command. If
there is no recorded session, start a fresh visible author of the recorded agent with
the existing-file instructions. A missing launcher reports which plugin to enable. Never guess a `--continue` session or silently show a detached viewer.
Raw source is an explicit action inside the companion.

The companion hydrates the keyed state, follows file changes and reports reader
selections through editor events. It renders selectable Markdown, editable properties,
select dropdowns and checkbox controls. Single choices expose the selected option's
label/reason; multiple choices expose all option labels/reasons. Locked choices show
the lock mark. Passage edits commit on blur or Ctrl/Cmd+Enter, and Escape abandons the
draft. Text remains staged until Confirm edits; Revert returns to disk.

Properties support text, sequence and one-level mapping values, renamed keys, added
and removed properties/entries and shape conversions. Duplicate/blank keys are refused.
Unsupported YAML shapes remain read-only. The outline navigates prose headings.
Changes have dismissible notices, highlighted controls and links to surviving controls.
Loading, absent and awaiting-author states remain distinct.

Only registered workspaces are followed and queried for spec links. Terminals can
belong to other directories, such as the home directory; those do not trigger
Blueprint subscriptions, filesystem watches or graph requests. Registering a
workspace later starts following its existing terminals. A graph request completing
after its workspace is removed is ignored, including its error.

The core keeps the mounted companion while its selected registration remains the
same. Publishing state must not recreate the pane or interrupt a draft.

## Verification

`BlueprintChecks` translates format, reconciliation and session cases from the fork,
uses the real plugin runtime, exercises host-side delivery and reported session IDs,
and checks generic gRPC parity. Its headless UI check opens the file through the
redirect, edits and confirms prose, toggles a checkbox, opens raw source and verifies
the companion's identity, then rewrites the file on disk as a terminal author would and
waits for the mounted pane to show each rewrite through the host's watcher alone. Start-dialog coverage exercises remote document selection,
source switching and cancellation, then Draft from Project Home into the Default
workspace with launcher prompts, a recorded terminal author and a visible companion. It also
refuses an outside document with the notification above, and a local check turns the real Claude
Code plugin on and off with the dialog open to show its author chip appearing and leaving.
Recovery coverage through the in-process host and a real gRPC host checks exact
recorded-session launch options, fresh recovery without a session, companion visibility,
preserved documents and reuse of the author on reopening. A launcher failure reports its actual
error and releases the pending opening so a second file-open can retry exactly once.
Codex coverage checks the author choice, opening prompts, persisted identity, reader-edit
delivery and fresh/recorded-session recovery through local and gRPC hosts.
Run with `--blueprint`; the plugin and full runners also include these checks.
Remaining acceptance gaps are tracked in `COMPLETION.md`.
