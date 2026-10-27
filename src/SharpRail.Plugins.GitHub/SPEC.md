---
id: module-plugin-github
type: module-design
status: active
title: GitHub — pull requests and GitHub sign-in, as a builtin repo hosting plugin
parent: architecture
references: [module-plugin-api, submodule-host-plugins, submodule-host-git, submodule-ui-plugins]
tags: [plugins, git]
---

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
branch status and Open PR's push step keep working; Open PR then pushes and reports `pushed`. With it off, a
GitHub remote is simply a remote.

## Limits

- Only `github.com`; no Enterprise Server.
- Needs `gh` on the host's PATH; the browser never runs it (the host probes authentication, as before).
- Fix with agent, templates, description sources and Merge locally stay unported.

## Implementation status

Implemented in strangler slices (host seam, plugin projects, UI half, wire): `GitHubProvider` runs every `gh` call through
the host's bounded runner; the Pull request item and the Review section are one W28 registration (`RepoHostUI`, over the
window's `IRepoHostSurface`; the section is not a W23 slot because it needs the window surface); the Settings page is a W4
section reading sign-in through the plugin's `signIn` method when shown and every 30 s; the wire is protocol 15 (neutral
`PullRequest` in field 14, provider = plugin id). Category is Version Control. A compact (phone) workbench builds the same location
strip, so the item shows there. Checks: `--github` (provider, cache, failures, plugin off = plain remote), `--branch-status`, `--review`,
`--settings`, `--shell`. Open: the merge dialog's remembered choice still lives in the UI profile (`MergePreferences`,
`method|delete` or `method|keep`) behind the surface rather than in plugin state (H14); the Settings page does not follow a host
broadcast; no `--plugins` case switches the plugin off through the registry.
