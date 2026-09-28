# Upstream E2E translation

Authoritative source: [JetBrains/thinkrail:main — e2e](https://github.com/JetBrains/thinkrail/tree/main/e2e).

Tests drive real Avalonia pointer/keyboard input and host calls in a headless window.
Each case gets an isolated repository fixture and profile. Native macOS checks remain
necessary for window moving, zooming, platform dialogs and final visual verification.
Multiwindow gesture cases open the same project with independent window profiles
and explicitly focus the shortcut recipient before sending keyboard input.
Browser URL/page-count assertions become internal workspace navigation assertions.
ARIA roles and relationships become Avalonia automation-peer assertions. Delayed
WebSocket replies become held host-call responses; local mode uses direct C# calls.
The initial upstream AI chat tab is omitted from document counts.
Terminal docking cases create a nonfunctional terminal tab through the pane menu
before following upstream's gestures. This verifies pane chrome and body identity;
it does not prove automatic initial terminal provisioning or terminal execution.
The upstream one-pixel PNG has an invalid IDAT checksum and truncated compressed
data. Its translated fixture uses a valid one-pixel PNG because Skia rejects the
original; the image-loading assertion remains intact.

Run with a local clone of `JetBrains/thinkrail:main`:

```sh
git clone --branch main https://github.com/JetBrains/thinkrail.git .bench/thinkrail
SHARPRAIL_TEST_GIT_SOURCE="$PWD/.bench/thinkrail" .tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release
```

## Case inventory

Ported cases retain upstream titles. `Ported` does not mean the latest run passed.
Existing host/layout checks provide additional coverage but are not counted as translations.

| Suite | Upstream case | Status |
| --- | --- | --- |
| `preview-tabs.spec.ts` | a single click previews into one reusable slot, a double click keeps the tab | Ported |
| `preview-tabs.spec.ts` | a double click claims the slot on its way to keeping the tab, at any latency | Ported |
| `preview-tabs.spec.ts` | a double click on an unopened file sends exactly one fs.readFile | Ported |
| `preview-tabs.spec.ts` | a browse the user has navigated away from is dropped, not activated on arrival | Ported |
| `preview-tabs.spec.ts` | of two browse clicks in flight at once, the later one wins | Ported |
| `preview-tabs.spec.ts` | a keep that lands first does not invalidate a browse requested after it | Ported |
| `preview-tabs.spec.ts` | a newer tab click cancels an older preview-tab settle timer | Ported |
| `preview-tabs.spec.ts` | the Specs panel shares the one slot, and closing the preview tab releases it | Ported |
| `markdown-links.spec.ts` | a parent-relative file link cannot escape into browser navigation | Ported |
| `markdown-links.spec.ts` | relative links, images, and heading anchors work in the rendered markdown view | Ported |
| `markdown-alerts.spec.ts` | renders GitHub-style alert callouts in the rendered markdown view | Ported |
| `markdown-mermaid.spec.ts` | renders mermaid fences as diagrams in the rendered markdown view | Pending |
| `layout.spec.ts` | workbench strips and feature toolbars keep one-row geometry with ARIA tabs | Pending |
| `layout.spec.ts` | overflow uses directional fades without changing tab-strip geometry | Ported |
| `layout.spec.ts` | auxiliary panel scrollbars stay quiet at rest and expose only clipped edges | Pending |
| `layout.spec.ts` | ARIA tabs use roving keyboard focus, recover after close, and expose keyboard separators | Ported |
| `layout.spec.ts` | outer side widths publish on pointer-up and restore after reload | Ported |
| `layout.spec.ts` | one local frame survives workspace switches while resource tabs stay workspace-specific | Pending |
| `layout.spec.ts` | a duplicated tab remints copied surface storage and preserves both layouts on reload | Pending |
| `layout.spec.ts` | dragging outer separators hides both sides and preserves their restore state | Pending |
| `layout.spec.ts` | the side group menu shows tools for its own side and opens terminals in that group | Ported |
| `layout.spec.ts` | a terminal can move to its own side group; resize, fold, and visibility gate its one body | Ported |
| `layout.spec.ts` | side groups expose broad per-panel above and below split targets | Ported |
| `layout.spec.ts` | Mod+B and Mod+J hide and restore local sides without affecting bottom | Ported |
| `layout.spec.ts` | keyboard and menu commands reorder, search, recursively split, and explicitly remove empty groups | Pending |
| `layout.spec.ts` | each center group owns an independent preview slot | Ported |
| `layout.spec.ts` | deferred opens stay with their request-time group and reroute only when it disappears | Ported |
| `layout.spec.ts` | pointer drag exposes deterministic split targets and moves one tab | Ported |
| `layout.spec.ts` | applying the Review preset preserves resources and installs its vertical center topology | Ported |
| `layout.spec.ts` | the local default preset drives an explicit frame reset | Ported |
| `layout.spec.ts` | custom presets synchronize while defaults and group limits remain window-local | Pending |
| `layout.spec.ts` | Layout settings controls keep their container-preset max-widths | Ported |
| `layout.spec.ts` | an accepted side-group overage is grandfathered without allowing further growth | Ported |
| `layout.spec.ts` | a narrow viewport compresses locally without rewriting recursive topology | Ported |
| `layout.spec.ts` | frontend windows keep chat and file placement independent | Pending |
| `layout.spec.ts` | layout survives a transport reconnect and remains writable | Pending |
| `layout.spec.ts` | another window cannot cancel or rearrange an active tab drag | Ported |
| `layout.spec.ts` | another window cannot cancel or adopt an active side resize | Ported |
| `layout.spec.ts` | local layout transitions with no gesture in progress never announce a canceled drag | Ported |
| `layout.spec.ts` | a local transition during a side resize cancels the gesture and says so | Ported |
| `layout.spec.ts` | a tab drag reveals every valid destination subtly, then emphasizes the one under the pointer | Ported |
| `layout.spec.ts` | the hidden bottom drop zone wins overlapping terminal targets and reveals its frame group | Ported |
| `projects.spec.ts` | opens a git repo as a project via the directory picker | Pending |
| `projects.spec.ts` | opens a project from an explicit host path | Pending |
| `projects.spec.ts` | picker failure falls back to host-path entry on every host platform | Pending |
| `projects.spec.ts` | manual path from the rail supersedes a picker started from Welcome | Pending |
| `projects.spec.ts` | opening a non-git folder offers to initialise a repo, then opens it end-to-end | Pending |
| `projects.spec.ts` | rail expansion is per-browser view state that survives a reload | Ported |
| `projects.spec.ts` | activating a workspace in one project keeps the other project's rail expansion | Pending |
| `projects.spec.ts` | project context actions stay compact and close/reopen is lossless across clients | Pending |
| `workspace-tabs.spec.ts` | editor tabs are scoped to the active workspace | Ported |
| `workspace-tabs.spec.ts` | the selected side tool follows workspace switches | Ported |
| `workspace-tabs.spec.ts` | switching workspaces re-targets the mounted workbench instead of remounting it | Ported |
| `workspace-tabs.spec.ts` | a same-id terminal body remounts instead of carrying across workspaces | Excluded: functional editor/terminal or AI |
| `theme.spec.ts` | appearance switches a discovered theme and persists it across reload | Ported |
| `theme.spec.ts` | system mode follows each client and retains its explicit pair | Pending |
| `theme.spec.ts` | Monaco opens files and re-themes under every discovered manifest | Excluded: functional editor/terminal or AI |
| `theme.spec.ts` | selected workspace tabs keep their surface and edge marker in high contrast | Pending |
| `line-width-settings.spec.ts` | line-width controls validate drafts, converge on broadcasts, and persist | Pending |
| `line-width-settings.spec.ts` | the file width wraps source and updates an already-mounted editor | Excluded: functional editor/terminal or AI |
| `line-width-settings.spec.ts` | the default file width wraps both sides of a long-line diff | Pending |
| `line-width-settings.spec.ts` | chat uses the selected measure and optionally exceeds a narrow pane | Excluded: functional editor/terminal or AI |
| `settings.spec.ts` | settings shows the Local GitHub status block and degrades gh gracefully | Pending |
| `changes.spec.ts` | Changes tab shows the active worktree's diff and swaps per workspace | Pending |
| `changes.spec.ts` | Rendered markdown diff of a large repetitive file never blocks the main thread | Pending |
| `changes.spec.ts` | Rendered markdown diff shows an error placeholder when the merge worker fails | Pending |
| `changes.spec.ts` | Rendered markdown diff follows live edits on disk (stale merge cancelled, fresh one lands) | Pending |
| `changes.spec.ts` | Changes has a List&#124;Tree toggle; Tree groups files into folders with +/- counts | Pending |
| `changes.spec.ts` | Changes scope selector filters by commit / uncommitted; each scope is its own diff tab | Pending |
| `changes.spec.ts` | Uncommitted scope converges when HEAD moves out-of-band (a commit in a terminal) | Pending |
| `changes.spec.ts` | The scope menu's target-branch picker re-points what the changes are measured against | Pending |
| `changes.spec.ts` | A target that advanced past the fork point adds no phantom changes (merge-base semantics) | Pending |
| `changes.spec.ts` | A change row's action menu opens from the ⌄ button and from right-click; Copy path writes the relative path | Pending |
| `changes.spec.ts` | The diff viewer collapses unchanged context and has a per-tab hide-whitespace + copy header | Pending |
| `changes.spec.ts` | Change rows stay one aligned, fully-highlighted row — menu slot included, long names truncated | Pending |
| `changes.spec.ts` | The diff header keeps its controls on a narrow pane, however long the file's path | Pending |
| `changes.spec.ts` | A commit scope keeps the header readable: short sha on the pill, subject in its tooltip | Pending |
| `changes.spec.ts` | The scope menu is per workspace: its commit rows never carry over to another worktree | Pending |
| `changes.spec.ts` | Re-pointing the target branch re-reads an open branch-scope diff tab — active or backgrounded | Pending |
| `changes.spec.ts` | A commit scope whose commit is rewritten away falls back to All changes with a toast | Pending |
| `changes.spec.ts` | A failed read says so — it never renders as an empty (clean) change set | Pending |
| `changes.spec.ts` | Closing a diff tab disposes Monaco cleanly — no 'TextModel got disposed' assertion | Excluded: functional editor/terminal or AI |
| `topbar-chrome.spec.ts` | ordinary browsers have a fixed themed header with zero native insets | Pending |
| `topbar-chrome.spec.ts` | live safe areas on either edge preserve header and workbench geometry | Pending |
| `topbar-chrome.spec.ts` | the action cluster keeps Update, quota Retry and Settings out of the drag region | Pending |

Upstream cases combining supported docking with terminal/chat content need adapted
fixtures while preserving the layout assertions. Multi-client synchronization is not
implemented by the current single-frontend remote host; those cases remain pending
rather than being weakened to local persistence. No benchmarks or Git commits are run.
