---
id: submodule-host-language-refactoring
type: submodule-design
status: draft
title: Code actions and refactoring through language servers
parent: module-host-language
---

# Code actions and refactoring through language servers

Status: design only except the editor's quick fixes (Alt+Enter, documented in the parent spec and in the editor spec),
which advertise `textDocument.codeAction` with literal, resolve, disabled and preferred support, `workspace.applyEdit`
and `workspaceEdit.documentChanges`, and apply edits only from the editor. The survey below predates it and is otherwise
current. Nothing else here is implemented; the shipped state is under "Survey". Semantic refactorings come
from a language server or IDE engine, never from textual replace. A missing capability is reported, not emulated.

## Survey of what exists (2026-10-27)

- MCP tools: `lsp_diagnostics`, `lsp_navigate`, `lsp_status` (`LanguageMcpTools`,
  `src/SharpRail.Host.Core/LanguageServices.cs`). All read-only; see "Inspection does not apply code actions"
  in the parent spec.
- Session requests (`LanguageSession.cs`): `textDocument/` hover, definition, typeDefinition, implementation,
  references, documentSymbol, semanticTokens/full, diagnostic, `workspace/symbol`, `workspace/diagnostic`, and
  `workspace/executeCommand` only for the Java and IntelliJ decompile hooks (`SupportsCommand`).
- Client capabilities advertised in `initialize`: `general.positionEncodings` utf-16; `window.workDoneProgress`;
  `workspace` folders, configuration, `didChangeWatchedFiles`; `textDocument` references (dynamic), synchronization
  `didSave`, publishDiagnostics, pull diagnostic, definition/typeDefinition/implementation links, hierarchical
  documentSymbol, semanticTokens. Not advertised: `codeAction`, `rename`/`prepareRename`, `formatting`,
  `completion`, `signatureHelp`, `workspace.applyEdit`, `workspace.workspaceEdit`, `workspace.executeCommand`,
  `fileOperations` (`willRenameFiles`), `workDoneProgress` cancel for refactorings, `window.showDocument`.
