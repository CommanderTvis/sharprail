# SharpRail instructions

.NET 10 C# solution (`SharpRail.slnx`): Avalonia workbench over a transport-independent host, with builtin plugins. Detail on demand: `docs/agent-reference.md` (project map, host-change layers, workbench partials, state and terminal details, check modes, artifact paths), `scripts/README.md`, module `SPEC.md` files, `SPEC.md`, `STATUS.md`, `context-log.md` (latest entries for continuation state), `gotchas.md` if present.

## Hard rules

- Never push unless the user asks. Preserve the commit-signing presence check; do not change Git configuration to work around a concurrent operation.
- CI babysitting. Every agent that pushes, or causes pushes or workflow dispatches (`ci.yml`, nightly, any workflow), MUST watch every run they trigger until all jobs finish. A push is not done until CI is green.
  - Poll with `gh run list` / `gh run view --json`, sleeping at least 60s between polls (Monitor or ScheduleWakeup, never a busy loop).
  - Read failures with `gh run view --job <id> --log-failed`, filtered by grep; never dump whole logs.
  - Diagnose, fix, and re-push or re-dispatch, but only where the user's push permission covers it. Fixes land in the commit chain per "Commit organization".
  - If still red, report exactly what and why (flaky/infra versus regression).
  - Report final run URLs and job conclusions.
  - Dependabot runs are excluded unless the push changed dependency locks.
- Inspect Git status before editing; leave other tasks' changes alone.
- Reuse the prepared workspace. For isolation use the SharpRail `workspace_create` tool and work in its path (it does not switch the terminal). `workspace_delete` only workspaces you created, after preserving their work; keep Default, others' workspaces and workspaces with active agents. New workspaces clone the git-ignored paths in `.sharprail/workspace.toml` (schema: `src/SharpRail.Host.Core/Workspaces.SPEC.md`); add an entry for a dependency or build-cache directory worth sharing.
- The UI thread never waits and does no Git, process, network or filesystem work, at startup or after (status, diffs, branches, worktrees, fetch, clone, spec indexing, plugin requests, profile reads). The embedded host is in-process, so awaiting a local adapter runs Core on the caller's thread until its first real async step: wrap such calls in `Task.Run` or make the adapter hop. No `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep`, `WaitForExit`, or a `lock` held across I/O. Return to the dispatcher only to apply results, after checking they still belong to the current workspace and were not superseded or cancelled. Long operations show progress and are cancellable. Handlers, constructors and `Build*`/render methods read in-memory state only.
- Do not run `scripts/tasks/*` directly; use `scripts/mise run <task>` (`scripts/mise tasks` lists them).
- Update the owning spec when changing its module. Keep the host independent of the UI; remoteness is an adapter choice, never a mandatory local daemon. No UI dependency in host projects.
- Keep user state in `~/.sharprail`, not project directories. Shareable project config is the exception: the project's `.sharprail/` as TOML (`language.toml`: `src/SharpRail.Host.Core/Language/SPEC.md`).
- Keep settings, presets, workspace labels, project list/recents and workspace lifecycle in host state (`IHostStateService`): change them through the host and update the UI on broadcast (`HostSync.cs`), never by writing a local copy.
- Static UI layouts, styles and templates go in compiled `.axaml`; dynamic docking and host/interaction wiring go in C#. Edit authored design sources (`src/SharpRail.UI/Rendering/Design`), not generated C#.
- Shared UI sources must compile for Android and Browser: desktop-only code goes under `#if !ANDROID && !BROWSER` or in files those projects exclude (see `docs/agent-reference.md`).
- pi and an embedded AI chat UI are out of scope; Codex and Claude Code terminal integrations are in scope.

## Build, format, check

- Toolchains come from mise. Run `scripts/mise run bootstrap` once. `.tools/dotnet/dotnet` is the SDK; `global.json` must name the version `mise.toml` pins.
- Build (compiler and warnings-as-errors gate; also builds native libraries whose inputs changed): `scripts/mise run build`.
- Packages: nuget.org only, locked restore; each project's `packages.lock.json` is committed (Android and Browser are not locked). Change packages with `scripts/mise run restore:update`; versions are pinned in `Directory.Packages.props`.
- Format touched files: `.tools/dotnet/dotnet format SharpRail.slnx --no-restore --include <files>`. CI verifies with `--verify-no-changes --no-restore`. Style comes from the checked-in `.editorconfig`.
- Checks: `.tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release [-- <mode>]`. Mode list: `docs/agent-reference.md`; add a mode in `tests/SharpRail.Checks/Program.cs` when a change has none.
- Browser WASM and Android build separately from `SharpRail.slnx` (`scripts/mise run browser`, `scripts/mise run android`).
- Documentation-only edits need whitespace and path verification, not a build or the gate.
- Report only checks actually run; name anything blocked.

