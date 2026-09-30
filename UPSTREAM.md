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
| `packages/server/src/spec/SPEC.md`, `packages/spec-graph/SPEC.md`, `packages/spec-graph/core/SPEC.md` | `src/SharpRail.Host.Core/Specs.SPEC.md` |
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