- Server-to-client requests (`LanguageConnection.cs`): `workspace/applyEdit` is answered `applied=false` ("Use the
  editor to apply changes"). Everything else listed there is configuration, folders, registration or progress.
- Editor (`SharpRail.UI/Editor/CodeDocumentView.Language.cs`, `src/SharpRail.Scintilla`): diagnostics squiggles,
  semantic colors, a context menu and chords for Go to Declaration (F12), Type Declaration, Implementation, Find
  Usages (Alt+F7, Shift+F12), document and workspace symbols, hover via `inspect`. No completion, signature help,
  rename, code actions, formatting or organize imports. Scintilla is a managed port with its own undo (`Editor`);
  an edit applied by the host must enter that undo stack as one group.
- Plugin hooks (`LanguageServerSpec`, `src/SharpRail.Plugins.Api.Host/LanguageServerTypes.cs`) declare identity,
  launch, options and adaptations, but nothing about refactorings.

## Capability matrix

Cells: N native LSP request (rename, prepareRename), A code action (kind in the title row of the key below),
C custom command or custom request reached through a code action's `command`, - not available. `?` marks a claim
from memory, not from a document I read this round; verify against the pinned server version before relying on it.

| Refactoring | Roslyn (csharp) | typescript-language-server | clangd | gopls | pyright | bash-language-server | Kotlin LSP | IntelliJ LSP | jdtls (java) |
|---|---|---|---|---|---|---|---|---|---|
| Rename + prepareRename | N | N ? | N (no comments/macros/templates [1]) | N | N | N ? | N [2] | N ? | N |
| Optional strings/comments | - | - | - | - | - | - | - | ? | - |
| Change signature | A/C ? | A `refactor.rewrite` via tsserver ? | - | A `refactor.rewrite.moveParam*`/`removeUnusedParam` plus rename-on-func [3] | - | - | - ? | ? | A ? |
| Extract method/function | A `refactor.extract` ? | A via `_typescript.applyRefactoring` [4] | A `ExtractFunction` tweak ? | A `refactor.extract.function`/`.method` [3] | - | - | - ? | ? | A `refactor.extract.function` ? |
| Extract variable/constant | A ? | A [4] | A `ExtractVariable` ? | A `.variable`, `.variable-all`, `.constant` [3] | - | - | - ? | ? | A ? |
| Inline method/variable | A ? | - ? | A `DefineInline` (different meaning) ? | A `refactor.inline.call`/`.variable` [3] | - | - | - ? | ? | A `refactor.inline` ? |
| Pull up / push down / extract superclass / interface | A ? | - | - | - (planned extract interface [3]) | - | - | - ? | ? | A ? (jdtls pull-up/push-down/extract interface via commands) |
| Change visibility, make static | A ? | - | - | - | - | - | - ? | ? | A ? |
| Safe delete / find unused | diagnostics + A "remove unused" ? | A `source.removeUnused.ts` [4] | - (clang-tidy only) | A `quickfix` unused param/var [3] | - | - | - | ? | - |
| Type migration / change type | - | - | `ExpandAutoType` | - | - | - | - | ? | - |
| Convert anonymous class/lambda, param object, ctor to factory, encapsulate field | A for some ? | - | - | - (param struct planned [3]) | - | - | - ? | ? | A some ? |
| Organize imports | A `source.organizeImports` ? | A `source.organizeImports.ts` and `_typescript.organizeImports` [4] | A include cleaner ? | A `source.organizeImports` [3] | A `source.organizeImports` ? | - | Supported [2] | ? | A `source.organizeImports` |
| Move file / update imports (`willRenameFiles`) | - ? | C `_typescript.applyRenameFile` [4] | - | N ? | - | - | - | ? | N ? |

Sources: [1] clangd features, https://clangd.llvm.org/features ; [2] Kotlin LSP README,
https://github.com/Kotlin/kotlin-lsp (lists rename and organize imports; says nothing on other refactorings or
`executeCommand`); [3] gopls transformation doc,
https://github.com/golang/tools/blob/master/gopls/doc/features/transformation.md (fetched; kinds
`source.organizeImports`, `refactor.extract.function|method|variable|variable-all|constant|toNewFile`,
`refactor.inline.call|variable`, `refactor.rewrite.*` incl. `moveParamLeft|Right`, `removeUnusedParam`, `fillStruct`,
`implementInterface`; rename on a `func` token reorders parameters); [4] typescript-language-server README,
https://github.com/typescript-language-server/typescript-language-server (fetched; kinds `source.organizeImports.ts`,
`source.removeUnused.ts`, `source.addMissingImports.ts`, `source.fixAll.ts`; commands `_typescript.applyRefactoring`,
`_typescript.organizeImports`, `_typescript.applyRenameFile`). tsserver refactor names (Extract Symbol, Move to file,
Convert function/arrow/parameters to object, Inline variable) reach the client as `refactor.extract.*`,
`refactor.move.*`, `refactor.rewrite.*`, `refactor.inline.*` actions: unverified for the pinned 6.0.2.
Roslyn, IntelliJ standalone LSP and jdtls rows are entirely unverified: a search this round found no Roslyn LSP
document, and the IntelliJ server's published capabilities were not fetched. The first implementation step is an
empirical probe (below), which replaces `?` cells with recorded `initialize` results. Rust (rust-analyzer),
Dart, Swift (sourcekit-lsp), Scala (Metals), PHP, Ruby, R, PowerShell and CSS plugins are not researched; they
are discovered at runtime only.

The matrix is documentation, not behavior: the host never reads it. Availability at runtime comes only from the
live server (see "Capability discovery").

## Capability discovery

The host advertises the client capabilities these features need (phase 1: `textDocument.codeAction` with
`codeActionLiteralSupport` for kinds `quickfix`, `refactor`, `refactor.extract`, `refactor.inline`,
`refactor.rewrite`, `source`, `source.organizeImports`, plus `resolveSupport` for `edit` and `disabledSupport`,
`isPreferredSupport`; `textDocument.rename` with `prepareSupport`; `workspace.applyEdit`; `workspace.workspaceEdit`
with `documentChanges`, `resourceOperations` create/rename/delete, `failureHandling` `transactional`,
`normalizesLineEndings`; `workspace.executeCommand`; `workspace.fileOperations.willRename`). Every server session
records the server's `codeActionProvider.codeActionKinds`, `renameProvider.prepareProvider`,
`executeCommandProvider.commands` and dynamic registrations, as it already does for other providers.

