---
name: sync-upstream-specs
description: Pull spec changes from the upstream ThinkRail checkout into SharpRail's adapted C# specs and advance the tracked upstream commit. Use when upstream has moved, or the user asks to sync, update or pull upstream specs/improvements.
---

# Sync upstream specs

`UPSTREAM.md` is the single source of truth for the last synced upstream commit and maps upstream specs to SharpRail specs.
Upstream is `/Users/commandertvis/IdeaProjects/thinkrail`.

1. Read `UPSTREAM.md`. Let `OLD` be the synced commit. Refresh upstream only if the user asked
   (`git -C <upstream> fetch`); then pick `NEW` (default: upstream `origin/main` if fetched,
   else `main`). Stop if `OLD == NEW` and the spec sync was completed. If the pointer was
   advanced without syncing, recover the previous baseline from Git before comparing.
2. List what changed:
   `git -C <upstream> diff --stat OLD NEW -- '*SPEC.md' '*.SPEC.md' architecture.md goal-and-requirements.md apps/web/src/styles/`
   and `git -C <upstream> log --oneline OLD..NEW` for context.
3. Triage every changed file:
   - Mapped: port it (step 4).
   - Listed as not tracked, or out of scope (AI/agent/pi/chat, CLI, website, analytics, GitHub/PR,
     mobile, web-only tooling): skip.
   - New upstream spec: decide mapped or not tracked, add it to `UPSTREAM.md` accordingly.
   - Deleted or moved upstream spec: update the mapping; do not delete SharpRail specs unasked.
4. Port each mapped change from `git -C <upstream> diff OLD NEW -- <file>`, not the whole file:
   - Translate TS/React/Electron concepts to the real C# counterpart; grep SharpRail to confirm
     names exist. Keep the destination's upstream source path and a relative Markdown link
     to `UPSTREAM.md`; do not duplicate commit hashes or dates in module specs.
   - Behavior SharpRail does not implement goes into the destination's `## Not yet ported` section;
     remove bullets from it when upstream drops the behavior.
   - Keep specs concise: intent, decisions, invariants; no restated code; no bold-lead-in bullets.
   - With more than ~5 destinations, fan out to parallel sub-agents, one group of related specs each,
     passing these rules and the exact diff range.
5. Separately list upstream changes that imply code work in SharpRail (new behavior, changed
   invariants). Report them; do not implement them unless the user asks.
6. Only after triage and spec porting are complete, update the synced commit and date in
   `UPSTREAM.md`. This records spec synchronization, not implementation completion; the
   destination specs' `Not yet ported` sections record remaining code work. Verify every
   mapped destination path and revision link exists and the sync adds changes only to specs,
   `UPSTREAM.md`, and this skill. Preserve other authorized changes. Do not commit unless asked.