## Verification budget

- Iterating: run only the focused mode for what changed, and format-check touched files.
- Run the full suite (argument-free) once per batch of related changes, after focused modes pass; never after each small fix or "to see what breaks". Never rerun tests on a byte-identical slice, including after a rebase.
- The suite runs from build output: never build, format, check out or rebase in a tree whose suite is running. Run long suites from a separate workspace; check `pgrep -fl SharpRail.Checks` first and identify each process's checkout.
- A failure ends the run: fix the cause, rerun its focused mode, then do one full run.
- After rewriting commits, verify the final tip only (Release build plus full suite).
- Fixture Git repos run unsigned under `IsolatedGit.ForProcess`; a signing prompt during checks is a harness bug, not a reason to wait.

## Scratch files

- Put temporary files in one task-specific subdirectory of `.bench` (git-ignored, disposable, small) and remove it when the task ends, including after failed checks. Keep no historical logs, captures, copied trees, build outputs, caches or archives; reuse or replace evidence instead of numbering copies.
- Keep `.bench/checks-last-run.txt` while `--last-failed` needs it. Never delete another active task's files.
- Record durable verification facts in owning module specs and dated task outcomes in `context-log.md`. Keep scratch evidence only if the user asks.

## Startup performance

Minimize launch-to-interactive-workspace time. Resolve only the identity needed for routing before mounting and restoring documents; load independent panel data progressively.

- Defer full Git snapshots, recursive scans, spec indexing, network requests, nonessential profile writes, hidden panels and inactive documents. Enumerate only files visible UI needs.
- Run blocking I/O and substantial parsing in the background. Async alone does not move work off the UI thread, and awaiting it before mounting still blocks startup.
- Release the project-switch gate before optional Git/indexing refreshes. Cancel superseded work; reject stale workspace results.
- Reuse the initial control tree, restore visible documents first, update only affected panel contents, and preserve dock chrome, tab instances, focus and pointer targets during deferred refreshes.
- Git or indexing failures must not block opening accessible files.
- Measure window creation, first presented frame and usable content separately; readiness logs do not prove presentation. Check the rendering backend and publish settings before tuning Skia/JIT/GC. Benchmark only when asked.
- Verify fresh and restored profiles, project-switch races, and usability during deferred loading.

## Commit organization

`main` is a chain on the shared history in the fork's order, so each part can be reviewed, ported or extracted alone:

1. General improvements that do not need the plugin API.
2. The Plugin API: contract assemblies, host plugin runtime, UI registry and kit, no plugins. If general and API changes cannot build apart, share one commit and say so in its message.
3. One commit per builtin plugin in order: spec dialect, Blueprint, Claude Code, Discord, Branch Graph, Visualize, Codex, then Air Context. A plugin commit carries its project folder, checks, license, package entries and its lines in shared registration files (`SharpRail.slnx`, project references, both `BuiltinPlugins.cs`, the check runner). File-type icons are core UI kit assets with a shared Appearance setting; PDF preview is a core resource renderer.

Status records (`STATUS.md`, `context-log.md`, notices) sit in one commit at the tip.

- Amend, do not append: fix an existing thing in the commit that owns it with `git commit --fixup=<sha>`, then `GIT_SEQUENCE_EDITOR=true git rebase -i --autosquash <base>`. A change touching two parts is two fixups. Rewrite only commits not yet on `origin/main` unless the user asks for a force-push.
- New commits only for a new general improvement (inserted into part 1, never appended at the tip), a new plugin (part 3), or a core API capability the API commit would be incomplete without. A capability a plugin introduced stays in that plugin's commit until generalised. A feature and its follow-up fixes end as one commit.
- Tests land with the code they test. Compare the final tree with the pre-rebase tree before replacing the branch.