`lsp_status` operation `servers` gains per server `refactorings`: `rename` (`native`, `unavailable`), `codeActionKinds`
(the advertised list, empty if none), `commands` (count, names on request), and `organizeImports` (`ready` when
the kind is advertised). Availability of an individual action is only known per position, so the tool-level answer
is "kinds advertised"; the per-position answer is `lsp_code_actions`. The Code Intelligence pane shows the same
line in a server's details.

A plugin may declare `Refactorings` on its `LanguageServerSpec`: `KindAliases` (maps server titles or custom
command names onto the normalized refactoring ids below, e.g. `_typescript.applyRefactoring` arguments to
`extract.function`), `Commands` it needs the host to allow through `executeCommand`, and `Unsupported` ids it knows
the server cannot do (so the error can name an alternative). The hook is optional; a server without it still lists
and applies its generic code actions.

### Normalized refactoring ids

`rename`, `changeSignature`, `extractMethod`, `extractVariable`, `extractConstant`, `extractField`,
`extractParameter`, `inlineMethod`, `inlineVariable`, `pullUp`, `pushDown`, `extractSuperclass`, `extractInterface`,
`changeVisibility`, `makeStatic`, `safeDelete`, `changeType`, `convertAnonymous`, `introduceParameterObject`,
`replaceConstructorWithFactory`, `encapsulateField`, `organizeImports`, `moveSymbol`, `moveFile`.
An action maps to an id by LSP kind (`refactor.extract.function|method` to `extractMethod`, `.variable` to
`extractVariable`, `.constant` to `extractConstant`, `refactor.inline.*`, `source.organizeImports`) and otherwise
by the plugin's `KindAliases`; an unmapped action keeps `id` empty and is still listed and applicable by `actionId`.

## MCP tools

Same conventions as the `lsp_*` tools: an explicit `workspace` (absolute), symbol `query` or `path` plus
`line`/`column` (zero-based), servers started on demand with the 60 second wait and the 90 second deadline,
results with `state`, `freshness`, `coverage`, `detail`, `hint`, paged lists, and the 28,000 character item budget.
Tool names: `lsp_code_actions`, `lsp_apply_action`, `lsp_rename`, `lsp_organize_imports`, `lsp_refactor`.
(Five tools, because arguments and failure modes differ; all five are registered together so the model sees one
family. `lsp_status` stays the lifecycle tool.)

### lsp_code_actions (read-only)

Input: `workspace`; target as `query` (symbol name as in `lsp_navigate`) or `path` with a position or range
(`line`/`column`/`endLine`/`endColumn`) or `path` alone (file-level `source.*` actions); optional `kinds` filter
(prefix match, e.g. `refactor.extract`), `onlyRefactorings` (default false: includes quickfixes for diagnostics in
range), `limit`/`offset`. Output per action: `actionId` (opaque, valid for this document version, 10 minutes,
invalidated by any edit to the file or restart), `title`, `kind`, `refactoring` (normalized id or empty),
`server`, `preferred`, `disabled` (reason string if the server marks it disabled), `needsInput` (names of
inputs the action requires, such as a new name; see `lsp_refactor`), `edit` summary when the server returned an
edit eagerly (files and counts, else resolved on apply). The server is sent `context.diagnostics` for the range and
`only` from `kinds`. A server without `codeActionProvider` yields `state: unsupported`, a `detail` naming the server
and alternative (below).

### lsp_apply_action

Input: `workspace`, `actionId`, `dryRun` (default true), optional `inputs` object for actions with `needsInput`.
Resolves the action (`codeAction/resolve` if `edit` is absent and `resolveSupport` allows), runs its `command`
through `workspace/executeCommand` when it has one (server-initiated `workspace/applyEdit` during the command is
captured into the same edit set, see "Applying a WorkspaceEdit"), and returns the edit summary. With
`dryRun: false` it applies the edit.

