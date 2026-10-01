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

Synced commit (`main`): specs follow JetBrains `830de941a12905c54ff7c0418acc6bde2d9a5157`
(2026-10-09), inherited from the `upstream` branch, which `main` is rebased onto. Fork code
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
| `packages/server/src/spec/SPEC.md`, `packages/spec-graph/SPEC.md`, `packages/spec-graph/core/SPEC.md` | `src/SharpRail.Host.Core/Specs.SPEC.md` |
| `packages/server/src/{terminal,subprocess}/SPEC.md` | `src/SharpRail.Host.Core/Terminals.SPEC.md`, `src/SharpRail.UI/Terminal/SPEC.md` |
| `packages/contracts/SPEC.md` | `src/SharpRail.Host.Protocol/SPEC.md` |
| `apps/web/src/transport/SPEC.md` | `src/SharpRail.Host.Client/SPEC.md` |
| `packages/server/src/auth/SPEC.md` | `src/SharpRail.Host.Remote/SPEC.md` |
| `apps/web/SPEC.md`, `apps/desktop/SPEC.md`, `apps/web/src/resources/SPEC.md` | `src/SharpRail.UI/SPEC.md` |
| `apps/web/src/shell/SPEC.md`, `apps/web/src/shell/{layout,layoutState}/SPEC.md` | `src/SharpRail.UI/Docking/SPEC.md` |
| `apps/web/src/shell/locationBar/SPEC.md` | `src/SharpRail.UI/SPEC.md` |
| `apps/web/src/store/SPEC.md`, `apps/web/src/navigation/SPEC.md` | `src/SharpRail.UI/State/SPEC.md` |
| `apps/web/src/panels/SPEC.md` | `src/SharpRail.UI/Panels.SPEC.md`, `src/SharpRail.UI/Panels/SPEC.md` |
| `apps/web/src/components/SPEC.md`, `packages/ui/SPEC.md`, `apps/web/src/lib/SPEC.md`, `apps/web/src/styles/TYPOGRAPHY.md` | `src/SharpRail.UI/Rendering/SPEC.md` |
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

Compared `68bb837474bf57fea1be6d9d9735172ab42b18e6` with the upstream
checkout's local `main`: one commit, `feat(workspaces): settle quiet workspaces
onto a per-project shelf (#663)`. No fetch was performed. The checkout's
`origin` is the personal fork; JetBrains/thinkrail is its `upstream` remote.
Triage completed on 2026-10-10; the revision date above is the commit date.

All twelve changed specs were triaged. Nine mapped specs add applicable behavior,
recorded in the native destinations' `Not yet ported` sections:

- `packages/server/src/workspaces/SPEC.md`: host-owned activity timestamps and
  explicit settle/keep-active overrides, safe write coalescing, creation stamps,
  HEAD baselines and one-time migration. Settling organises the list without
  removing a worktree; Default never settles. Native activity sources are accepted
  PTY input and actual HEAD movement; viewing or selecting is not activity.
- `packages/server/src/host/SPEC.md`: compose these activity sources and migration
  without allowing lifecycle-maintenance failures to block listing or kill the host.
- `packages/server/src/watch/SPEC.md`: seed HEAD before metadata events, compare
  the retained baseline on watcher recreation and clean up failed admission.
- `packages/server/src/terminal/SPEC.md`: attribute only input that reaches the
  PTY to its workspace; displaced, exited and ignored writes must not reactivate it.
- `packages/server/src/settings/SPEC.md`: shared idle window, seven days by
  default, positive integer clamped to 1–365 or null for Never; malformed stored
  values restore the default.
- `packages/contracts/SPEC.md`: additive activity/override facts, settle/unsettle
  mutations and nullable idle setting, with a native capability version and
  local/remote parity. The client derives the partition; it is never a wire boolean.
- `apps/web/src/store/SPEC.md`: one partition for rail and header, Recent activity /
  Created / Name sorting, Default pinned first, active-selection latch and a shared
  clock. Native sort preferences belong in the profile; shelf expansion and paging
  stay transient per window. Unsupported hosts retain their legacy list, and a
  temporary disconnect must not discard the last completed capability verdict.
- `apps/web/src/panels/SPEC.md`: collapsed Settled shelf, reason chips, paging,
  shared Settle / Keep active actions, force-reveal of selected rows and a one-time
  automatic-move notice with Show. Workspaces settings offers 1 / 3 / 7 / 14 days /
  Never and does not mislabel valid non-preset values.
- `apps/web/src/shell/locationBar/SPEC.md`: the same actions and sorted partition
  in the workspace switcher, a Settled submenu and a settled selection caption.

The three changed untracked specs remain excluded:
`packages/server/src/branch-review/SPEC.md` (open/merged/closed provider lookup,
reliable review snapshots and bounded asynchronous refresh),
`packages/server/src/host/planReview.SPEC.md` (AI review as activity), and
`packages/server/src/pr/SPEC.md` (branch-safe PR snapshot mutation). The review
snapshot, PR-driven settling/chips and AI session activity/backfill portions of
mapped specs are likewise excluded under this sync's existing scope. No specs
were added, moved or deleted; the mapping is unchanged.

Source changes reinforce the same feature. New `e2e/settled-shelf.spec.ts` covers
manual settle/keep-active, the first-move notice, legacy-host behavior and persisted
idle settings; these remain untranslated acceptance gaps. Existing terminal,
workspace-tab and topbar journeys now locate rows by identity because sorting
invalidates positional assumptions. Chat history journeys, Plan/PR changes,
product analytics, React hydration, compact age formatting and README screenshots
add no independent native implementation work beyond the shelf or excluded scope.

Implementation work remains: host lifecycle facts and activity wiring, shared
idle settings and compatible adapters, then the shared UI partition, shelf,
header actions and notice with local/remote and multi-window acceptance coverage.
This sync changes documentation only; no implementation or translated coverage
is claimed, and advancing this revision does not claim code parity.

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
- `1e4449a2a` A repository URL becomes a project, cloned into a folder you choose: skipped (not yet ported: no Clone repository… dialog and no host clone operation yet; the remaining diff deletes web-only unit tests)
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
- `5bdc70e39` Vertical tabs can live under their workspace in Projects: skipped (a new tab-placement mode inside the Projects rail with a large layout-model change; not attempted in this pass)
- `ba90884c6` A dirty worktree can be force-removed before its branch: skipped (needs the topbar branch list with branch deletion, which SharpRail does not have)
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
