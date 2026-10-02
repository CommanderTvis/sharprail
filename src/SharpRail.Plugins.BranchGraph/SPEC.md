---
id: module-plugin-branch-graph
type: module-design
status: active
title: plugin-branch-graph — the branch graph as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api, module-contracts, module-plugin-ui]
references: [module-server, module-web]
tags: [v1, plugins]
---

## Responsibility

Owns the Git Graph side tool: the project's branches drawn as commit history, paged and lane-laid-out
in the right rail. This ports CommanderTvis's fork commit `e91aef250`, with the current fork's
`packages/plugin-branch-graph` as reference. Enabled by default, no dependencies, wire version 1.
The manifest declares `graph`, label Git Graph, icon `git-branch`, right side and `RequiresGit`.
The host and UI assemblies reference only their plugin API entry and shared contract; the UI also uses the kit.

## Contract and host

- `graph({ projectId, skip? })` returns `{ commits, worktrees, hasMore }`. Each commit carries sha,
  shortSha, parents, refs, subject, author and committedAt. Worktrees carry sha, name and optional workspaceId.
- `patch({ projectId, sha })` returns `{ patch }` from `git format-patch -1 -m <sha> --stdout`.
  Git revision expressions are accepted, matching the fork. Nothing in this plugin mutates a repository.
- Project lookup uses the host's project list; an unknown project fails with its id.
  `graph` uses the project's path, the generic host Git runner and the project's workspaces.
- Log arguments mirror the fork: branches, date order, nonnegative skip, 401 commits and fixed NUL-separated
  fields. A page returns at most 400 commits; the extra commit answers whether another page follows.
  Subjects and authors strip control, invisible and directional characters. Decorations drop `HEAD ->` and bare `HEAD`.
- Worktree porcelain output pairs canonical paths with known workspaces; unmatched trees use the path's basename.
  Symbolic links, including macOS `/var` versus `/private/var`, are resolved before matching.
- A failed Git launch or process operation reports Could not read the history / Could not generate patch
  with the runner's detail. A nonzero or empty log without a runner failure returns an empty graph.
  An unsuccessful worktree listing contributes no marks.

## UI

- `BranchGraphUI` registers `GraphPanel` under the manifest's tool. Its project is the one owning the panel's own workspace in the
  projection's `Workspaces`, not `ContextProjectId`: SharpRail's projection follows the active window, while
  each fork web client has its own, so this keeps a graph in a background window on its project. Workspace revision changes reread history from the tip. A project change resets history.
  Late reads after a newer refresh or detach are discarded.
- Workspaces of the same repository reuse the graph, its rows and scroll position without rereading
  history merely for navigation. Commit actions target the currently mounted workspace; revisions from
  any workspace in the project refresh the shared history. Pending routing retains known workspace identity.
- Reading history…, No commits on any branch yet. and Could not read the history. cover loading, empty
  and failed reads. File and Git events arrive through the shared local/remote host stream.
- Rows have height 40 and lane width 14. A lane waits for a sha; a commit takes the leftmost waiting lane,
  its first parent inherits it and other parents open lanes. The layout is uncapped, so every line keeps
  leading to its commit; the drawing has eight lanes, and lanes past the eighth are drawn on it as one
  unbroken line with their commits' dots on it. Layout carries pending lanes across pages. Orphan roots terminate their own lanes.
- Only the viewport plus twelve rows of overscan on each side has controls. Counting all rows sizes the
  scroll extent. Near the end, another page appends to history. Every visible row shares one gutter width,
  measured from the rows in the window; a 120 ms transition follows changes in that width.
  Rows use compiled XAML for their frame, metadata templates, context menu and styles. Their bundled
  commit and code-file glyphs match Remix Icon 4.9.0. Changed refs update labels without replacing the
  row, lane art, focus or open menu. Paging is reconsidered
  after refreshed history is laid out, even if no new scroll event fires; a refresh prevents a simultaneous page append.
- Lane strokes and dots use the kit's subtle color; commits with worktree marks have a larger accent dot.
  Half-lines depend on actual parents/children; merges use identical quarter-circle turns.
- A row shows subject, short hash, author, ref chips and worktree names. Text yields to the available rail width.
  Rows have 8 px horizontal padding so the first graph lane is inset from the pane edge.
  Clicking a row calls `SetDiffScope` with the commit's full sha. Changes validates that commit with the
  host's independent `GetCommitAsync`, even without a comparison target or beyond its 200-entry menu.
- Right-click offers Copy commit hash and Copy patch to clipboard. Clipboard and patch failures notify
  the user with the fork's messages.

## Checks and remaining gates

`tests/SharpRail.Checks/BranchGraphChecks.cs` runs through `--branch-graph`, `--plugins` and the full runner.
Parser and lane checks translate the fork's fixtures: control stripping, fields, page cropping, worktree pairing,
straight/branched/merged/orphan histories, lane caps and carried pages.

Set `SHARPRAIL_TEST_GIT_SOURCE` to the fork clone containing
`refs/remotes/origin/claude-code-integration-plugin-api` for real Git checks. These fetch existing history
and create refs/worktrees in an isolated fixture; they create no commits and never alter signing configuration.
Local and gRPC host checks cover pagination, refs, worktrees, patches and errors. UI checks cover windowing,
shared gutters, copying, Changes scope and paging through both host modes. Fresh committed fixtures
(isolated, unsigned) translate the fork's merge/orphan rendering (branch art only on the merge row, the
orphan's `docs-site` chip) and gutter scenarios (one lane at the tip, widening on scroll into dated
branches), and cover two windows on different projects (no re-read, Copy patch from the background
window), project-switch reset, empty history and a failed read that recovers; they need no Git source.
The UI checks also cover Git-only
ref refresh with retained focus/menu, disable into a dormant tool tab, re-enable/remount and Gitless withholding.

Still required for the full port: in-flight lifecycle races,
native appearance, published output and full-suite verification.