### lsp_rename

Input: `workspace`, symbol `query` or `path`+position, `newName`, `dryRun` (default true), optional
`includeStringsAndComments` (honored only by servers that expose it, otherwise answered with `warnings` that it
was ignored; never emulated by text search). Runs `prepareRename` when advertised (its range and placeholder
are returned; a refusal such as "cannot rename this element" is the error), then `textDocument/rename`.
Same-named overloads, shadowing and override hierarchies are the server's responsibility; the tool reports the
files and number of edits so a result touching unexpectedly many or few files is visible, and `ambiguous`
resolution works exactly as in `lsp_navigate` (never guesses a position).

### lsp_organize_imports

Input: `workspace`, `paths` (files), `dryRun` (default true). Per file requests code actions of kind
`source.organizeImports` (falls back to `source.removeUnusedImports`/`source.sortImports` only when the plugin
declares them; never silently picks a different kind) and applies or summarizes them. One result row per file.

### lsp_refactor

Typed entry for refactorings that need parameters; schema is a `refactoring` enum of the normalized ids plus the
target (`query` or `path`+range) and an `inputs` object whose shape depends on the id:

| id | inputs |
|---|---|
| `extractMethod` | `name`, optional `visibility`, `static` |
| `extractVariable`, `extractConstant`, `extractField`, `extractParameter` | `name`, optional `replaceAll` (maps to `...-all` kinds when available) |
| `inlineMethod`, `inlineVariable` | `keepDeclaration` (only when the server supports it) |
| `changeSignature` | `parameters` (ordered list of `{oldIndex|new:{name,type,default}, name?, type?, default?}`), `returnType?`, `name?`, `visibility?` |
| `pullUp`, `pushDown`, `extractSuperclass`, `extractInterface` | `members`, `target` or `name` |
| others | `name` or `type` as relevant |

`lsp_refactor` resolves `refactoring` + target to the matching code action(s) via the same mapping as
`lsp_code_actions`, then performs steps the server defines (`rename`, `executeCommand` with arguments, or a code
action whose title carries the input). If the server offers the refactoring only as an action with no input channel
(most servers; they pick the name themselves, e.g. gopls `newFunction`) the tool applies it and reports the
chosen name, then offers `lsp_rename` for the new symbol as the documented follow-up (in-place naming is a
rename after extract, as it is in the editor). If no matching action exists at that target, the result is
`unsupported` or `notApplicable` (selection invalid), never a partial emulation. Parameters the server cannot
honor are rejected before the request with a message naming them.

### Output shape (all apply-capable tools)

`state` (`ready`, `ambiguous`, `notFound`, `unsupported`, `notApplicable`, `conflict`, `failed`, `initializing`),
`dryRun`, `applied` (bool), `server`, `title`, `files` (up to 50 rows `{path, edits, created|renamed|deleted}`),
`totals` (`files`, `edits`, `insertedChars`, `deletedChars`), `diff` (unified diff, capped at 12,000 characters,
`truncated`), `warnings`, `undoToken` (applies only to the current session, see "Undo"), `hint`. A dry run changes
nothing, including open buffers and the file watcher.

### Dry-run default

Agents get `dryRun: true` by default. A refactoring touches many files at once and the usual failure is wrong
target resolution (overloads, same-named members); a diff summary is cheap and lets the agent confirm the file
list before writing. Apply is explicit (`dryRun: false`). A dry run still sends the real server request (the
server computes the edit), so the cost is a round trip, not a guess. The editor never uses dry-run semantics by
default: its in-place rename applies live (see editor section), with preview opt-in.

## Applying a WorkspaceEdit

One host component (`WorkspaceEditApplier`, host layer, no UI dependency) applies every edit from every source: tool
results, editor actions, and server-initiated `workspace/applyEdit` (which stops answering `applied=false`; it is
answered through this applier only while a refactoring operation owns it, and refused with the reason otherwise).

Contract:

1. Normalize `changes`, `documentChanges` (text edits, `CreateFile`, `RenameFile`, `DeleteFile`) and annotated edits
   into an ordered operation list. UTF-16 positions, overlapping edits within a file are an error (`failed`), not
   merged.
