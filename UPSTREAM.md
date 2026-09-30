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

