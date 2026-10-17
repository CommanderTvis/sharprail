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

Synced commit (`main`): specs follow JetBrains `f7cca7bfb0b1d0543eb9898c52d28df5a784eb09`
(2026-10-11), inherited from the `upstream` branch, which `main` is rebased onto. Fork code
porting is recorded by fork commit title in "Fork port log" below.

Latest tracked JetBrains commit (research, not a completed port or spec sync):
`be06fb01927e8386e3aec215bbcdb1a312ec66fa` (2026-10-14), "feat(web): list newest workspaces
first in the projects panel (#725)". Verified by fetching `upstream/main` from
JetBrains/thinkrail on 2026-10-14. The range after the synced `f7cca7bfb` contains
22 newer commits. [Current porting gaps](#current-porting-gaps) records applicable changes, older unported behavior,
supported Codex/Claude workflows and remaining acceptance gates. The dated comparison
baseline there is provenance, not the current implementation revision.
The synced revision above remains unchanged; this research does not claim that the
new specifications or implementations have been ported.

Latest tracked fork commit: `281bda97b2840c92e9046092d56b81286c4cea4a` ("The fork describes itself:
what it adds, how to run it, how its history moves"), the tip of `claude-code-integration-plugin-api`
verified against GitHub on 2026-10-09. Fork port coverage remains recorded by title below.

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
| `packages/plugin-pdf-preview/SPEC.md` | `src/SharpRail.UI/Resources/Pdf/SPEC.md` |
| `packages/plugin-branch-graph/SPEC.md` | `src/SharpRail.Plugins.BranchGraph/SPEC.md` |
| `packages/plugin-visualize/SPEC.md` | `src/SharpRail.Plugins.Visualize/SPEC.md` |
| `packages/plugin-file-icons/SPEC.md` | `src/SharpRail.Plugins.UI.Kit/FileIcons/SPEC.md` |
| `packages/plugin-codex/SPEC.md`, `packages/plugin-codex/host/ideBridge/SPEC.md` | `src/SharpRail.Plugins.Codex/SPEC.md` |
| `packages/server/src/{terminal,subprocess}/SPEC.md` | `src/SharpRail.Host.Core/Terminals.SPEC.md`, `src/SharpRail.UI/Terminal/SPEC.md` |
| `packages/contracts/SPEC.md` | `src/SharpRail.Host.Protocol/SPEC.md` |
| `apps/web/src/transport/SPEC.md` | `src/SharpRail.Host.Client/SPEC.md` |
| `packages/server/src/auth/SPEC.md` | `src/SharpRail.Host.Remote/SPEC.md` |
| `apps/web/SPEC.md`, `apps/desktop/SPEC.md`, `apps/web/src/resources/SPEC.md` | `src/SharpRail.UI/SPEC.md` |
| `apps/web/src/shell/SPEC.md`, `apps/web/src/shell/{layout,layoutState,layoutIntents}/SPEC.md` | `src/SharpRail.UI/Docking/SPEC.md` |
| `apps/web/src/shell/locationBar/SPEC.md` | `src/SharpRail.UI/SPEC.md` |
| `apps/web/src/store/SPEC.md`, `apps/web/src/navigation/SPEC.md` | `src/SharpRail.UI/State/SPEC.md` |
| `apps/web/src/panels/SPEC.md` | `src/SharpRail.UI/Panels.SPEC.md`, `src/SharpRail.UI/Panels/SPEC.md` |
| `apps/web/src/components/SPEC.md`, `packages/ui/SPEC.md`, `apps/web/src/lib/SPEC.md`, `apps/web/src/resources/SPEC.md`, `apps/web/src/styles/TYPOGRAPHY.md` | `src/SharpRail.UI/Rendering/SPEC.md` |
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

## Latest sync triage

Compared the previously tracked revision with the requested commit
`f7cca7bfb0b1d0543eb9898c52d28df5a784eb09`: six commits, through the continuous
Changes review feature (#668). The requested object was fetched directly from
JetBrains/thinkrail into this checkout; the authoritative upstream checkout was
not changed. Triage completed on 2026-10-11.

Every changed spec and the changed styles were triaged. Applicable deltas are
adapted in their mapped destinations, with unimplemented behavior explicitly
recorded under Not yet ported:

- UI, shell/layout, layoutState, layoutIntents and lib: persistent edge tool rails,
  compatible tool/terminal panes, remount-free visibility and topology changes,
  workspace-fenced gestures/focus, atomic attention updates, no-op notification
  suppression, Home routing and shared modal/text-entry keyboard ownership.
- Panels, store and resources: continuous Changes review tabs, sidebar navigation,
  single-click reveal versus double-click individual diff, lazy bounded sections,
  stacked/one-file presentation, viewed and content-keyed hunk Keep progress,
  scoped session state, historical-scope immutability and renderer sizing.
- Themes: opaque primary-fill semantics and the Minimal Light/Dark pair.
- Shared host: login-shell environment import with inherited-value precedence and
  excluded probe variables; it remains an implementation gap.
- Spec graph: exclude assistant scratch directories from indexing.
- E2E: pending rail/layout/Home translations, continuous Changes journeys and
  fixture/disclosure-anchor invariants; no new translated coverage is claimed.

The mapping now includes layoutIntents under Docking and resource-renderer
contracts under Rendering as well as UI. The new
`packages/server/src/turns/SPEC.md` is not tracked: agent-run snapshots and change
receipts are AI functionality. Changes to contracts, transport, server/git/host,
persistence, settings, workspaces, agent, pi-delegation, chat, reviews and
planReview specify excluded AI turn tracking, subagent concurrency or review
guides. Web scripts' React compiler census and styles' CSS token allowance are
web-only; chat colour changes remain excluded. No specs were deleted.

This revision records completed specification triage, not implementation parity.
Remaining native work is recorded in the owning specs.

`apps/web/src/notifications/SPEC.md`, left untracked by an earlier sync, is adapted on `main` in
`src/SharpRail.UI/SPEC.md` ("Away notifications") for the Claude Code and Codex terminals, whose
plugins are the only attention source. Its pi session-state observer, browser permission preface
and session switcher have no counterpart.

## Fork port log

Historical fork port decisions, by title, with what happened at each recorded pass.
Current gaps are consolidated in [Current porting gaps](#current-porting-gaps); skipped chat/pi
commits below document exclusions, not pending work. Hashes are from
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
- `32f07e106` Frontmatter edits like Obsidian properties, above the rendered view: ported (editable preview properties, chips, folding, key operations, shape conversion and suggestions share the source draft; unsupported YAML and diffs remain read-only)
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
- `3f01f8942` A file in the tree can be deleted, into the trash, after asking: ported (Delete file/folder asks, then moves to the OS trash via NSFileManager on macOS or gio on Linux; also the row menu itself and Copy absolute path; git-ignored dimming is now ported with host rule checks and headless styling/opening coverage; composer drag remains unported)
- `1d30e44be` A diff too narrow for two columns opens inline until you say otherwise: ported
- `82419d3ad` Cmd+F finds text in every preview, not only inside the editor: partial (find bar over Markdown previews and rendered diffs with count and stepping; only the current match is selected, other matches are not painted; no find over terminals)
- `be216ba71` Typechecking the desktop stops waiting on a dev host that is running: skipped (Bun tooling)
- `b71bb30a9` A diagram inline in a document stops at a height you can see past: ported
- `99d36b658` Search the whole workspace from one popup: partial (Markdown hits open the rendered document without the fork's block flash)
- `99ed15307` The rendered markdown diff gets the outline and the properties block: ported (properties diffed per key rather than through the text merge)
- `2a2739307` A workbench frame belongs to the project it was arranged in: skipped (needs a per-project frame stash in the persisted DockState, touching presets, validation and document pruning; deferred as too risky for this merge)
- `bf7881065` A selection you can see in the dark theme: ported
- `4496970ed` The projects rail's plus says what it does: already present (the Add project button already has its tooltip)
- `155735756` A spec's own frontmatter is a properties table, not a raw block: ported (multi-line flow sequences and one-level mappings read as properties; editable preview properties now share the source draft)
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

Tracked fork commit at this refresh: `867bffea508a25e48b7b8500f95ba8991bbc587f` ("The fork describes itself:
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

## Current porting gaps

This is the cross-module inventory of applicable ThinkRail implementation differences and
Codex/Claude terminal workflow gaps. Module specs own contracts and durable verification facts;
COMPLETION.md owns completion criteria and E2E.md inventories translations. There is no separate
current gap ledger. Embedded chat, pi execution and their internal services remain excluded.
`Decision` rows are proposals, not accepted feature designs; `Missing` means absent and `Partial`
means a narrower counterpart exists. Acceptance tasks do not imply missing implementation.

The research was performed on 2026-10-14 against `main` at
`16138700b2fbf86d845ad76a133dacb7d6ab1d5c` (`audit-thinkrail-gaps`), excluding other
workspaces and the original checkout’s uncommitted browser script change. It covered the
475-commit JetBrains history, its 98 spec paths and E2E catalogue, the 22 commits after the
recorded sync, fork omissions and SharpRail module gaps. The tracked upstream revisions at the
top of this document remain authoritative: research does not advance specification or code sync.
The rows include later workspace-13 integration and main fixes; the original revision remains
comparison provenance.

### Implemented since the research baseline

| Original IDs | Implementation |
| --- | --- |
| A33, A34 | Four shared CLI workflow skills: interview/bug brainstorming, questions, routing and spec-graph guidance. |
| H11 | Spec traversal and authoring exclude the five assistant/mockup scratch folders. |
| P13 | Terminal companions start with the fork's 45% share. |
| L01–L05, L09 | Persistent edge rails, compatible tool/terminal panes, placement/drop rules and independent Project Home visibility. The bottom zone remains retired. |
| W01–W07, W09 | Host activity, settling overrides/settings, client partition/latch, shelf/paging/notice and differentiated creation/activity ordering. |
| U02, U07, G01 | Semantic typography, quiet scrollbars/curtains and the calm 40px location strip. Minimal palettes are included under the narrowed U01 entry. |
| V08 | Browser Settings renderer ownership is already fixed; normal-bundle and physical-browser acceptance remains V07. |
| W13 | Intentional divergence: workspaces start empty and terminals launch explicitly. Initial-terminal provisioning is no longer a product requirement. |

These dispositions do not close native appearance, physical-device, complete packaged-suite or browser-engine acceptance. The owning specs record verification scope and limits; automated passes do not establish physical-platform acceptance.

### Changes since the recorded JetBrains sync

Applicable commits below are linked to their immutable source diffs. Chat/pi-only changes are omitted; mixed commits retain only their applicable parts. Later commits supersede earlier ordering requirements.

| Commit | Change and SharpRail disposition |
| --- | --- |
| [1ab1b1e31](https://github.com/JetBrains/thinkrail/commit/1ab1b1e31) | Denser named layout tokens and tighter line heights. Named dense layout tokens and semantic typography are now implemented; remaining surface fidelity is tracked under U03 and V05. |
| [951863595](https://github.com/JetBrains/thinkrail/commit/951863595) | Explain and pass project trust for skills, extensions, settings and MCP. Provider-specific configuration/trust exists; no common pre-launch project-resource trust summary. |
| [dedfc1bac](https://github.com/JetBrains/thinkrail/commit/dedfc1bac) | OS window focus suppresses desktop notifications. SharpRail already uses app-window focus; live signed-bundle behavior still needs verification. |
| [9d6ad0b2f](https://github.com/JetBrains/thinkrail/commit/9d6ad0b2f) | Calmer captioned location bar. The calm 40px location strip is implemented; branch headlines and broader PR treatment remain separate gaps. |
| [1166af01a](https://github.com/JetBrains/thinkrail/commit/1166af01a) | Login environment only for launchers opting in. SharpRail lacks the equivalent bounded host environment repair/import. |
| [530dffdb1](https://github.com/JetBrains/thinkrail/commit/530dffdb1) | Stable rail order, recent-activity topbar switcher. Superseded in part by newest-created-first at the tip; Activity stamps and differentiated rail/switcher ordering are implemented. |
| [e04b2681f](https://github.com/JetBrains/thinkrail/commit/e04b2681f) | Shell environment probe must not pollute history. The opted-in environment probe is absent; pi background-completion cleanup is excluded. |
| [bf4da6614](https://github.com/JetBrains/thinkrail/commit/bf4da6614) | No unread-result attention while background children still work. SharpRail has terminal status marks, not this normalized parent/child attention model. |
| [ba8d9d898](https://github.com/JetBrains/thinkrail/commit/ba8d9d898) | Restore design interview and durable spec amendment workflow. The shared CLI bundle now ships brainstorming, question, routing and spec-graph workflows. |
| [e5eeebffe](https://github.com/JetBrains/thinkrail/commit/e5eeebffe) | MCP Settings editor, presets, admission, sign-in and diagnostics. Claude configuration provides part of this; no common cross-agent manager. |
| [5ab283a35](https://github.com/JetBrains/thinkrail/commit/5ab283a35) | Show rejected design alternatives and tradeoffs. Rejected alternatives and durable spec-amendment guidance now ship in the brainstorming workflow. |
| [ffc934fa4](https://github.com/JetBrains/thinkrail/commit/ffc934fa4) | Background work keeps running treatment; seen results clear owner-wide unread state. No equivalent SharpRail lifecycle/read watermark across clients. |
| [be06fb019](https://github.com/JetBrains/thinkrail/commit/be06fb019) | Rail workspaces newest-created-first with Default pinned, switcher still recent-activity-first. The rail now pins Default and reverses registry creation order; the switcher orders by activity. |

The researched range contains 22 newer commits; this table lists only applicable changes.

### Agent workflow gaps for Codex and Claude Code

These gaps concern the supported terminal workflows. Provider-owned adapters and host-neutral contracts may be needed; Decision entries remain proposals rather than accepted feature designs. CLI-owned chat controls are excluded.

SharpRail counterparts: [Codex](src/SharpRail.Plugins.Codex/SPEC.md), [Claude Code](src/SharpRail.Plugins.ClaudeCode/SPEC.md), [shared accessories](src/SharpRail.Plugins.Agent.UI/TerminalFacts.cs), [rail](src/SharpRail.UI/ProjectRail.cs) and [notification collection](src/SharpRail.UI/Notifications/AttentionNotifications.cs).

| ID | Status | ThinkRail has; SharpRail lacks or narrows |
| --- | --- | --- |
| A08 | Partial | New-workspace launch supports prompt, image, skill and model choices. SharpRail has prompt/images and provider launcher choices, but lacks the full completion/template/skill-selection workflow. |
| A13 | Partial | Unified model/effort picker with favorites, recents, provider glyphs, compatible efforts and refreshed catalog. Codex has a catalog/picker; Claude has CLI-driven choices; no shared full preference/catalog semantics. |
| A14 | Partial | Consistent account connection and default model/effort controls for the supported CLIs. Launch defaults and provider-owned account/config surfaces exist, but their workbench presentation remains uneven. |
| A16 | Partial | Live tokens/cost/context telemetry with explicit finalized boundaries. SharpRail exposes rollout/transcript tokens and account limits, but not the complete common context/cost presentation; Claude deliberately avoids estimated subscription cost. |
| A17 | Missing | Workspace/project rollup replaces identity glyph with the shared working brand badge; attention is a separate static binary dot. Plugin decorations are on terminal tabs, including nested Projects tabs, not a normalized workspace/project rollup. |
| A18 | Missing | Owner-wide unread-result watermark cleared only when the relevant result is visibly seen; converges across clients. Terminal done/blocked statuses are not this durable read model. |
| A19 | Missing | CLI-reported background work keeps the workspace working and suppresses premature done attention. Establish which lifecycle signals Claude Code and Codex expose before defining a shared status contract; SharpRail does not own their command/subagent execution. |
| A26 | Partial | Common MCP Settings: user/project scope, shadowed entries, stdio/HTTP editors, JSON paste, presets and field-preserving writes. Claude has reviewed configuration composers; Codex exposes configuration/provenance; no unified manager matching this complete workflow. |
| A32 | Partial | Portable skills loaded from multiple agent roots with source provenance, group/individual enablement, live stale/reload badge and project trust acknowledgement. Provider capability panes expose discovery, but no shared per-session manager with those guarantees. |
| A35 | Partial | Before starting work, explain which project resources trust admits and which run code; revoke/acknowledge through one projection. Claude/Codex have their own trust/config semantics, but no common Welcome/Start-work/Skills summary. |

### Review and agent-run change gaps

Upstream authorities: [reviews](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/reviews/SPEC.md), [turn receipts](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/turns/SPEC.md), [panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md). SharpRail: [Panels contract](src/SharpRail.UI/Panels.SPEC.md), [continuous review](src/SharpRail.UI/ChangesReviewPane.axaml.cs), [Git contract](src/SharpRail.Host.Core/Git.SPEC.md), [Review panel](src/SharpRail.UI/ReviewPull.cs).

| ID | Status | Gap |
| --- | --- | --- |
| R01 | Missing | Durable review records with draft/sent/resolved/dismissed lifecycle, clear/archive and per-file finished state. SharpRail's Review panel is principally branch/PR actions; continuous Changes review is a diff reader with local triage. |
| R02 | Missing | Select code or a hunk and write a review comment on it; whole-file and whole-change-set remarks. Editor-to-agent selection context already exists and is not review authoring. |
| R03 | Missing | Base/worktree anchor spaces pinned to immutable original blobs, quoted context and stale/moved/outdated re-anchoring. Existing IDE selections and CAS revert hashes do not persist review anchors. |
| R04 | Missing | Image/SVG region, CSV cell, JSON pointer, notebook cell and PDF page-region anchors, geometry projection and unplaced-thread strip. No shared review contract on rich renderers. |
| R05 | Missing | Per-file accordion with editable drafts, sent history, finding badges, focus/jump and review flags on tabs. |
| R06 | Decision | Send one or all reviewed comments as structured context to the chosen Codex/Claude terminal, retain a per-file recipient and resolve comments through MCP. The fork's Claude review round trip remains pending; no complete host comment subsystem backs it. |
| R07 | Missing | Hunk Ask agent action with displayed hunk context, source-side semantics and a review composer. Revert/Undo and Keep exist; this third action does not. |
| R08 | Missing | Review-section scratch drafts survive collapse, virtualization, renderer changes and Stacked/One-file switching without accidental focus/submission. |
| R09 | Decision | Read-only Last turn and selected agent-run Changes scopes from before/after Git tree snapshots, persisted bounded receipts and turn-divider navigation. Current commit/branch/working/index scopes do not answer what one run changed. |
| R10 | Decision | Agent-neutral run-boundary receipts for Codex/Claude, including shell/generator writes rather than trusting tool arguments. Hooks would need a new bridge; overlapping runs must remain interval changes, not exclusive authorship claims. |
| R17 | Partial | Complete review keyboard ownership: one active review tab answers V/J/N/P, and modal/menu/text-entry/terminal input wins. Current specs retain the shared keyboard gate as an open requirement. |
| R18 | Partial | Delayed section sizing preserves requested scroll/reveal until content can satisfy it or the user takes over. Current outer scroll restores only the available offset. |
| R19 | Partial | Registry-declared natural versus bounded review section height. Source/Markdown grow; other formats use generic bounded viewports rather than their own capability contract. |
| R20 | Missing | A diff whose file left the change set announces identical sides. Current open diff retains its previous content instead. |

### Workbench and docking gaps

Authorities: upstream [layout](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/layout/SPEC.md), [intents](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/layoutIntents/SPEC.md); SharpRail [Docking contract](src/SharpRail.UI/Docking/SPEC.md), [UI contract](src/SharpRail.UI/SPEC.md), [profile](src/SharpRail.UI/State/ProfileStore.cs), and the older fork log in [Fork port log](#fork-port-log).

| ID | Status | Gap |
| --- | --- | --- |
| L06 | Partial | Preserve surviving pane chrome and bodies through visibility, folds and topology changes, not just cached document controls or unchanged refreshes. Full latest remount-free contract remains open. |
| L08 | Partial | Drop normalized no-op writes and atomically commit document/attention/tool fan-out; review-local writes invalidate only their body. |
| L10 | Missing | Older fork's per-project frame stash: returning to a project restores its own arrangement. Current profile owns a frame per window and workspace document views. |
| L11 | Missing | Older fork's drawn tab-placement picker instead of only text destination menus. |
| L12 | Partial | Older fork's open-beside-Claude rule and unoffered-tool handling in Projects tab mode. The core vertical/Projects mode itself is implemented. |

### Projects and workspace lifecycle gaps

Authorities: upstream [workspaces](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/workspaces/SPEC.md), [panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md), [latest ordering](https://github.com/JetBrains/thinkrail/commit/be06fb019); SharpRail [Workspaces](src/SharpRail.Host.Core/Workspaces.SPEC.md), [HostState](src/SharpRail.Host.Core/HostState.SPEC.md), [State](src/SharpRail.UI/State/SPEC.md), [Panels](src/SharpRail.UI/Panels.SPEC.md).

| ID | Status | Gap |
| --- | --- | --- |
| W08 | Decision | Automatic settling on merged/closed PR and authoritative host review snapshot. Existing PR lookup does not provide this lifecycle. |
| W10 | Partial | Active workspace's parent project is revealed on initial mount/ownership change while a later manual collapse stays respected. SharpRail primarily restores its collapsed set. |
| W11 | Missing | Prefetch a selected non-default base off the creation critical path. Default-base prefetch and create-time fetch already exist. |
| W12 | Missing | Retry rejected workspace creation inside the same dialog. SharpRail closes it before the operation and reports failure separately. |
| W14 | Missing | Seed the scratch directory when a session starts in a user-owned attached workspace. Managed creation seeding is not that case. |
| W15 | Partial | Managed branch rename after its initial automatic name. First `set_title` names a untouched generated branch; later label renames deliberately do not rename Git. |
| W16 | Decision | Create a new project through an agent-assisted setup brief, not only open/clone an existing project. Existing starting-a-new-project skill does not provide this native flow. |
| W17 | Partial | Git-only pane withholding is implemented for plain folders, including direct actions and restoration; empty/unborn Git repositories intentionally remain eligible. The older fork's gitless Start-work explanatory copy and launcher-state fidelity are not established by the pane regressions and remain to be reviewed. See [Panels](src/SharpRail.UI/Panels.SPEC.md). |

### Location bar and Git UI gaps

Authorities: upstream [location bar](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/locationBar/SPEC.md), fork history in [Fork port log](#fork-port-log); SharpRail [header layout](src/SharpRail.UI/WorkbenchWindow.axaml), [location actions](src/SharpRail.UI/LocationBar.cs), [branch list](src/SharpRail.UI/BranchList.cs), [panel gaps](src/SharpRail.UI/Panels.SPEC.md).

| ID | Status | Gap |
| --- | --- | --- |
| G02 | Missing | Header Remote divergence and Pull request chips; open/merged/closed state, provider URL and authoritative snapshot arbitration. PR details are confined to Review. |
| G03 | Partial | Branch card's separate Based on row, Remote/review rows and shared Compare-to chooser. Caption currently derives “from” from the comparison choice, so creation base and retargeted comparison are not separately represented. |
| G04 | Partial | Shared searchable comparison branch combobox; Local/Remote folder groups, leaf labels, full-ref tooltips and chosen target. Current Changes target menu does not implement the full grammar. |
| G05 | Partial | Fork's topbar branch picker lists every remote collapsibly and click switches to an existing worktree or starts a workspace. Native branch list currently lists local refs and delete/fetch actions. |
| G06 | Partial | Fork's branch deletion can first remove its worktree, with explicit dirty-force confirmation. Dirty workspace deletion exists, but branch deletion refuses occupied branches rather than composing the teardown. |
| G07 | Missing | PR setup guidance Run action opens a workspace terminal; preserve Git's authentication error text and host-key hint; remote-host commands follow host platform. Copyable generic guidance exists. |
| G08 | Missing | Registered OS URL scheme for serialized project/workspace/resource links. Serialization, in-app history and `--link` launch routing are implemented. |
| G09 | Partial | Remote host confirms workspace existence before history traversal. Current remote navigation cannot make that confirmation. |
| G10 | Partial | Back/Forward records user navigation separately from passive remote removal/close fallbacks, with observed locations persisted as links. Current history records any location change. |
| G11 | Missing | CI status beside the pull request chip is a recorded SharpRail request. [Panels contract](src/SharpRail.UI/Panels.SPEC.md) lists it as missing; [LocationBar.cs](src/SharpRail.UI/LocationBar.cs) and the window header have no CI chip. Keep separate from JetBrains parity. |

### Rendering and resource gaps

Authorities: upstream [resource registry](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/resources/SPEC.md), [rich panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md), [themes](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/themes/SPEC.md); SharpRail [registry](src/SharpRail.UI/Resources/ResourceRegistry.cs), [Rendering](src/SharpRail.UI/Rendering/SPEC.md), [Themes](src/SharpRail.UI/Rendering/Themes.SPEC.md), [Core PDF preview](src/SharpRail.UI/Resources/Pdf/SPEC.md), [Scintilla limits](src/SharpRail.Scintilla/README.md#limits).

| ID | Status | Gap |
| --- | --- | --- |
| U01 | Partial | Minimal Light and Minimal Dark now join the four original palettes. The latest independently tuned standard Light solid accent remains unported. |
| U03 | Partial | Dense named layout tokens, semantic typography and line heights are implemented, including the 40px location strip, 28px panel headers and 24px header controls. Remaining surface adoption and matched-content fidelity belong to the visual acceptance audit. |
| U04 | Missing | User-selected code font and ligatures across editor, terminals, Markdown code and diagrams. Font size controls are not font-family selection. |
| U05 | Partial | Consume manifest syntax colours consistently across source and Markdown code. Editor TextMate grammars and theme updates exist, but their palette is separately authored; code fences have narrower tinting. |
| U06 | Missing | Content-shaped loading skeletons and resolved-content fade-in. Several native placeholders remain text. |
| U08 | Partial | Shared tooltip timing and disabled-trigger wrapper; pointer-transparent fork tooltips need framework-specific acceptance. Avalonia tooltips and individual disabled-control tips already exist. |
| U09 | Missing | Shared non-modal dialog/inspector semantics, Escape and focus return while other controls remain usable. Most native dialogs are modal windows. |
| U10 | Missing | Toast swipe dismissal and timer pause on hover/focus; route remaining gesture/error-line notices through the bounded/coalesced toast queue. |
| U11 | Partial | Find paints every match rather than just selecting the current one. Terminal search and the full preview search surface remain narrower than the fork's expectation. |
| U12 | Partial | Workspace-search Markdown hit opens and flashes the matching rendered block. Navigation exists; block flash does not. |
| U13 | Missing | Registry advertises phone support, anchor geometry, copy/layout/whitespace and natural/bounded height per renderer. Current registry primarily declares matching/rank and view/diff factories. |
| U14 | Missing | Sanitized HTML preview and 2-up HTML diff. HTML currently opens as source; notebook HTML output also lacks the upstream sandboxed rich presentation. |
| U15 | Missing | PDF page-pair diffs, synchronized page sequence and 2-up/swipe/onion/difference comparison. PDF file preview is core functionality. |
| U16 | Partial | PDF viewport render window with two-page overscan, cancellation/release of offscreen canvases, page/zoom state restoration and page-region review focus. Current core preview rasterizes a full page set and has no review geometry. |
| U17 | Partial | Free image zoom/pan gestures and intrinsic-space region selections. Fit/natural/button zoom and four visual diff modes already exist. |
| U18 | Partial | JSON array move detection and stable object hashes. Native alignment presents moves as removed/added. JSON/JSONC trees themselves exist. |
| U19 | Partial | Notebook source-language treatment, rich HTML outputs and structural review/selection parity. Notebook view/diff exists; full upstream output and authoring contract does not. |
| U20 | Partial | Rendered Markdown raw HTML tables/lists/styles and text wrapping around floated pictures. Current limited HTML treatment can show text or place a floated picture on its own line. |
| U21 | Partial | Regroup a multi-block `<details>` in Markdown diffs; compare list items robustly when documents contain different counts of top-level lists. Current native diff has documented limits. |
| U22 | Partial | Per-tab code/table/JSON/notebook view state and delayed scroll restoration after detach/reattach. Markdown/source positions and image-mode state are implemented subsets. |
| U23 | Partial | Deleted-file banner in every applicable preview, not only source editor; other file formats mainly receive the deleted tab mark. |
| U24 | Partial | Editor text input exists, but full IME preedit, an accessibility text provider, touch selection handles and a copy menu are absent, confirmed by [Scintilla limits](src/SharpRail.Scintilla/README.md#limits). These are implementation gaps, followed by platform acceptance; completion/LSP workstation features are not automatically upstream requirements. |
| U25 | Partial | Native AppKit Edit role items work inside native picker fields, plus About. Current Avalonia menu uses app commands instead. |

### Host and transport gaps

Authorities: upstream [shared environment](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/shared/SPEC.md), [host](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/host/SPEC.md), [logging](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/log/SPEC.md), [watchers](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/watch/SPEC.md); SharpRail [Core](src/SharpRail.Host.Core/SPEC.md), [Files](src/SharpRail.Host.Core/Files.SPEC.md), [Git](src/SharpRail.Host.Core/Git.SPEC.md), [Client](src/SharpRail.Host.Client/SPEC.md), [Remote](src/SharpRail.Host.Remote/SPEC.md), [protocol counter](src/SharpRail.Host.Abstractions/Handshake.cs).

| ID | Status | Gap |
| --- | --- | --- |
| H01 | Missing | Bounded login-shell PATH/SSH_AUTH_SOCK repair and environment import for opted-in GUI launchers, inherited-value precedence, noisy-probe parsing, retry and excluded control/history variables. Terminal login-shell spawning alone does not fix the host's own child environment. |
| H02 | Missing | Structured leveled rotating host logs and crash report under state directory. Console diagnostics are not this persistent support surface. |
| H03 | Partial | One bounded idempotent graceful shutdown path awaited by every launcher, signal handling and in-flight work drain. Existing disposers are not evidence of the entire upstream contract. |
| H04 | Missing | Retrying recursive teardown and automatic first-free-port selection for launchers. Explicit listener configuration is a different contract. |
| H05 | Partial | Streamed bounded file/blob bytes and child output, with cancellation/nonzero/truncation semantics. Native single-message byte reads and callback-loaded images work, but do not offer streamed large-resource transfer. |
| H07 | Partial | Additive capability gates for later host operations such as path inspection/prewarm/spec queries. Current protocol is 6 and request replay is enabled; not every later operation has its own introduction gate. |
| H08 | Partial | Immutable original snapshot for index/working-side diffs. Index bytes can change between reads; commit-side scopes already resolve immutable originals. |
| H09 | Missing | Periodic identity healing when a watched root is replaced while all subscribers remain attached and no fresh admission occurs. Current watcher heals on a new subscriber/prewarm or error. |
| H10 | Missing | Watcher admission/activity lifecycle seam that seeds retained HEAD baseline before metadata events, including recreation and failed-registration cleanup. |
| H12 | Partial | Full frontmatter YAML semantics for graph metadata: anchors, nested maps and multiline scalars. The portable spec dialect is deliberately narrower; editing preview properties does not make graph parsing a full YAML parser. |
| H13 | Partial | Windows shell resolution, ConPTY and platform filesystem/native build paths are implemented. The Settings shell picker and per-shell startup guidance are missing; current guidance only distinguishes SHELL from the default. See [Windows](WINDOWS.md), [terminal contract](src/SharpRail.Host.Core/Terminals.SPEC.md) and [startup handling](src/SharpRail.Host.Core/PtyTerminalService.cs). Real Windows execution is tracked once under V09. |

### Fork plugin differences and acceptance gaps

These entries retain old omissions that a JetBrains-only delta audit would miss. Sources are [Fork port log](#fork-port-log), [E2E.md](E2E.md), owning module specs and implementation. Implemented plugin behavior is not listed as a feature gap merely because a native acceptance journey has not been run.

| ID | Status | Gap |
| --- | --- | --- |
| P01 | Partial | Plugin `WorkspaceUpdated` on actual branch switch; existing creation/removal/relabel notifications are narrower. [Host plugin runtime](src/SharpRail.Host.Core/Plugins.SPEC.md). |
| P03 | Partial | Faint-cell blanking when accessories read terminal buffer tail. Native text reads lack the cell-attribute information; picker placeholder detection can therefore differ. [Terminal](src/SharpRail.UI/Terminal/SPEC.md). |
| P04 | Partial | Claude IDE `openDiff` displays proposed unsaved content and returns real accept/reject/save results. It currently opens the target file and reports `diffShown: false`, also the fork's limitation; this is an integration opportunity rather than regression against that fork. [Claude](src/SharpRail.Plugins.ClaudeCode/SPEC.md). |
| P05 | Decision | Claude IDE diagnostics/execute-code capabilities. They are not offered; compare real supported CLI/IDE protocols before adding them. They are not declared fork parity requirements. |
| P06 | Partial | Codex IDE reply lists editors per answering frontend. Current app context unions editor tabs across windows in the workspace; background-window focus selection is otherwise implemented. [Codex](src/SharpRail.Plugins.Codex/SPEC.md). |
| P07 | Partial | Codex failed-state/summary notifications. Its current hook vocabulary has blocked/done but no failed status or summary; cancellation stays silent. Claude has richer failure notifications. |
| P08 | Partial | Codex skill-file ownership across multiple hosts and shutdown. User edits are protected, but disabling and host shutdown share disposal, and a second host is not coordinated. |
| P10 | Partial | Input sealing during picker driving is missing: [ClaudeTerminal.cs](src/SharpRail.Plugins.ClaudeCode/UI/ClaudeTerminal.cs) shows a notice without an exclusive-input overlay. Model/effort, attach, usage and plan controls exist; [Pickers.cs](src/SharpRail.Plugins.ClaudeCode/UI/Pickers.cs) implements draft refusal and Esc fallback. Live terminal-facts acceptance is included in V12 below. |
| P11 | Partial | Codex Windows IDE IPC is absent: [IdeBridge.cs](src/SharpRail.Plugins.Codex/Host/IdeBridge/IdeBridge.cs) declares Windows unsupported and [Codex checks](tests/SharpRail.Checks/CodexChecks.cs) skip it there. The registered model supplier is not consumed by a reactive Start-work selector. The failed recovery check is recorded under V06; installed CLI and native lifecycle acceptance is included in V12. |

### Notifications and platform acceptance gaps

Authorities: [UI](src/SharpRail.UI/SPEC.md), [terminal contract](src/SharpRail.UI/Terminal/SPEC.md), [browser](src/SharpRail.Browser/SPEC.md) and [browser acceptance](src/SharpRail.Browser/SPEC.md#acceptance). Implementation gaps are listed here; acceptance tasks follow separately.

| ID | Status | Gap |
| --- | --- | --- |
| V01 | Missing | Equivalent away notifications outside macOS and explicit system-permission state presentation; current denial can be silent. |
| V02 | Partial | Multiple attention notifications lead to a chooser. Current batch opens the latest terminal rather than letting the user choose among all waiting terminals. |
| V04 | Missing | Busy-close confirmation does not dismiss when another client closes the terminal. [TerminalTabs.cs](src/SharpRail.UI/TerminalTabs.cs) awaits a normal confirmation without catalog-driven invalidation; this remains an explicit [terminal contract](src/SharpRail.UI/Terminal/SPEC.md) implementation gap. |

### Outstanding acceptance work

These eight tasks consolidate the remaining checks; they do not assert missing product features. Owning specs retain durable verification scope and measurements; [the checks contract](tests/SharpRail.Checks/SPEC.md#recorded-verification-scope) distinguishes source and packaged evidence. Original row IDs in parentheses identify merged entries. No unrun journey is treated as passed.

| ID | Latest result | Remaining work |
| --- | --- | --- |
| V03 | Not run | In a signed macOS app, see a real away notification and click it to focus the correct terminal/window. Delivery/activation wiring exists and recording-notifier checks pass. |
| V05 | Not run | Compare native Settings, menus, Markdown, all plugins and docking at matched content/dimensions; exercise PDF trackpad magnify. Include drop/overflow/spacer/folded-weight geometry (L13, P15–P18). Functional/package checks pass, but there is no matched capture corpus. |
| V06 | Failed/incomplete | Complete the packaged gate after resolving its 2026-10-14 settings-observer timeout, Codex recovery failure and unfinished terminal/Blueprint cases; the later 131-case aggregate source pass is not packaged acceptance. Add direct regressions for reused-tab-ID workspace focus, held Visualize render/remount and same-terminal/two-window rendering, held graph requests after detach/disable, and held PDF read/raster replacement/detach (L07, P12, P14–P16). Release, formatting, package identity/signature and 13 targeted cases pass; the full gate does not. |
| V07 | Not run on required platforms | Exercise Safari/Firefox, non-macOS keyboards/IME, real notification permission and production HTTPS/proxies. Accept the Settings fix in a normal bundle and on physical Retina; its parallel-workspace Chromium test-build acceptance already passes. |
| V09 | 2026-10-14 audit: portable checks only | Execute the Windows package and its ConPTY, Recycle Bin, native editor/Skia, clipboard/IME and client journeys. Portable macOS checks skip Windows runtime branches. This covers the platform part of H13. |
| V10 | Emulator/fixture coverage only | Exercise physical Android, camera/QR and real two-device Tailscale pairing, approval, reconnect, revoke and remote UX, including packaged deployment. Emulator and fake-identity transport checks pass. |
| V11 | 120 Hz presentation not measured | Measure physical 120 Hz browser presentation. Recorded restty GPU draw cadence is about 60/s and output throughput is 111 FPS; neither proves the presentation target. No new benchmark was run. |
| V12 | Fixture coverage only | Exercise installed Codex/Claude during sustained streaming while typing/resizing/switching controls, interruptions, process replacement, auth/model changes, editor reload/remount/title changes, remote reconnect and physical drops (A36, P10–P11). Complete Claude pane-driven composers/account/usage/MCP health, nested source navigation after unsaved edits, uninstall/marketplace removal and overrides (P09); Blueprint live-model controls, frontmatter discard, outline, alerts and Mermaid (P12); and live Discord IPC/presence (P18). Generic host/UI tests do not establish these journeys. |

### Suggested implementation batches

These are priorities inferred from the comparison, not accepted feature designs.

1. Close the remaining normal-bundle, real-platform input and notification acceptance gates; browser Settings renderer ownership is implemented.
2. Finish retained-chrome, per-project frame, placement-picker and visual-fidelity gaps; rails, compatible panes, Home visibility and density/location typography are implemented.
3. Extend implemented activity/settling with the remaining workspace attention/read and PR-driven semantics.
4. Build review records/anchors and terminal send/resolve, then evaluate CLI run-boundary receipts for Last turn changes.
5. Close CLI configuration, MCP configuration and skills/design workflow gaps through the provider plugins and shared shipped skills.
6. Close resource parity: HTML/PDF diffs, renderer capabilities, view state, rich anchors and font/syntax consistency.

The inventory records implementation dispositions and outstanding decisions. Durable verification facts and their limits belong in the owning module specs; dated task continuations remain in context-log.md.