2. Version checks: a text document edit carrying a version is rejected with `conflict` unless it matches the
   document version the host last synchronized for that URI (stale after typing or a server's slow answer). Edits
   without version are validated against the current text hash the host sent. A request begun while a buffer was
   dirty is applied to that buffer.
3. Buffers take precedence. For a file open in an editor, the edit applies to the editor's buffer (not disk) as one
   undo group and leaves the document dirty; unopened files are written through the host file service (atomic
   replace, same path as editor saves) and notified to servers through the normal change channel. A file open and
   dirty is never overwritten on disk.
4. Atomicity: all validation (paths, versions, overlaps, existence of rename/delete sources and no overwrite of
   existing rename targets) runs first with no writes. Writes then proceed in operation order and record an
   inverse for each. Any failure rolls back completed operations in reverse using the recorded inverses (text
   restored byte-exact, created files deleted, renamed files renamed back, deleted files restored from the
   recorded bytes) and reports `failed` with the failing operation; rollback failure is reported loudly with the
   remaining paths and leaves a recovery note. Editor buffers are changed last and only after disk operations
   succeed, so a failed refactoring leaves no partial edits. Cancellation before the first write is free;
   after, the operation runs to completion or rolls back.
5. Safety: every path must be inside the workspace root (other worktrees of the project are refused);
   dependency URIs and virtual sources (jar:, jrt:, decompiled) reject edits ("Virtual sources reject edits" in the
   parent spec). Gitignored and vendor paths (`node_modules`, `bin`, `obj`, `.git`, generated trees the project
   language file excludes) are refused unless the edit is a server action on a file already open in an editor; the
   refusal lists the paths and the whole edit is not applied. Symbolic-link escapes are resolved and refused. `.git`
   and the host state directory are never written. Bounds: at most 500 files, 5 MiB of edit text total, 1 MiB per
   file edited, exceeded means `failed` before any write.
6. Line endings follow the target (normalize edit newlines to the file's dominant ending unless the server
   advertises it normalized them). File mode and BOM are preserved. File watchers: the applier registers its own
   writes so the watcher does not report them as external edits.
7. Undo: the host records one inverse record per applied edit set keyed by `undoToken`, kept for the session (in
   memory, last 20 per workspace). The editor's undo group undoes the open buffers; files not open undo through
   `Undo Refactoring` (editor and a future `lsp_undo`), which applies the inverse only if every touched file still
   hash-matches what the applier wrote, else reports `conflict` listing the files. Git is the durable undo; agents
   are told the tool is not a substitute for it.
8. After apply: documents are resynchronized (`didChange`/`didSave` flow), diagnostics are marked stale, and the
   result carries `freshness: stale` for diagnostics until the next scan.

Resource operations are applied only with `documentChanges` support; for `changes`-only servers file operations
cannot occur. `workspace/willRenameFiles`: when a Files tree rename or move is performed and a server advertises
`fileOperations.willRename` for the file, the host requests the edit, previews it in the same dialog, and applies
it in the same atomic set as the rename (phase 3).

## Editor UX (copy intellij-community ergonomics)

All editor entries are driven by the same host operations as the tools, run off the UI thread (AGENTS.md), show
progress with Cancel after 400 ms, and apply results only if the document version and workspace still match.
JetBrains references: Refactor This and refactoring dialogs
https://www.jetbrains.com/help/idea/refactoring-source-code.html ; Show Context Actions (Alt+Enter)
https://www.jetbrains.com/help/idea/intention-actions.html ; Rename
https://www.jetbrains.com/help/idea/rename-refactorings.html ; Change Signature
https://www.jetbrains.com/help/idea/change-signature.html ; Extract Method
https://www.jetbrains.com/help/idea/extract-method.html ; Inline
https://www.jetbrains.com/help/idea/inline.html ; Safe Delete
https://www.jetbrains.com/help/idea/safe-delete.html ; Optimize imports
https://www.jetbrains.com/help/idea/creating-and-optimizing-imports.html ; Reformat
https://www.jetbrains.com/help/idea/reformat-and-rearrange-code.html . (Only the first page was fetched; shortcut
values in the other pages are from memory.)

