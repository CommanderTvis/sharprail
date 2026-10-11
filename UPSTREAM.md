# Upstream spec tracking

SharpRail's module specs are adapted from the upstream ThinkRail checkout at
`/Users/commandertvis/IdeaProjects/thinkrail`. The `sync-upstream-specs` skill
(`.claude/skills/sync-upstream-specs/SKILL.md`) pulls later upstream changes and advances
the synced revision below. This file is the single source of truth for the
upstream revision; module specs link here rather than recording their own hashes.
The revision records completed spec triage and adaptation, not implementation
completion; remaining code work lives in each spec's `Not yet ported` section.

Synced commit: `f7cca7bfb0b1d0543eb9898c52d28df5a784eb09` (2026-10-11)

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
Remaining native work is recorded in the owning specs. Git graph work belongs to
another branch and is outside this task.
