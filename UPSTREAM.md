# Upstream tracking

SharpRail ports two ThinkRail lines, one per SharpRail branch. Both live in the checkout at
`/Users/commandertvis/IdeaProjects/thinkrail`, which has one remote for each.

| SharpRail branch | Ports | ThinkRail ref |
| --- | --- | --- |
| `main` | CommanderTvis's fork: JetBrains plus the Plugin API, its builtin plugins and the fork's general improvements | `origin/claude-code-integration-plugin-api` (the fork's default branch) |
| `upstream` | JetBrains ThinkRail only | `upstream/main` |

Each branch keeps its own copy of this file with its own synced commit. Work on the `upstream`
branch happens in its worktree, `/Users/commandertvis/.thinkrail/worktrees/sharprail/upstream`;
do not port JetBrains-only changes onto `main` by hand, and do not port fork-only changes onto
`upstream`.

The fork's branch is a rebased chain that is force-pushed often, so its hashes do not survive:
general improvements (one commit each), then one Plugin API commit, then one commit per builtin
plugin, then one fork-only README commit. The fork's `AGENTS.md` ("The fork's commits, by title")
lists the chain. Record fork work here by commit title as well as hash, and when the recorded
hash is gone, find the new base by title with `git log --format='%h %s'`.
`main` mirrors that shape: the fork's general improvements land as one commit, the Plugin API as
one commit, and each builtin plugin as its own commit.

The `sync-upstream-specs` skill (`.claude/skills/sync-upstream-specs/SKILL.md`) pulls later
changes for the current branch and advances its synced commit.

Synced commit (`main`): specs follow JetBrains `c44534ead3bd2e64107904cebba20617c5eaac59`
(2026-10-03), inherited from the `upstream` branch, which `main` is rebased onto. Fork code
porting is recorded by fork commit title in "Fork port log" below.

## Mapping

| Upstream | SharpRail |
| --- | --- |
| `architecture.md` | `ARCHITECTURE.md` |
| `packages/shared/SPEC.md`, `packages/server/SPEC.md`, `packages/server/src/host/SPEC.md` | `src/SharpRail.Host.Core/SPEC.md`, `src/SharpRail.Host.Abstractions/SPEC.md` |
| `packages/server/src/{git,changes}/SPEC.md` | `src/SharpRail.Host.Core/Git.SPEC.md` |
| `packages/server/src/workspaces/SPEC.md` | `src/SharpRail.Host.Core/Workspaces.SPEC.md` |
| `packages/server/src/{projects,settings,persistence}/SPEC.md` | `src/SharpRail.Host.Core/HostState.SPEC.md` |
| `packages/server/src/{fs,watch,trash}/SPEC.md` | `src/SharpRail.Host.Core/Files.SPEC.md` |
| `packages/server/src/spec/SPEC.md` | `src/SharpRail.Host.Core/Specs.SPEC.md` |
| `packages/plugin-spec-dialect/SPEC.md`, `packages/spec-graph/{core,tools}/SPEC.md` | `src/SharpRail.Plugins.SpecDialect/SPEC.md` |
| `packages/plugin-discord/SPEC.md` | `src/SharpRail.Plugins.Discord/SPEC.md` |
| `packages/plugin-pdf-preview/SPEC.md` | `src/SharpRail.Plugins.PdfPreview/SPEC.md` |
| `packages/plugin-branch-graph/SPEC.md` | `src/SharpRail.Plugins.BranchGraph/SPEC.md` |
| `packages/plugin-visualize/SPEC.md` | `src/SharpRail.Plugins.Visualize/SPEC.md` |
| `packages/plugin-file-icons/SPEC.md` | `src/SharpRail.Plugins.FileIcons/SPEC.md` |
| `packages/plugin-codex/SPEC.md`, `packages/plugin-codex/host/ideBridge/SPEC.md` | `src/SharpRail.Plugins.Codex/SPEC.md` |
| `packages/server/src/{terminal,subprocess}/SPEC.md` | `src/SharpRail.Host.Core/Terminals.SPEC.md`, `src/SharpRail.UI/Terminal/SPEC.md` |
| `packages/contracts/SPEC.md` | `src/SharpRail.Host.Protocol/SPEC.md` |
| `apps/web/src/transport/SPEC.md` | `src/SharpRail.Host.Client/SPEC.md` |
| `packages/server/src/auth/SPEC.md` | `src/SharpRail.Host.Remote/SPEC.md` |
| `apps/web/SPEC.md`, `apps/desktop/SPEC.md`, `apps/web/src/resources/SPEC.md` | `src/SharpRail.UI/SPEC.md` |
| `apps/web/src/shell/**/SPEC.md` | `src/SharpRail.UI/Docking/SPEC.md` |
| `apps/web/src/store/SPEC.md`, `apps/web/src/navigation/SPEC.md` | `src/SharpRail.UI/State/SPEC.md` |
| `apps/web/src/panels/SPEC.md` | `src/SharpRail.UI/Panels.SPEC.md`, `src/SharpRail.UI/Panels/SPEC.md` |
| `apps/web/src/components/SPEC.md`, `apps/web/src/components/ui/SPEC.md`, `apps/web/src/lib/SPEC.md` | `src/SharpRail.UI/Rendering/SPEC.md` |
| `apps/web/src/themes/SPEC.md` | `src/SharpRail.UI/Rendering/Themes.SPEC.md` |
| `apps/web/src/styles/COLOR.md` | `src/SharpRail.UI/Rendering/COLOR.md` |
| `apps/web/src/styles/SPACING.md` | `src/SharpRail.UI/Rendering/SPACING.md` |
| `e2e/SPEC.md` | `tests/SharpRail.Checks/SPEC.md` |
| `scripts/SPEC.md` | `scripts/SPEC.md` |
| `packages/plugin-api/SPEC.md`, `plugin-adoption.md` (fork) | `src/SharpRail.Plugins.Api/SPEC.md` |
| `packages/server/src/plugins/SPEC.md` (fork) | `src/SharpRail.Host.Core/Plugins.SPEC.md` |
| `apps/web/src/plugins/SPEC.md`, `apps/web/src/plugins/{loader,registry}/SPEC.md` (fork) | `src/SharpRail.UI/Plugins/SPEC.md` |
| `packages/plugin-ui/SPEC.md` (fork) | `src/SharpRail.Plugins.UI.Kit/SPEC.md` |

## Not tracked

Out of scope for SharpRail (AI functionality, the pi packages, CLI launcher, website,
analytics, GitHub/PR review, desktop updates, web-only tooling): every other upstream
`SPEC.md`, plus `goal-and-requirements.md`, whose applicable scope already lives in
`SPEC.md`. A new upstream spec is triaged into one of the two lists on the next sync.

## Fork port log

Fork commits seen while porting `main`, by title, with what happened to each. Hashes are from
the fork chain as fetched on the date given and will not match after a force-push.

Out of scope for every pass: pi, AI chat, CLI, website/analytics, desktop update flow, Electron-
and browser-only mechanics with no Avalonia counterpart.

### General improvements, one commit (2026-10-01)

Range: the synced commit above to the fork's "Plugin API: the contract, the host loader, the web
registry, and the UI kit" (`4737df6d3`), exclusive; fork chain fetched 2026-09-30, tip `0304a543e`.
Commits are in chain order; the first ten are JetBrains commits the fork carries.

- `2f99a3939` Plan page: live Session block, cumulative Summary, and in-plan interaction (#555): skipped (pi/chat)
- `15c66331e` feat(delegation): add resource ownership and captured history (#540): skipped (pi)
- `be8a5e433` feat(dag): add validated contracts and durable storage (#541): skipped (pi)
- `f8f0b725c` feat(dag): add durable execution and portable pi adapter (#542): skipped (pi)
- `15a92a264` fix(e2e): preserve hidden consoles for Windows descendants (#559): skipped (Playwright harness on Windows)
- `5383b8506` feat(updates): add consent-driven update flows (#558): skipped (desktop updates)
- `fdbbc1d2f` docs(specs): make the goal doc a living product document (#573): skipped (goal doc is not tracked)
- `f90ea4c0b` fix(web): default markdown diffs to the rendered view (#569): ported
- `32ce2cad9` Text insertion issue and padding adjustment (#560): skipped (the non-chat part is the create-workspace dialog's prompt image chips and caret restoration; SharpRail's create-workspace flow has no prompt or image attachments, the AI prompt being excluded)
- `d49d91bef` feat(website): add consented journey analytics foundation (#524): skipped (website/analytics)
- `fba9d8853` Reveal a file in the file manager, from our own menu: ported (Files row menu "Reveal in Finder" via host `ApplyFileActionAsync("reveal")`: `open -R` / `explorer /select,` / containing folder; the pi-runtime diagnostics hunk skipped as pi)
- `4fdb372e1` Open a plain folder as a project, with no git required: partial (plain folders open directly and the git-init prompt and host "init" action are removed; Changes/Review withholding for a plain or unborn repo, the `tool-needs-git` notice and the gitless Start-work dialog copy are not ported)
- `008c972b0` web: our own tab tooltips, and they stop blocking what they cover: skipped (SharpRail's tab tooltips are already its own Avalonia tooltips anchored below the tab; the pointer-transparent popup has no verifiable Avalonia counterpart, and the rest of this commit is a web-only merge of unrelated fork work)
- `1f6aa7dfb` The desktop window stops handing its own host to the browser: skipped (Electron navigation)
- `1829e0736` Send an editor selection into a pi chat: skipped (pi chat)
- `10183e913` Where a tab can go, drawn instead of listed: skipped (a new drawn placement picker in the tab menu is a large docking-menu redesign; not attempted in this pass)
- `20a3450be` The light theme's diff canvas stops reading as disabled: ported
- `f4408d2a3` Previewing a file before you keep it is a setting: ported
- `0d59b4878` The desktop window comes back to the port it had, and with it your tabs: skipped (desktop host port; SharpRail embeds its host)
- `ec1ec6823` tests: a fixture open survives a neighbour's pick and a loaded machine: skipped (Playwright fixture timing and picker-pointer state; SharpRail checks use isolated fixtures)
- `32f07e106` Frontmatter edits like Obsidian properties, above the rendered view: partial (read-only properties table with chips, folding and raw fallback; editing into a draft is missing because SharpRail Markdown tabs have no editable source)
- `b047c8f29` The spec tools reach any agent in a ThinkRail terminal, over MCP: partial (MCP protocol, per-terminal token, THINKRAIL_MCP_URL stamping and a loopback HTTP/1.1 route on both the remote host and the local terminal relay are ported; only spec_get and spec_grep are served, because SharpRail has no spec authoring or full graph yet, so spec_create/update/delete/graph/validate and grep's tag/dependsOn filters are missing; the carried mouse-mode guard, process tree and title changes have no SharpRail counterpart)
- `25133aa27` Panes a resource carries: embedded, never a tab of their own: partial (Markdown Split view; visualization/blueprint companions are AI surfaces and skipped)
- `76c3ec2f3` A terminal whose pty inherited a stale utmpx record no longer runs as the wrong user: ported
- `b9548ea25` The outline moves to the pane's edge and drives both preview and source: ported
- `0d61c37c8` A terminal frozen by a drain event the OS never delivered thaws on its own: partial (the drain latch is skipped: gRPC/HTTP-2 flow control has no drain event to lose; of the carried server changes only the unborn-HEAD workspace refusal was ported; published-branch rename, branchDetails/deleteBranch/fetch, suggestWorkspaceName and vcs gap belong to features SharpRail lacks; GIT_OPTIONAL_LOCKS was already present)
- `f8730d329` On the desktop, the host is this computer, and the copy stops calling it "the host": ported for the strings SharpRail has (Enter path… and the path dialog's explanation locally; GitHub/JetBrains AI card strings do not exist here)
- `308b0eef4` A file in the tree can be dragged to where it is wanted: skipped (the diff is xterm drag-and-drop of a tree path into the web terminal; SharpRail's Files rows are not drag sources and its terminal is native Ghostty, so this needs native-bridge drop work)
- `c4eebc5f1` feat(web): put the target choice above the Start work header: ported (the model/effort labels are AI and omitted; an edited name becomes the workspace's host label)
- `87325ac26` An issue number in a comment stops being painted as a colour: already present (Scintilla has no colour decorators)
- `1e4449a2a` A repository URL becomes a project, cloned into a folder you choose: ported 2026-10-03 (`CloneProjectAsync` through every host layer, the Clone repository… dialog and both `clone-project.spec.ts` scenarios; the remaining diff deletes web-only unit tests)
- `7a7685057` The desktop's right-click menu stops offering Look Up, Fonts, and Services: skipped (Electron context menu)
- `3f01f8942` A file in the tree can be deleted, into the trash, after asking: ported (Delete file/folder asks, then moves to the OS trash via NSFileManager on macOS or gio on Linux; also the row menu itself and Copy absolute path; the git-ignored dimming and composer drag tests in the same diff are not ported)
- `1d30e44be` A diff too narrow for two columns opens inline until you say otherwise: ported
- `82419d3ad` Cmd+F finds text in every preview, not only inside the editor: partial (find bar over Markdown previews and rendered diffs with count and stepping; only the current match is selected, other matches are not painted; no find over terminals)
- `be216ba71` Typechecking the desktop stops waiting on a dev host that is running: skipped (Bun tooling)
- `b71bb30a9` A diagram inline in a document stops at a height you can see past: ported
- `99d36b658` Search the whole workspace from one popup: partial (Markdown hits open the rendered document without the fork's block flash)
- `99ed15307` The rendered markdown diff gets the outline and the properties block: ported (properties diffed per key rather than through the text merge)
- `2a2739307` A workbench frame belongs to the project it was arranged in: skipped (needs a per-project frame stash in the persisted DockState, touching presets, validation and document pruning; deferred as too risky for this merge)
- `bf7881065` A selection you can see in the dark theme: ported
- `4496970ed` The projects rail's plus says what it does: already present (the Add project button already has its tooltip)
- `155735756` A spec's own frontmatter is a properties table, not a raw block: ported (multi-line flow sequences and one-level mappings read as properties; read-only as above)
- `783b0b04e` An inline diagram zooms like the fullscreen one, and pans inside its box: ported
- `d55683607` chore: bump TypeScript to 7.0.2 and migrate off the legacy compiler API: skipped (TS tooling)
- `e597593d7` A spec is titled by its frontmatter, and its [[links]] go somewhere: ported
- `f6371aa62` Folding a rail stops rebuilding the whole workbench: already present (a regression check was added)
- `fb6c0147d` The branch in the topbar opens the project's branches: partial (SharpRail lists every worktree as a workspace, so the fork's "foreign worktree" branch deletion is refused instead)
- `cf91102e9` A project with no specs opens its rail on Files: ported
- `356366850` A terminal nobody is looking at stops rendering: skipped (xterm/CSS content-visibility; Ghostty renders natively)
- `f9afe4049` The bottom row's controls clear the window's rounded corner: skipped (SharpRail's bottom group header sits at the top of the bottom region, never against the window's rounded corner)
- `c4c236097` A code font you choose, with its ligatures: skipped (needs a new host setting through all five host layers plus font plumbing into Scintilla's bundled SKTypeface, Ghostty's config shim and Markdown code blocks; recorded under Not yet ported in Rendering/SPEC.md)
- `ebedcf702` Settings answer to Command+, on macOS: ported
- `0d40a484b` The topbar says what "from main" means: ported (SharpRail's topbar has no "from main" line, so the tooltip is on the workspace placeholder's "· from main", the only place it appears)
- `4171b16c2` A large markdown preview stops re-doing its own work: already present (document controls are cached across tab switches, so a revisited preview is not rebuilt; there is no tokenizer to cache)
- `2d8fce7b1` A selection reaches the document, not the chrome around it: already present (only content surfaces use SelectableTextBlock; chrome is plain TextBlock)
- `9e5e727a2` Tests, fixtures and spec text the fork's changes left behind: skipped (nothing in scope: web unit tests, Playwright fixtures and the New project spec, a feature SharpRail lacks)
- `7f94b1832` The CLI waits a beat for a tab that is already open before opening another: skipped (CLI)
- `8e05d76c0` A default model and thinking level you can choose in Settings: skipped (AI)
- `5bdc70e39` Vertical tabs can live under their workspace in Projects: ported later, as `8feda8584` (see "Fork refresh, 2026-10-02")
- `ba90884c6` A dirty worktree can be force-removed before its branch: partial (the host has `force-remove-worktree`, and removing a dirty workspace from the rail asks a second time before forcing; the branch list still refuses to delete a branch a workspace is on instead of removing that worktree first)
- `ba3d301c4` Branch pickers show every remote, collapsibly, and a click can switch or start a workspace: partial (collapsible, profile-remembered remote groups in the New Workspace picker are ported; the topbar branch list and its click-to-switch/start actions do not exist in SharpRail)
- `a86e95aaf` The Welcome provider warning recognizes connected JetBrains AI: skipped (AI)
- `2c44d749d` JetBrains AI access source switching, for accounts with more than one org: skipped (AI)
- `a204860fb` Hidden models you can filter out in Settings and the model picker: skipped (AI)
- `3123ee248` A tooltip when a model name truncates in the model selector: skipped (AI chat)
- `063b43f55` Questionnaires superseded by later assistant activity clear the waiting status: skipped (AI chat)
- `557f0f3d6` Attached images in chat draft persist across tab switches: skipped (AI chat)
- `26e511629` An open file says when its file is deleted on disk: ported (tab "deleted" mark and a Scintilla banner; a remote read maps a missing file to gRPC NotFound so the coded miss survives the wire; the banner is only on Scintilla editors, so Markdown/image tabs get the tab mark alone)
- `8f6b555d8` A file or folder can be created, or renamed, from the Files tree: ported (New file…/New folder…/Rename… with one name dialog: inline collision, stem selection, host error shown in place; no per-file-type icon since the tree has none)
- `be1bf412e` Remove the CLAUDE.md alias of AGENTS.md: skipped (ThinkRail repo housekeeping)
- `df19980af` Terminals start clean after a host restart: already present (SharpRail never persisted terminal output; the not-yet-ported revival note now says revived shells start blank)
- `524db465d` OpenAI models carry their provider mark: skipped (AI chat)
- `4d2e6b2d2` Chat tabs carry the Pi mark: skipped (AI chat)
- `915b6d978` A live terminal reattach restores full-screen input modes: ported (host replay appends the live alternate screen and mouse/alternate-scroll modes)
- `4c774f45e` Chats survive a provider switch when their saved model is unavailable: skipped (AI chat)
- `626031274` A project's absolute path can be copied from its context menu: ported (plus the workspace menu's Copy path -> Copy absolute path and new Copy name)
- `d4d03db1b` A worktree made in ThinkRail's folder shows up as a workspace: already present (SharpRail's workspace rows come straight from `git worktree list`, so any worktree made from a terminal already shows; there is no workspace registry to adopt into or dismissal list to keep)

### Plugin API, one commit (2026-10-01)

- `4737df6d3` Plugin API: the contract, the host loader, the web registry, and the UI kit: ported as the three
  `SharpRail.Plugins.Api*` assemblies, the host runtime (`Host.Core/Plugins`), the app runtime (`UI/Plugins`)
  and `SharpRail.Plugins.UI.Kit`, with the wire as one generic gRPC call and stream. Not ported: pi (H11, H16,
  the agent tool surface and the `pi` block, which is parsed and ignored), chat (chat companion hosts,
  `openChat`, tool renderers, the `writtenPathGroup` slot), workspace auto-naming hints, the `styles` manifest
  field, and the kit's settings-row and terminal-fact components, which arrive with their first plugin. The
  file-icons plugin's assets and the Specs panel's move are later commits.

### Builtin plugin ports in progress (2026-10-01)

- `e8a751bd7` Spec Dialect: recovered and extended to all seven MCP tools, the Specs tree, spec links,
  rail defaults and legacy layout migration. Specs discovery and MCP ownership now belong to the plugin.
  Focused checks pass; the signed commit awaits full-suite verification and the signing agent.
- `b2a6c75a6` Blueprint: registered host/UI halves with the contract, streaming format, reconciliation,
  exact prompts, persistent author, keyed state, tool, properties, controls, companion, start dialog and
  file-to-author redirect. Focused runtime, gRPC and headless checks pass. Remaining fidelity/acceptance
  gates are in `COMPLETION.md`; the signed commit and full-suite gate remain pending. Pi and bundled AI
  chat remain excluded.
- `6401ccae8` Claude Code: recovered host/configuration/IDE bridge/hook assets and UI controls;
  added activation entry point and builtin registration. Host status/configuration/review/revive,
  transcript folding, headless pane/launcher/settings lifecycle and 40 hook checks pass. Static
  settings use compiled XAML. Other fidelity and full verification gates remain in the plugin spec;
  no signed plugin commit yet. IDE `openDiff` retains the fork's documented file-opening fallback.
- `eb382d75e` Discord: recovered and registered host/UI halves, preserving the fork's presence decisions,
  IPC protocol, settings, disabled default and Discord mark. Local/remote settings and gRPC checks pass;
  native/published/full-suite gates and the signed plugin commit remain pending.
- `ab23be7cf` PDF Preview: recovered and registered the UI-only builtin, with PDFium rendering,
  text selection, shared zoom math and local/remote live reload. Focused engine/UI checks pass.
  The remaining fidelity/verification gates are still open.
  Selection across visible pages, clipboard/Select All and rotated/cropped text geometry now pass checks.
  Captured drags scroll across offscreen pages; native Unicode extraction and compiled toolbar/retry pass checks.
- `e91aef250` Branch Graph: recovered and registered contract, host and UI halves. Parser/lane fixtures,
  local/gRPC history and patches, windowed/paged UI, copy actions and Changes scoping pass focused checks.
  Independent host commit lookup closes the comparison-menu dependency; unchanged graph rows retain their
  controls during refresh. Compiled row/menu templates, glyphs, changed-ref focus/menu retention and
  disable/remount are verified. Remaining lifecycle/visual scenarios and complete gates are open.
- `b8d8ec066` Visualize: registered contract, host and UI halves with the terminal MCP drawing tool,
  persisted session adoption, whole-workspace state channel and renderer reports. Shared comparison and
  Mermaid controls use compiled XAML and the PDF zoom gesture vocabulary. Focused local/gRPC host and
  companion checks pass, including rollback, revision reuse and restart. Remaining multi-terminal/window,
  content/lifetime, MCP HTTP ownership, native/published/full gates are in its owning spec.

- `40678fd30` File Icons: registered the UI-only builtin, exact filename/extension mappings and pinned
  Material Icon Theme 5.38.1 generation. All 1,251 recoloured SVGs match the fork generator byte for byte.
  Local/gRPC tree, tab, Changes, theme, resizing and disabled/missing/broken asset fallbacks pass focused checks.
  Native appearance, canonical published app and full-suite gates remain open.

- `177abd30c` Codex: recovered and registered contract/host/UI draft with TOML provenance, hooks,
  launch/revive, rollout facts, app-server account/models and IDE v0 IPC. Logic checks pass; 297
  documented keys/types match fork. Focused real gRPC UI account/config/context/IDE/launcher/model/status
  checks pass; complete fidelity, native/published/full gates remain open.

## Pending fork refresh, 2026-10-01

The local fork ref now points to `1c7c90a5a`. Compared with the previous `0304a543e` tree,
plugin sources remove workspace-name suggestions from the host API and Claude/Codex hooks,
and add the UI kit's `Switch` control and tests. SharpRail already omits the naming operation.
The obsolete Codex report fields and corresponding test expectations are now removed. The shared
switch's controlled state, geometry, input, automation and disabled theme checks pass in the
isolated build; combined verification and transition/native appearance review remain. Historical
port references above remain until the source audit is complete.

## Fork refresh, 2026-10-02

Latest tracked fork commit: `867bffea508a25e48b7b8500f95ba8991bbc587f` ("The fork describes itself:
what it adds, how to run it, how its history moves"), the tip of `claude-code-integration-plugin-api`
fetched 2026-10-02. The chain is now rebased onto JetBrains `be804a563`, the commit `upstream` and
therefore `main` already follow. Compared with `1c7c90a5a`, by title:

- `d356661a5` feat(state): replace session activity with normalized session lifecycle (#525): skipped (pi/chat)
- `be804a563` feat(desktop): frameless Windows title bar with HTML window controls (#536): inherited from `upstream`
- `ee852215c` perf(web): enable the native React Compiler: skipped (web-only)
- `71e884170` Restoring the latest Pi chat in every workspace is a setting: skipped (pi/chat)
- `b9ec7d8ba` Recents stops listing project folders that no longer exist: ported (a read-time projection in
  `HostStateStore`; missing or file-replaced paths are hidden, other errors keep them, and the stored list
  is untouched)
- "Folding a rail stops rebuilding the whole workbench" left the chain; its SharpRail regression check remains.
- `4a8aa9191` Claude Code integration is a builtin plugin, amended: the appended system prompt is the fork's
  compact text with SharpRail's tools; render-time host reads were already reactive through `WatchHost`.
- `667cf631f` OpenAI Codex is a builtin plugin, amended: wire version 3, tab-scoped configuration and
  AGENTS.md discovery from the tab's CWD, the "Tell Codex it is running in SharpRail" setting with its
  developer-instructions file, launch-flag source and hook tagging, and revival that reapplies the
  setting once. See `src/SharpRail.Plugins.Codex/SPEC.md`.
- `8feda8584` Vertical tabs can live under their workspace in Projects: ported (vertical centre column with
  px width, tab panes as workspace-view metadata with the fork's model rules, pane drops and menu verbs,
  no centre split in the mode, ambiguous-name folder lines, the strips nested in Projects with other
  workspaces' read-only tab lists). Its other parts were already covered or are web-only: keep-alive
  terminals (SharpRail retains terminal controls), `useElementSize`, `content-visibility`, and the window
  corner gutter. The "show beside a Claude terminal" open rule and `unofferedTools` are not ported.

## Fork refresh, 2026-10-03

`claude-code-integration-plugin-api` is still at `867bffea5` (checked with `git ls-remote` on 2026-10-03).
Two of its features were missing here and are now ported:

- The Settings dialog nests plugin sections under Plugins (its `SettingsDialog.tsx` fieldset): plugin pages
  sit indented behind a rule directly below Settings › Plugins instead of after the core sections.
- `1e4449a2a` A repository URL becomes a project: see its entry in the port log above.