- Context actions popup (Alt+Enter, macOS Option+Enter): a list at the caret with quick fixes for diagnostics under
  the caret first, then refactorings, then `source.*`; a lightbulb gutter marker appears when `codeAction` returns
  items (requested on caret idle, debounced 250 ms, cancelled on move). Arrow keys, Enter, number hints, type to
  filter. The preferred action is first and bold. `disabled` actions are hidden unless a filter matches them,
  then shown dimmed with the reason.
- Refactor This (macOS Ctrl+T, Windows/Linux Ctrl+Alt+Shift+T): the same list restricted to `refactor.*` kinds,
  plus Rename, Change Signature, Safe Delete, Move when the server supports them, each with its chord shown.
- Rename (Shift+F6; also F2 as an alias): in-place. `prepareRename` selects the identifier, a template popup edits
  the new name live, all occurrences from the server's computed edit are highlighted and rewritten as typed only
  once Enter is pressed (the edit is computed once per Enter, not per key, because servers are not incremental),
  Esc cancels, Tab opens the dialog form with "Search in comments and strings" when the server exposes that option,
  Preview opens the preview dialog. Rename of a file-named symbol also offers renaming the file when the plugin
  declares it.
- Change Signature (Cmd+F6 / Ctrl+F6): dialog with a parameters table (name, type, default, move up/down, add,
  remove) driven by the signature from `hover`/outline; submit builds `lsp_refactor changeSignature` and, if the
  server has no such action, shows "gopls can only reorder or remove unused parameters" style text from the
  plugin's `Unsupported`/`KindAliases`.
- Extract Method / Variable / Constant / Field / Parameter (Cmd+Alt+M / V / C / F / P; Ctrl+Alt on Windows/Linux):
  the editor sends the selection range; on apply the new symbol is selected in place and the in-place Rename
  template opens on it (the JetBrains behavior), using `prepareRename` at the new declaration.
- Inline (Cmd+Alt+N / Ctrl+Alt+N). Safe Delete (Cmd+Delete / Alt+Delete): runs references first (existing
  `references`) and shows the usages list in the Find Usages view; deletion happens only when the server offers
  a delete action or there are no usages, otherwise the dialog shows "N usages" with View Usages and a disabled
  Delete; no textual delete of a declaration with usages. Move / Pull Up / Push Down: dialog or popup when the
  server offers the action, else hidden.
- Optimize Imports (Ctrl+Alt+O on all platforms): `source.organizeImports` for the file; the context menu and Code
  menu show it. Reformat Code (Cmd+Alt+L / Ctrl+Alt+L) belongs to `textDocument/formatting`, out of phase scope but
  listed in the key table so chords are reserved.
- Preview ("Preview" button in dialogs, or Alt+P in the rename template): reuse the diff viewer: a tree of files
  (checkboxes to exclude a file or hunk; exclusion of a hunk whose server edit is atomic is disabled with an explanation),
  an inline diff per file, "Do Refactor" and Cancel. Excluding edits is only offered when the server marks edits
  with change annotations `needsConfirmation` or the edit is a plain `changes` map; otherwise Preview is
  accept-all.
- Conflicts view: when the applier returns `conflict` (stale version, overlapping edit), a dialog lists the files and
  reasons with "Refactor Anyway" only for non-version conflicts that the user can retry from a fresh server
  answer; "Show in Find" opens them as a list. A server warning attached to the response (`disabled.reason`,
  message) is shown the same way.
- Undo as one step: all open-buffer edits apply as a single Scintilla undo group (begin/end undo action); Cmd+Z
  reverts all open files of the refactoring in one press, and closed-file changes through the Undo Refactoring
  item shown in a toast ("Renamed 14 usages in 6 files — Undo").
- Context menu: a "Refactor" submenu (Rename…, Change Signature…, Extract ▸, Inline…, Safe Delete…, Move…), "Show
  Context Actions", "Optimize Imports". Items are present only when the file's server advertises the capability
  (`codeActionProvider` kinds or `renameProvider`); unadvertised items are hidden, not disabled. With a server
  that starts only on demand, items appear after the first answer (matching the existing context-menu rule).
  A refactoring that the server advertises but declines at the caret is shown by the popup as nothing and the
  direct chord reports a status line "<server> cannot <refactoring> here".

