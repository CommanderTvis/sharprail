# Upstream spec tracking

SharpRail's module specs are adapted from the upstream ThinkRail checkout at
`/Users/commandertvis/IdeaProjects/thinkrail`. The `sync-upstream-specs` skill
(`.claude/skills/sync-upstream-specs/SKILL.md`) pulls later upstream changes and advances
the synced revision below. This file is the single source of truth for the
upstream revision; module specs link here rather than recording their own hashes.
The revision records completed spec triage and adaptation, not implementation
completion; remaining code work lives in each spec's `Not yet ported` section.

Synced commit: `68bb837474bf57fea1be6d9d9735172ab42b18e6` (2026-10-08)

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

Compared `fdebcdd532a5a3955539e96439f2ddae074d18ec` with the fetched
JetBrains/thinkrail `upstream/main`: 20 commits. The checkout's `origin` is the
personal fork and has no `origin/main`; the authoritative remote is `upstream`.

Applicable changes are adapted in the owning specs:

- `packages/shared/SPEC.md`: always probe the Unix login-shell PATH and merge
  launcher-specific entries ahead of it; Core records this existing implementation gap.
- `packages/server/src/terminal/SPEC.md` and `packages/server/src/host/SPEC.md`:
  validate terminal grids and guarantee backpressure progress. C# input is already
  typed; Bun's lost-drain latch is not a demonstrated gRPC bug. Bounded output queues
  and progress guarantees remain native work.
- `packages/server/src/settings/SPEC.md`: mutation keys/values are already validated;
  retaining unknown settings on disk across older-host updates remains missing.
- `packages/contracts/SPEC.md`: RPC interfaces already enforce adapter completeness
  at compile time. Native attention-notification bridges are excluded.
- New `apps/web/src/shell/locationBar/SPEC.md` maps to the UI spec: captioned
  project/workspace/branch controls, responsive priorities and shared workspace actions.
  The related shell/panels changes also require workspace-bound rename/remove state.
- `apps/web/src/shell/layout/SPEC.md`: document the remaining layout-isolation
  validation gap without copying React component boundaries.
- `apps/web/src/panels/SPEC.md` and the renderer source fix: preserve pending scroll
  restoration until content is ready or user input takes over; PR-review cache changes
  remain excluded.
- `apps/web/src/lib/SPEC.md` and `styles/TYPOGRAPHY.md`: native highlighting work must
  avoid dispatcher stalls and stale replies; the new location caption is unported.
  Worker engines and generated CSS remain web-specific. Added muted feedback colors
  serve AI Plan/TODO status, so introduce no native color roles in this sync.
- `e2e/SPEC.md`: record login-shell fixture isolation needed with PATH repair.
  Heavy chat replay, CPU throttling and React profiling remain web/AI tooling;
  no benchmarks or coverage translations were run or claimed.

Mapped `architecture.md`, server parent, web parent, desktop, store and spec-graph
changes otherwise concern AI extensions, native agent-attention notifications or
web bundling, so their native counterparts need no additional adaptation.

New `apps/web/src/notifications/SPEC.md` and
`packages/server/src/extensions/SPEC.md` are not tracked: agent attention and AI
extension composition are outside scope. Deleted `packages/pi-visualize/SPEC.md`
was not tracked; its replacement lives in the already excluded pi/ThinkRail
extension specs. No SharpRail spec is deleted.

The remaining changed specs are excluded: CLI; website and vibecoding;
artifact-tests; server agent and analytics; pi-delegation, pi-thinkrail-workflow,
pi-todos (parent/core/tools); website-analytics; pi-extensions (parent/visualize);
thinkrail-extensions (parent/visualize). Their source changes cover AI chat,
TODOs, delegation, analytics, packaging and extension migration. The changed
product goal concerns the loose TODO queue and is likewise outside scope.

This sync changes documentation only. Implementation gaps remain in the module
specs' `Not yet ported` sections; advancing this revision does not claim code parity.
