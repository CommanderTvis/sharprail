---
name: sync-upstream-specs
description: Pull spec changes from the ThinkRail line the current SharpRail branch tracks (the CommanderTvis fork on main, JetBrains on the upstream branch) into SharpRail's adapted C# specs and advance the synced commit. Use when upstream or the fork has moved, or the user asks to sync, update or pull upstream specs/improvements.
---

# Sync upstream specs

`STATUS.md` (section Upstream tracking) records the last synced commit and maps upstream specs to SharpRail specs.
The ThinkRail checkout is `/Users/commandertvis/IdeaProjects/thinkrail`; which ref to sync
depends on the SharpRail branch (`git branch --show-current`):

- `main`: the fork, remote `origin`, ref `origin/claude-code-integration-plugin-api`.
- `upstream`: JetBrains, remote `upstream`, ref `upstream/main`.

On any other branch, ask which line it follows.

1. Read the Upstream tracking section of `STATUS.md`. Let `OLD` be the synced commit. Refresh only if the user asked
   (`git -C <checkout> fetch <remote>`); then pick `NEW` (default: the branch's ref above).
   Stop if `OLD == NEW` and the spec sync was completed. If the pointer was advanced
   without syncing, recover the previous baseline from Git before comparing. The fork is force-pushed: if `OLD` is not an ancestor of `NEW`, find
   the rebased counterpart by the recorded commit title and use it as `OLD`.
2. List what changed:
   `git -C <upstream> diff --stat OLD NEW -- '*SPEC.md' '*.SPEC.md' architecture.md goal-and-requirements.md apps/web/src/styles/`
   and `git -C <upstream> log --oneline OLD..NEW` for context.
3. Triage every changed file:
   - Mapped: port it (step 4).
   - Listed as not tracked, or out of scope (pi, AI chat, CLI, website, analytics, GitHub/PR,
     mobile, web-only tooling): skip. On `main`, the fork's plugin specs (`packages/plugin-*`,
     `packages/server/src/plugins`, `apps/web/src/plugins`) are in scope.
   - New upstream spec: decide mapped or not tracked, add it to the mapping in `STATUS.md` accordingly.
   - Deleted or moved upstream spec: update the mapping; do not delete SharpRail specs unasked.
4. Port each mapped change from `git -C <upstream> diff OLD NEW -- <file>`, not the whole file:
   - Translate TS/React/Electron concepts to the real C# counterpart; grep SharpRail to confirm
     names exist. Keep the destination's upstream source path and a relative Markdown link
     to `STATUS.md`; do not duplicate commit hashes or dates in module specs.
   - Behavior SharpRail does not implement goes into the destination's `## Not yet ported` section;
     remove bullets from it when upstream drops the behavior.
   - Keep specs concise: intent, decisions, invariants; no restated code; no bold-lead-in bullets.
   - With more than ~5 destinations, fan out to parallel sub-agents, one group of related specs each,
     passing these rules and the exact diff range.
5. Refresh the E2E porting inventory in `STATUS.md`: compare the `test(` titles of `e2e/**/*.spec.ts` at
   `OLD` and `NEW`, mark each added or removed case translated, backlog or excluded, and verify "translated"
   against `tests/SharpRail.Checks/E2E` rather than the earlier rows.
6. Separately list upstream changes that imply code work in SharpRail (new behavior, changed
   invariants). Report them; do not implement them unless the user asks.
7. Only after triage and spec porting are complete, update the synced commit and date in
   `STATUS.md`. This records spec synchronization, not implementation completion; the
   destination specs' `Not yet ported` sections record remaining code work. Verify every
   mapped destination path and revision link exists and the sync adds changes only to specs,
   `STATUS.md`, and this skill. Preserve other authorized changes. Do not commit unless asked.