Key bindings (primary = Cmd on macOS, Ctrl elsewhere):

| Action | macOS | Windows/Linux | Notes |
|---|---|---|---|
| Show Context Actions | Option+Enter | Alt+Enter | |
| Refactor This | Ctrl+T | Ctrl+Alt+Shift+T | |
| Rename | Shift+F6 (F2 alias) | Shift+F6 (F2 alias) | |
| Change Signature | Cmd+F6 | Ctrl+F6 | |
| Extract Method | Cmd+Option+M | Ctrl+Alt+M | |
| Extract Variable | Cmd+Option+V | Ctrl+Alt+V | |
| Extract Constant | Cmd+Option+C | Ctrl+Alt+C | |
| Extract Field / Parameter | Cmd+Option+F / P | Ctrl+Alt+F / P | |
| Inline | Cmd+Option+N | Ctrl+Alt+N | |
| Safe Delete | Cmd+Delete | Alt+Delete | |
| Move | F6 | F6 | |
| Optimize Imports | Ctrl+Option+O | Ctrl+Alt+O | |
| Reformat Code | Cmd+Option+L | Ctrl+Alt+L | reserved |

Deliberately not copied: the multi-caret preselection of members, Rename Variables dialog for similar names, the
suggestion of renaming related names, and inspection-based "Fix all in file" batch runs. IntelliJ's own
"Replace Constructor with Factory Method" and "Encapsulate Fields" dialogs are not replicated: they are offered
only as the server's code action titles in the popup. Chords are checked against `KeyMap.cs` and the application
menu at implementation time; a chord already bound (for example Ctrl+T on macOS) is resolved by the editor first
only when focus is in a code editor.

## Coverage of the requested refactorings

"Editor" is the entry; "Tool" is the MCP entry. Servers listed are those whose matrix cell is not `-`; cells marked
`?` there are unverified. "Unsupported" text is the result when the target server advertises none, shown in both
the editor status line and the tool `detail`.

| Refactoring | Tool | Editor | Servers (see matrix) | When unsupported |
|---|---|---|---|---|
| Rename (symbol, member, parameter, local) | `lsp_rename` | Shift+F6 | all with `renameProvider` | "`<server>` does not advertise rename. Use `lsp_navigate references` to review usages and edit by hand." |
| Optional strings/comments/config | `lsp_rename` `includeStringsAndComments` | rename dialog checkbox | only servers exposing the option | warning that the option was ignored; no text search fallback |
| Change signature | `lsp_refactor changeSignature` | Cmd+F6 | gopls (reorder/remove only), tsserver ?, Roslyn ?, jdtls ?, IntelliJ ? | "`<server>` cannot change signatures. Alternatives: `lsp_rename` for the name, `lsp_navigate references` for call sites." |
| Extract method/variable/constant | `lsp_refactor extract*` | Cmd+Alt+M/V/C | gopls, tsserver, clangd, Roslyn ?, jdtls ? | "no extract action at this selection" or unsupported with the server name |
| Inline method/variable | `lsp_refactor inline*` | Cmd+Alt+N | gopls, Roslyn ?, jdtls ? | as above |
| Pull up/push down/extract superclass/interface | `lsp_refactor` | Refactor This | Roslyn ?, jdtls ?, IntelliJ ? | hidden in editor; tool says unsupported |
| Change visibility, make static | `lsp_code_actions` + `lsp_apply_action` | Alt+Enter | Roslyn ?, jdtls ?, IntelliJ ? | not offered |
| Safe delete / find unused | `lsp_navigate references` then `lsp_apply_action` for an unused-removal quickfix | Cmd+Delete | tsserver, gopls, Roslyn ? | the usages list only; no delete without server action |
| Type migration / change type | `lsp_code_actions` | Alt+Enter | server quickfixes only | not offered; use rename plus signature change |
| Convert anonymous/lambda, parameter object, ctor to factory, encapsulate field | `lsp_code_actions` + `lsp_apply_action` | Alt+Enter / Refactor This | Roslyn ?, jdtls ?, IntelliJ ? | not offered |
| Organize imports | `lsp_organize_imports` | Ctrl+Alt+O | gopls, tsserver, pyright ?, jdtls, Roslyn ?, Kotlin LSP | "`<server>` has no organize imports action" |

