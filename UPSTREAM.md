# Upstream spec tracking

SharpRail's module specs are adapted from the upstream ThinkRail checkout at
`/Users/commandertvis/IdeaProjects/thinkrail`. The `sync-upstream-specs` skill
(`.claude/skills/sync-upstream-specs/SKILL.md`) pulls later upstream changes and advances
the synced revision below. This file is the single source of truth for the
upstream revision; module specs link here rather than recording their own hashes.
The revision records completed spec triage and adaptation, not implementation
completion; remaining code work lives in each spec's `Not yet ported` section.

Synced commit: `fdebcdd532a5a3955539e96439f2ddae074d18ec` (2026-10-07)

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

The former `apps/web/src/components/ui/SPEC.md` moved to `packages/ui/SPEC.md`;
the existing Rendering spec remains its native counterpart. Typography rules now
map to that spec as well. Style-usage test changes inform the documented validation
gaps rather than copying TypeScript tooling.

New `apps/web/src/extensions/SPEC.md`, `packages/extension-api/SPEC.md`,
`thinkrail-extensions/SPEC.md`, `thinkrail-extensions/visualize/SPEC.md`,
`pi-extensions/SPEC.md` and `pi-extensions/visualize/SPEC.md` are not tracked:
they compose AI tools and their host/web SDK, outside SharpRail's current scope.
Their architecture and boundary changes do not introduce an extension SDK here.

Changed CLI, website, CI/release, web profiling scripts, provider-auth UI, prompt,
chat/resources/tools, updates, artifact analytics, agent/review/analytics services
and pi/website-analytics specs remain out of scope. The product-goal change is AI
positioning. AI-only portions of mapped specs (model controls, agent naming,
session switching and working badges) are likewise excluded; applicable native
workbench contracts are adapted in the mapped specs.
