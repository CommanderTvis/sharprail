---
id: module-plugin-github
type: module-design
status: draft
title: GitHub — pull requests and GitHub sign-in, as a builtin repo hosting plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins, submodule-host-git, submodule-ui-plugins]
tags: [plugins, git]
---

> Progress notes (remove when the plugin lands). Strangler plan, one slice per commit. Slice A done: `ProjectServices` takes optional `repoHosts` (url to `RepoHostRoute`) and `branchStatusObserved` seams, wired from `PluginRuntime.RepoHostFor`/`NotifyBranchStatus` in App.cs and RemoteServer; every `gh` call now runs through the internal `LegacyGitHubProvider` (Host.Core) behind `RepoHostProvider`; DTOs and wire unchanged (the host maps the neutral model back to GitHub's spellings). Slice B done: `SharpRail.Plugins.GitHub` Contract/Host/UI exist and are registered (Host.Core and UI `BuiltinPlugins.cs`, UI/Android/Browser csprojs, slnx); the provider is `GitHubProvider` in the Host project (runs `gh` through `IPluginHostContext.RunToolAsync`), the core fallback is deleted, so a GitHub remote with the plugin off is a plain remote; sign-in without a recognized remote asks the first active provider (`RepoHostFor("")`). Category is Version Control (resolved). The `--github` mode replaces `--pull-requests` and includes the provider-off case; checks that run `ProjectServices` without a runtime pass `PullRequestChecks.GitHubRoute`. The UI half is still empty. Remaining slices: C (UI half), D (wire cleanup and version bump).
> Slice C design notes (not started in code). Coupling found: `BranchShip.cs` (Pull request half), `ReviewPull.cs`, `PrDialogs.cs` and
> `GitPanels.cs:480` (`ReviewPullRequestHost`) use `WorkbenchWindow` state (`git.Branch`, `host`, `profile.Data.MergePreferences`,
> `Toasts`, `Dialogs` in `SharpRail.UI/Panels`, `OpenLink`, the `remote` flag, `RequestBranchStatus`), none reachable from a plugin UI
> project, which references only `Plugins.Api.UI` and `UI.Kit`. Moving them therefore needs one core-implemented surface handed to the
> plugin through W28 (status view of the branch with the neutral request, apply/preview/open-PR calls that delegate to `IProjectServices`
> so they work remotely, toast, confirm/notice dialogs, clipboard, open-link, the remembered merge choice) plus: a `LocationItemRegistration`
> hosted by `LocationStrip` after `BranchSegment` and next to `RemoteSegment` (the PR segment in `WorkbenchWindow.axaml:156` becomes a
> plugin-built control keeping the 14 + 20 row grid, the 16 px icon slot `LayoutRoles.LocationIconSlot` and `Margin="8,0,0,0"` row), the
> `core:review-pull-request` slot in the Review panel, a Settings section for the GitHub page (auto refresh on show, status broadcast
> and a gentle poll, no Refresh button), and moving `SettingsGitHubE2E`, the PR half of `ReviewChecks` and `LocationBarChecks` to `--github`.
> Phone: the location strip is built identically for compact workbenches (`BranchShip` ran regardless of `workbench.Compact`; only
> `PluginHeaderItems` is hidden there), so location items show on phones too; W24's "shows none" applies to header items only. Done: the host half of the provider API (checklist 4, host part): `RepoHost.cs`
> in `Plugins.Api.Host`, `RepoHost`/`OnBranchStatus`/`RunToolAsync` on the host context, and `PluginRuntime.RepoHostFor(url)`
> / `NotifyBranchStatus`. Remaining: 1, 2, 3, 4 (UI half: W28 `LocationItem`, `core:review-pull-request` slot), 5 to 9.
> Findings for the successor. (a) `ProjectServices` has no handle on `PluginRuntime`; it is built in many places
> (`new ProjectServices(root, state, plugins.AllowsExternalFile)`), so routing needs a constructor seam of the same kind,
> for example `Func<string, (string PluginId, RepoHostProvider Provider)?>` from `PluginRuntime.RepoHostFor`, plus a
> `Func<BranchStatusEvent, ...>` for `NotifyBranchStatus`; with no seam the host reports a plain remote. (b)
> `Host.Abstractions` cannot reference `Plugins.Api.Host`, so the neutral `PullRequest` exists twice (Abstractions for the
> wire and UI, Api.Host for providers) and Core maps between them. (c) The merge dialog's preference is the UI profile's
> `MergePreferences[projectRoot]` (`method|delete` or `method|keep`). (d) `BranchStatusChecks`, `PullRequestChecks` and
> `E2eWorkspace` wrap `IProjectServices` and must follow its signature changes.

# GitHub — pull requests and GitHub sign-in, as a builtin repo hosting plugin

## Responsibility

`GitHub` (`github`, enabled by default, category Version Control, no dependencies, wire version 1) is
the first provider of the generic repo hosting surface ([Plugins](../SharpRail.Host.Core/Plugins.SPEC.md#repo-hosting-providers-h24-w28),
Plugin API H24 and W28). It owns everything that is GitHub and not git:

- Recognition: a remote URL belongs to it when its host is `github.com` (https, ssh and scp forms; enterprise hosts are
  not recognized).
- The `gh` CLI, the only way it talks to GitHub: `gh auth status`, `gh pr list/create/edit/ready/merge/reopen`,
  `gh repo view` for the allowed merge methods (cached per remote for ten minutes). No tokens are read or stored.
  `SHARPRAIL_GH_OFFLINE=1` makes every lookup and mutation report unavailable.
- The mapping from GitHub's values to the provider-neutral `PullRequest` (below), including GitHub's merge state and
  status-check rollup.
- Its UI: the Pull request segment and next-step button of the location strip, the pull request section of the
  Review panel (compose, push updates, merge dialog, setup guidance), the compare-link fallback, and the Settings
  page `GitHub` (sign-in status from `gh auth status`, Refresh). Wording stays "PR".

It does not run `git`: pushes, fetches, ahead/behind and the remote branch stay in the host.

## What stays in the host and what moves

Stays: `origin` and remote-URL reading, `GetBranchStatusAsync` git facts (has origin, published, ahead, behind, ahead of
base, on base, HEAD), push (never forced, `confirmBase`), delete-remote-branch (leased `git push origin --delete`),
the Remote segment and its Publish/Push/Sync step, the Open-review guards and push step, the draft proposal (branch
name and commit subjects), request budgets, the lookup cache and routing ([Git](../SharpRail.Host.Core/Git.SPEC.md)).

Moves here: every `gh` call (`PullRequests.cs`, `GitHubAuthentication.cs`, the `gh` parts of `BranchStatus.cs`),
`ReviewDetails` and merge-method discovery, the GitHub Settings page, `BranchShip.cs`' pull request half,
`ReviewPull.cs`, `PrDialogs.cs` and `GitHubStatus`, and the protocol's GitHub RPC (`GitHubContract.cs`,
`GitHubRpc.cs`, `GitHubAdapters.cs`), replaced by plugin methods.

Out of scope and unchanged: the `shipping-a-pr` skill and other agent workflows (they instruct an agent to use `gh`
itself; they are not a host feature), GitHub URLs in docs and in the Claude Code marketplace source field, and issue
references in commit messages.

## Data contract

The host sees only the neutral `PullRequest`; GitHub's own strings never cross the boundary.

| field | values |
| --- | --- |
| `number`, `title`, `url` | the pull request's identity; `url` is https or the result is dropped |
| `state` | `open`, `merged`, `closed` |
| `draft` | bool |
| `checks` | `passing`, `failing`, `pending`, `none` |
| `review` | `approved`, `changes-requested`, `required`, `none` |
| `mergeability` | `clean`, `blocked`, `behind`, `conflicting`, `unknown` (GitHub's merge state, never approvals) |
| `mergeMethods` | subset of `merge`, `squash`, `rebase` the repository allows |
| `sameRepository` | false when the head is a fork's branch; the provider refuses actions on it |
| `unpushed`, `behind` | commit counts against the pull request head; -1 when unknown |

The lookup returns the open pull request of the branch, else the newest in any state, else `null`.

## Failure and timeouts

Everything runs on pool threads; the UI thread only applies results after checking the workspace and branch still
match (AGENTS.md). A query has the 8 second budget and kills the child's process group; a mutation has the
5 minute deadline. A missing or unauthenticated `gh`, a timeout, a non-https link or an exception reads as "no pull
request" for lookups (logged against the plugin, never cached) and as a `Problem` of `missing` or `unauthenticated`
for mutations, which the host turns into the compare-link outcome with the setup guidance the UI half draws.

## Settings and migration

The plugin has no settings of its own beyond `enabled`; the old Settings › GitHub page held none. The merge dialog's
remembered method and branch-deletion choice per project (today the UI profile's `MergePreferences[projectRoot]`, the string `method|delete` or `method|keep`) moves to the plugin's state
(H14) on first run; an unreadable old value falls back to the dialog defaults. Profiles without an explicit
`plugins.github.enabled` get the plugin on.

## Switching off

Switching `GitHub` off (plugin enable switch, no dependents to cascade) removes only its segment, Review section,
Settings page and the `gh` calls; the host drops the cached lookups and reports no review. Remote, Publish, Push, Sync,
branch status and `OpenReview`'s push step keep working; Open PR then pushes and reports `pushed`. With it off, a
GitHub remote is simply a remote.

## Limits

- Only `github.com`; no Enterprise Server.
- Needs `gh` on the host's PATH; the browser never runs it (the host probes authentication, as before).
- Fix with agent, templates, description sources and Merge locally stay unported.

## Implementation checklist (nothing below exists yet)

1. Host contract (Abstractions, Core): add the neutral `PullRequest`, `RepoHostProvider` and H24 registration; replace
   `OpenReview`/`ReviewDetails` with it; make `BranchStatus.Provider` the matched provider id (empty when none) and
   `PrResult.GhProblem` the neutral `Problem`; remove `GitHubAuthentication` from `IProjectServices`.
2. Host.Core: provider registry, routing by `origin` URL, the generic cache, `OnBranchStatus`; strip `gh`, `IsGitHub`
   and `IsGitLab` from `BranchStatus.cs`, `PullRequests.cs` and delete `GitHubAuthentication.cs`.
3. Protocol and Client: remove `GitHubContract.cs`, `GitHubRpc.cs`, `GitHubAdapters.cs`; update the status DTOs
   (`ProjectContract.cs`, `ProjectRpc.cs`, `ProjectAdapters.cs`); bump the host protocol version
   (`HostCapabilities`); a client that predates it reports no review.
4. Plugin API: H24 and W28 (and the `core:review-pull-request` slot of W23) in `Plugins.Api`, `Plugins.Api.Host`,
   `Plugins.Api.UI`.
5. New projects `SharpRail.Plugins.GitHub` (`Contract`, `Host`, `UI`) with locks and the solution entry; move the code
   named above; `GitHubPlugin.Manifest`, `WireVersion = 1`.
6. Register in `src/SharpRail.Host.Core/Plugins/BuiltinPlugins.cs`, `src/SharpRail.UI/Plugins/BuiltinPlugins.cs`,
   `SharpRail.UI.csproj` and the Android and Browser csprojs (UI half).
7. UI core: `BranchShip.cs` keeps Remote only; `LocationStrip` hosts W28 items; remove the Settings GitHub entry and
   `GitHubSettings()`; Review panel keeps Show Changes and exposes the slot.
8. Checks: move `PullRequestChecks`, `ReviewChecks`, `BranchStatusChecks` (pull request half) and
   `SettingsGitHubE2E` behind a `--github` mode against a fake `gh`; add to `--plugins` the off case (no segment,
   no `gh` spawned, push still works) and to `BoundaryChecks` that Host.Core and UI core spawn no `gh`; keep
   Remote tests in the core mode.
9. Docs: `docs/develop/agent-reference.md` project map and check modes, `STATUS.md`, `context-log.md`.