## Failure reporting

Every failure names the server, the operation, and either the alternative (table above) or the next step. States:
`unsupported` (capability absent), `notApplicable` (server declined at that position, with its message),
`ambiguous` (candidates as `lsp_navigate`), `conflict` (stale version or overlap, retry with a fresh
`lsp_code_actions`), `failed` (server error, validation or rollback; the first error is quoted, bounded to 600
characters, and the result states that no file changed or lists exactly what could not be rolled back), `initializing`
(as for other tools). Server timeouts use the 90 second deadline and send `$/cancelRequest`. Source contents are
never logged (parent spec); logs carry method names, counts and outcomes.

## Implementation checklist

Phase 0, probe and plumbing (no behavior change):
1. Record every shipped server's `initialize` result `capabilities` (codeAction kinds, rename provider,
   executeCommand commands) in a fixture file and replace the `?` cells of the matrix with that data; keep the
   source version beside each. Done only on request of a live probe (profiling rules apply: one light measurement).
2. Add the client capabilities above to `initialize`. Servers that behave differently once advertised
   (Roslyn `workspace/applyEdit` flows, tsserver) are covered by the phase 1 checks; the change is gated by a host
   setting `refactorings` default on once checks pass.
3. Capability record per session and `refactorings` in `lsp_status` servers (wire change, additive; bump Protocol
   minor and keep older clients tolerant).

Phase 1, rename, organize imports, code actions:
4. `WorkspaceEditApplier` with all contract items, including rollback and bounds; check `--lsp-edit-apply`: atomic
   rollback on a failing write, stale version conflict, buffers precedence, resource operations, path safety
   (outside workspace, symlink, `.git`, gitignored), bounds, and line endings.
5. `lsp_code_actions`, `lsp_apply_action`, `lsp_rename`, `lsp_organize_imports`; registered with `lsp_*` family;
   update tool descriptions and Codex/Claude launch instructions (`CorePrompt.cs` and plugin prompts) and the
   `code-intel` plugin script; check `--lsp-refactor` against a fake server and `--lsp-mcp` schema checks.
6. Editor: lightbulb and Alt+Enter popup, Rename in place, Optimize Imports, context menu entries gated by
   capability, undo grouping in Scintilla, progress and cancel; check `--lsp-ui` (menu gating, popup, rename,
   undo as one step, stale rejection) and `--scintilla-core` (undo group API). Wire `workspace/applyEdit` handling.

Phase 2, extract and inline:
7. `lsp_refactor` with extract/inline; `Refactorings.KindAliases` hook in `LanguageServerSpec` and the first
   plugins (TypeScript `_typescript.applyRefactoring`, Go, Clangd); editor chords, post-extract rename.
8. Preview dialog on the diff viewer; conflicts dialog.

Phase 3, change signature, move, safe delete, file operations:
9. Change Signature dialog and tool inputs; gopls-style rename-on-func mapping; Safe Delete with usages view;
   `willRenameFiles` in the Files tree rename; Move.
10. Server-specific work for Roslyn, jdtls and IntelliJ custom commands once probe data says what exists.

Phase 4, remaining items: pull up/push down, visibility, static, type change and convert actions through generic
code actions with plugin `KindAliases`; `lsp_undo`; Reformat via formatting.

Specs to keep consistent when implementing: the parent spec (Agents and Analysis sections), `SharpRail.UI/SPEC.md`
(Edit menu), `Plugins.SPEC.md` (language server hook), `docs/develop/agent-reference.md` (check modes).

## Open decisions

- Dry-run default for agents (proposed true).
- Whether a failed rollback should block further refactorings in the workspace.
- Whether Ctrl+T on macOS (Refactor This) may clash with existing terminal and tab chords.
- Whether IntelliJ Java/Kotlin gets a custom MCP-visible refactoring channel beyond LSP (its protocol is unverified).
