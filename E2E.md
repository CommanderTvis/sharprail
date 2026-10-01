# Upstream E2E translation

Authoritative source: [JetBrains/thinkrail:main — e2e](https://github.com/JetBrains/thinkrail/tree/main/e2e).

Tests drive real Avalonia pointer/keyboard input and host calls in a headless window.
Each case gets an isolated repository fixture and profile. Native macOS checks remain
necessary for window moving, zooming, platform dialogs and final visual verification.
Multiwindow gesture cases open the same project with independent window profiles
and explicitly focus the shortcut recipient before sending keyboard input; shared-state
cases open the second window from the same app so both windows share one host.
Browser URL/page-count assertions become internal workspace navigation assertions.
ARIA roles and relationships become Avalonia automation-peer assertions. Delayed
WebSocket replies become held host-call responses; local mode uses direct C# calls.
AI chat is a non-goal (see `SPEC.md`): the initial upstream chat tab is omitted from
document counts, chat-only cases are excluded, and mixed cases keep only their
non-chat assertions.
Every new workspace opens upstream's initial terminal, so terminal docking cases use it.
Headless terminal tabs run real host PTY sessions (`/bin/sh`) and show their output as
plain text; commands print split markers so an echoed command line never satisfies an
output assertion. Delayed or failed `terminal.attach` replies become held or failing
host starts. Ghostty itself cannot run headless.
Project picker cases inject a fake folder picker (the platform dialog is native); the
non-git case initialises through the host `init` action, which commits the folder's existing
files as upstream does. Rail expansion is checked after activating the first project's default
workspace, because only the active project lists its workspaces. Same-id terminal
bodies are provoked by opening one terminal tab id in two workspaces through the
layout API, because generated terminal ids never repeat. The
Local GitHub case empties `PATH` so the real `gh` probe degrades.
Workspace suites commit the sample project and click its project row to reach Project
Home, where upstream's `openFixtureProject` lands. A reload closes the window and opens
a new one on the same profile; the profile's last location stands in for the URL route.
Native shell execution, Metal presentation, Mod+Shift+J from a focused Ghostty view and
remote Ghostty tabs relayed to a gRPC host PTY have separate checks in
`scripts/check-terminal.sh` and `SharpRail.Checks --native-terminal`.
The upstream one-pixel PNG has an invalid IDAT checksum and truncated compressed
data. Its translated fixture uses a valid one-pixel PNG because Skia rejects the
original; the image-loading assertion remains intact.

The rendered Markdown diff cases replace upstream's web worker and long-task observer
with a merge seam on the window: it records the thread each merge runs on, can hold or
fail a merge, and the test times every dispatcher turn while the merge lands. Git-backed
rendered-diff and live-refresh fixtures commit the sample README and SPEC.md on the base
branch before creating the worktree, matching upstream's sample project.

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
| `preview-tabs.spec.ts` | with previewing off, every click keeps a tab of its own | Ported (fork) |
| `markdown-links.spec.ts` | a parent-relative file link cannot escape into browser navigation | Ported |
| `markdown-links.spec.ts` | relative links, images, and heading anchors work in the rendered markdown view | Ported |
| `markdown-alerts.spec.ts` | renders GitHub-style alert callouts in the rendered markdown view | Ported |
| `markdown-mermaid.spec.ts` | renders mermaid fences as diagrams in the rendered markdown view | Ported (with the fork's capped inline diagram: the box stays under 480 px, zoom enlarges the drawing, a drag pans it) |
| `layout.spec.ts` | workbench strips and feature toolbars keep one-row geometry with ARIA tabs | Ported |
| `layout.spec.ts` | overflow uses directional fades without changing tab-strip geometry | Ported |
| `layout.spec.ts` | auxiliary panel scrollbars stay quiet at rest and expose only clipped edges | Pending: relies on browser WebKit scrollbar pseudo-elements, hover-intent attributes and forced-colors media; Avalonia has no quiet-scroll cue surface to assert |
| `layout.spec.ts` | ARIA tabs use roving keyboard focus, recover after close, and expose keyboard separators | Ported |
| `layout.spec.ts` | outer side widths publish on pointer-up and restore after reload | Ported |
| `layout.spec.ts` | one local frame survives workspace switches while resource tabs stay workspace-specific | Ported |
| `layout.spec.ts` | a duplicated tab remints copied surface storage and preserves both layouts on reload | Pending: browser sessionStorage surface ids and duplicated tabs have no desktop-window analogue |
| `layout.spec.ts` | dragging outer separators hides both sides and preserves their restore state | Ported |
| `layout.spec.ts` | the side group menu shows tools for its own side and opens terminals in that group | Ported |
| `layout.spec.ts` | a terminal can move to its own side group; resize, fold, and visibility gate its one body | Ported |
| `layout.spec.ts` | a project with no specs opens its rail on Files, not on the empty Specs panel | Ported (fork; the fixture's specs are removed rather than opening a plain folder) |
| `layout.spec.ts` | side groups expose broad per-panel above and below split targets | Ported |
| `layout.spec.ts` | Mod+B and Mod+J hide and restore local sides without affecting bottom | Ported |
| `layout.spec.ts` | keyboard and menu commands reorder, search, recursively split, and explicitly remove empty groups | Ported |
| `layout.spec.ts` | each center group owns an independent preview slot | Ported |
| `layout.spec.ts` | deferred opens stay with their request-time group and reroute only when it disappears | Ported |
| `layout.spec.ts` | pointer drag exposes deterministic split targets and moves one tab | Ported |
| `layout.spec.ts` | applying the Review preset preserves resources and installs its vertical center topology | Ported |
| `layout.spec.ts` | the local default preset drives an explicit frame reset | Ported |
| `layout.spec.ts` | custom presets synchronize while defaults and group limits remain window-local | Ported (the peer is a second window of the app; save, rename, set default, limits and delete) |
| `layout.spec.ts` | Layout settings controls keep their container-preset max-widths | Ported |
| `layout.spec.ts` | an accepted side-group overage is grandfathered without allowing further growth | Ported |
| `layout.spec.ts` | a narrow viewport compresses locally without rewriting recursive topology | Ported |
| `layout.spec.ts` | frontend windows keep chat and file placement independent | Ported for terminal and file placement across two windows of the app; the chat-tab and closed-chat history assertions are excluded (AI chat is a non-goal) |
| `layout.spec.ts` | layout survives a transport reconnect and remains writable | Ported |
| `layout.spec.ts` | another window cannot cancel or rearrange an active tab drag | Ported |
| `layout.spec.ts` | another window cannot cancel or adopt an active side resize | Ported |
| `layout.spec.ts` | local layout transitions with no gesture in progress never announce a canceled drag | Ported |
| `layout.spec.ts` | a local transition during a side resize cancels the gesture and says so | Ported |
| `layout.spec.ts` | a tab drag reveals every valid destination subtly, then emphasizes the one under the pointer | Diverges: tab strips are not framed; their tabs are the only strip targets |
| `layout.spec.ts` | the hidden bottom drop zone wins overlapping terminal targets and reveals its frame group | Ported |
| `projects.spec.ts` | opens a git repo as a project via the directory picker | Ported |
| `projects.spec.ts` | opens a project from an explicit host path | Ported |
| `projects.spec.ts` | picker failure falls back to host-path entry on every host platform | Ported |
| `projects.spec.ts` | manual path from the rail supersedes a picker started from Welcome | Ported |
| `projects.spec.ts` | opening a non-git folder offers to initialise a repo, then opens it end-to-end | Superseded by the fork's `gitless.spec.ts`: a plain folder opens directly, with no git required (the check asserts no dialog, no `.git`, and Create workspace disabled) |
| `projects.spec.ts` (fork) | project context menu copies its absolute path without changing the active workspace | Ported |
| `projects.spec.ts` | rail expansion is per-browser view state that survives a reload | Ported |
| `projects.spec.ts` | activating a workspace in one project keeps the other project's rail expansion | Ported |
| `projects.spec.ts` | project context actions stay compact and close/reopen is lossless across clients | Ported (the observer is a second window of the app, opened after the cancel/Escape focus steps; touch long-press has no desktop input, and the menu has no Open existing worktree entry) |
| `new-workspace-shortcut.spec.ts` | Mod+N opens the Create workspace dialog for the selected project from the Welcome screen, and Escape closes it | Ported |
| `new-workspace-shortcut.spec.ts` | The Mod+Alt+N alias opens the same dialog and Mod+Shift+N does not | Ported |
| `new-workspace-shortcut.spec.ts` | Mod+N works inside an active workspace | Ported |
| `workspace-actions.spec.ts` | Open in launches the detected editor detached at the worktree path | Ported (a stub `code` on `PATH` records its argument) |
| `workspace-actions.spec.ts` | Copy path copies the worktree's absolute path to the clipboard | Ported |
| `workspace-actions.spec.ts` | a managed workspace can rename its display label inline without changing Git | Ported |
| `workspace-actions.spec.ts` | an open inline rename survives reconnect | Ported (a real gRPC host behind a relay that drops and restores the client's connection) |
| `workspace-actions.spec.ts` | the Default workspace's kebab menu offers only non-mutating actions | Ported |
| `workspace-actions.spec.ts` | right-click opens the workspace's kebab menu without activating it | Ported |
| `workspace-actions.spec.ts` | the kebab is hover-only ONLY on devices that actually have hover — never invisible by default | Ported (Avalonia has no hover media query; asserts opacity 0 at rest and 1 on row hover) |
| `welcome.spec.ts` | opens a clean ThinkRail with no projects imported | Ported (the title is SharpRail; the side tool frame stays, empty) |
| `welcome.spec.ts` | the Welcome provider warning only shows when no provider is connected, and opens Settings | Excluded: AI providers |
| `welcome.spec.ts` | Settings → Providers lists in-app auth options | Excluded: AI providers |
| `welcome.spec.ts` | a real provider's API key round-trips through the login dialog (add in Settings, sign out) | Excluded: AI providers |
| `welcome.spec.ts` | clicking Sign in (Settings) opens the in-app login dialog, and Cancel dismisses it | Excluded: AI providers |
| `welcome.spec.ts` | Settings → Providers offers JetBrains AI with host-authoritative Central guidance | Excluded: AI providers |
| `welcome.spec.ts` | a project with specs offers Start building over Set up, beside the project-folder fork | Ported (non-AI assertions: Project home context and the single Work in project folder fork; Start building opens an AI chat, so Create workspace is the call to action) |
| `welcome.spec.ts` | a project without specs suggests setting it up | Excluded: Set up project pre-fills the setting-up-a-project AI skill prompt; the dialog's project-folder path is covered by the new-workspace folder-mode case |
| `welcome.spec.ts` | opening a non-git folder from the Welcome screen offers to initialise a repo | Superseded by the fork's `gitless.spec.ts`: the folder lands on its Project Home without git init (the Set up project call to action is AI and omitted) |
| `welcome.spec.ts` | clicking a project returns to its Welcome, deselecting the active workspace | Ported |
| `default-workspace.spec.ts` | the Welcome fork's “Work in project folder” enters the Default workspace — the project folder itself | Ported (the terminal `pwd` step is omitted: headless checks do not execute shells) |
| `default-workspace.spec.ts` | a terminal branch switch converges every Default branch label live | Ported (the fixture switches branch with Git directly instead of through an embedded terminal) |
| `default-workspace.spec.ts` | the Default workspace is non-removable and unique; project home stays reachable | Ported |
| `workspaces.spec.ts` | opens and safely forgets an existing user-owned worktree | Pending: SharpRail lists every Git worktree from `git worktree list`; there is no Open existing worktree dialog or adopt/forget registry for user-owned checkouts |
| `workspaces.spec.ts` | an attached worktree cannot also be opened as a project | Pending: opening a linked worktree resolves to its owning project rather than reporting a conflict; needs the external-worktree registry above |
| `workspaces.spec.ts` | creates, removes, and re-creates worktree workspaces (no branch collision) | Ported |
| `new-workspace.spec.ts` | the dialog lists local branches (no stray origin) and creates a worktree | Ported (the model/effort selectors and initial chat tab are AI and omitted) |
| `new-workspace.spec.ts` | an edited name names the worktree, and the placeholder leaves naming to the host | Ported (fork; the edited name becomes the workspace's host label, and the AI naming hint is omitted) |
| `branch-list.spec.ts` | the branch chip lists branches with their worktrees, and guards deletion | Ported (fork; the checked-out branch is refused too, and the host's refusal is checked directly) |
| `branch-list.spec.ts` | a branch held by a worktree ThinkRail did not make is still the user's to delete | Excluded: SharpRail lists every worktree of a project as a workspace, so a worktree-held branch is always refused |
| `branch-list.spec.ts` | Fetch brings the remotes up to date from the branch list | Ported (fork) |
| `search.spec.ts` | Mod+Shift+F searches the worktree and a hit opens its file at that line | Ported (fork; also checks that Git-ignored files are skipped; the line is scrolled to on macOS, where Scintilla opens text files) |
| `search.spec.ts` | a hit in a markdown file flashes the block it landed in | Pending: the rendered Markdown view has no source-line landing |
| `search.spec.ts` | a query with no matches says so, and Escape closes the popup | Ported (fork) |
| `fold-perf.spec.ts` | folding a side group does not remount the centre | Ported (fork; already held, since folding re-projects cached document and terminal controls) |
| `new-workspace.spec.ts` | folder-mode Start with an empty prompt lands in a fresh chat in the Default workspace | Ported (Start lands in the Default workspace; no chat is created) |
| `new-workspace.spec.ts` | a project's committed skills are gated behind trust, then autocomplete | Excluded: AI skills |
| `new-workspace.spec.ts` | the start prompt shares template completion and slot behavior without live-only commands | Excluded: AI start prompt |
| `new-workspace.spec.ts` | Enter in the prompt creates; Shift+Enter inserts a newline | Ported (the start prompt is AI, so Enter in the dialog creates; there is no multiline field for Shift+Enter) |
| `new-workspace.spec.ts` | a base whose fetch fails reports git's error, not a request timeout | Ported |
| `new-workspace.spec.ts` | the branch picker groups by host-supplied remotes and creates from the selected ref | Ported |
| `new-workspace.spec.ts` | opening New Workspace prefetches a stale default before create | Ported |
| `new-workspace.spec.ts` | opening New Workspace prefetches a missing default tracking ref | Ported |
| `branch-list.spec.ts` | a remote group can be collapsed and expanded, and stays that way in every branch picker | Ported (the New Workspace picker; remembered per remote in the profile) |
| `branch-list.spec.ts` | left-clicking a branch checked out by a ThinkRail workspace switches to it | Excluded: SharpRail has no topbar branch list |
| `branch-list.spec.ts` | the branch list groups branches from every configured remote, and a remote-only branch opens New Workspace prefilled | Excluded: SharpRail has no topbar branch list |
| `branch-list.spec.ts` | the topbar branch list marks its Local branches, mirroring the Changes picker | Excluded: SharpRail has no topbar branch list |
| `branch-list.spec.ts` | a dirty external worktree requires a second force-delete confirmation | Excluded: SharpRail has no branch list with branch deletion |
| `new-workspace.spec.ts` | a pasted image in the workspace dialog rides along into the first chat turn | Excluded: AI chat |
| `workspace-lifecycle.spec.ts` | workspace removal propagates — no zombie row in a second tab | Ported (second window of the app; the removed workspace's notice stands in for the toast) |
| `workspace-lifecycle.spec.ts` | workspace rename propagates live and rehydrates a tab that missed a later snapshot | Ported (two gRPC clients; the peer's connection is dropped and restored by a relay) |
| `workspace-lifecycle.spec.ts` | removing the active workspace restores the previously selected workspace | Ported |
| `workspace-lifecycle.spec.ts` | workspace creation propagates to a second tab's rail | Ported (second window of the app) |
| `reload-navigation.spec.ts` | reloading from the older of two chats returns to that exact chat without rail clicks | Excluded: AI chat |
| `reload-navigation.spec.ts` | a directly opened exact-chat fragment restores that chat; two tabs keep independent routes | Excluded: AI chat |
| `reload-navigation.spec.ts` | missing chat, workspace, and project fall back to the nearest valid location | Ported (the chat step is omitted; the profile's last location stands in for the URL) |
| `reload-navigation.spec.ts` | a transient workspace read failure preserves the URL and restores after reconnect | Ported (the relay refuses the restoring client's connection, then allows it; the remembered workspace stands in for the URL and the chat tab is omitted) |
| `reload-navigation.spec.ts` | a failed exact-chat transcript waits for reconnect instead of duplicating its read | Excluded: AI chat |
| `reload-navigation.spec.ts` | user navigation while the restore read is delayed wins over the late response | Ported (the held read is the startup project open) |
| `reload-navigation.spec.ts` | reload from a file tab restores its shared placement under the workspace route | Ported |
| `reload-navigation.spec.ts` | workspace rows still list after a reload restore (the light list is complete) | Ported |
| `files.spec.ts` | shows files and compacts single-directory runs in the Files tree | Ported |
| `files.spec.ts` (fork) | a file row has our own context menu, not the webview's | Ported |
| `files.spec.ts` (fork) | file and compacted folder menus copy their host absolute paths | Ported for a file row |
| `files.spec.ts` (fork) | deleting a previewed file leaves the workbench interactive | Ported (macOS only: it moves a real file to the Trash) |
| `files.spec.ts` (fork) | a folder row creates a file inside it, whose icon follows the name as it is typed | Ported without the icon step: the tree has no per-file-type icons |
| `files.spec.ts` (fork) | a new file warns as soon as its name already exists | Ported (within the folder-row case) |
| `files.spec.ts` (fork) | a file row creates a folder beside it, and renames itself | Ported (the stem selection is asserted; the rename text is set rather than typed) |
| `files.spec.ts` (fork) | an open tab says when its file is deleted on disk, and recovers when it returns | Ported (the banner is asserted on macOS, where text files open in Scintilla) |
| `files.spec.ts` (fork) | an entry git ignores is dimmed, whichever rule ignores it | Not ported: the tree does not mark ignored entries |
| `files.spec.ts` (fork) | a file row drags into the composer as a mention and into a terminal as a path | Not ported: AI composer excluded; tree rows are not drag sources |
| `clone-project.spec.ts` (fork) | clone repository | Not ported yet |
| `gitless.spec.ts` (fork) | Changes and Review withheld for a plain folder or an unborn repository | Not ported yet |
| `editor.spec.ts` | opens a file in a center Monaco tab, focuses on re-open, and closes | Ported (Markdown source is SharpRail's read-only source view; the ready placeholder omits chats) |
| `editor.spec.ts` | hides YAML frontmatter in the rendered view but shows it in source | Ported (the prose is checked apart from the fork's properties block, whose values are text here rather than form fields) |
| `editor.spec.ts` | opens a non-markdown file straight to Monaco with no rendered-view toggle | Ported (macOS Scintilla) |
| `workspace-tabs.spec.ts` | editor tabs are scoped to the active workspace | Ported |
| `workspace-tabs.spec.ts` | the selected side tool follows workspace switches | Ported |
| `workspace-tabs.spec.ts` | switching workspaces re-targets the mounted workbench instead of remounting it | Ported |
| `workspace-tabs.spec.ts` | a same-id terminal body remounts instead of carrying across workspaces | Ported |
| `terminals.spec.ts` | a workspace opens a terminal automatically, rooted in the worktree, with working I/O | Ported |
| `terminals.spec.ts` | a shell start failure explains recovery and retries the same tab | Ported (failure text, preserved focus, disabled then focused Retry, same-tab recovery; SharpRail has no terminal Settings page to link; take-back is covered by the second-client row) |
| `terminals.spec.ts` | xterm uses the shared quiet rail and directional curtains | Excluded: xterm.js scrollbar and CSS curtain styling; Ghostty draws its own native scrollback |
| `terminals.spec.ts` | terminals are workspace-scoped and survive workspace switches | Ported |
| `terminals.spec.ts` | multiple terminals per workspace keep independent buffers and can be closed | Ported |
| `terminals.spec.ts` | the terminal's shell counts characters, not bytes | Ported |
| `terminals.spec.ts` | a shell survives a trip to Project Home and back | Ported (the test leaves through the project's default workspace, which also unmounts the worktree's terminal; the Project Home route is not exercised yet) |
| `terminals.spec.ts` | historical terminal queries do not become input on remount | Excluded: SharpRail keeps the live terminal surface across remounts instead of replaying output into a new emulator, so no replayed query can answer itself |
| `terminals.spec.ts` | rapid re-entry never spawns a second shell | Ported |
| `terminals.spec.ts` | a shell survives a page reload | Ported (reload is a new window of the same app over the same profile and terminal host; earlier output is replayed once) |
| `terminals.spec.ts` | a terminal's output never reaches another client | Ported (two windows, each with its own connection to a real gRPC host) |
| `terminals.spec.ts` | a tab says so when its shell exits | Ported |
| `terminals.spec.ts` | Ctrl+C still interrupts while an input method is active | Excluded: Chromium keyCode 229 composition events; native Ctrl+C through the AppKit responder chain is covered by `--native-terminal` |
| `terminals.spec.ts` | a shell that dies while detached is not re-attached as if alive | Ported (the reopened tab shows the final output and `[process exited with code 137]` without starting a shell; unlike upstream, the exited tab stays and a new terminal gives a live shell) |
| `terminals.spec.ts` | a shell survives losing the connection and reconnecting | Ported (a TCP proxy severs the gRPC connection; the session resumes from its last output position without duplicates) |
| `terminals.spec.ts` | a terminal attach response lost with its socket is replayed exactly once | Ported (a client interceptor drops the first attach reply; the retry attaches the same session and the host starts one shell) |
| `terminals.spec.ts` | final shell output is delivered before exit after reconnect | Ported (the proxy holds the reconnect while the shell exits; final output arrives once, then exit code 7) |
| `terminals.spec.ts` | a second client takes a terminal over and the first is told | Ported (second window, real gRPC host; the first shows the detached notice over its hidden surface and takes the terminal back) |
| `terminals.spec.ts` | closing a tab with a running process asks first | Ported |
| `terminals.spec.ts` | a rejected forced close stays correlated and permits a clean retry | Excluded: closing disposes the tab's own session; SharpRail has no host close request that can be refused |
| `terminals.spec.ts` | closing an idle tab does not ask | Ported |
| `terminals.spec.ts` | a terminal opened in one browser never creates placement in another | Ported (second window, real gRPC host) |
| `terminals.spec.ts` | a shell that dies during a reclaim is not presented as alive | Ported (the take-back attach reply is delayed while the shell is killed; the tab ends exited) |
| `terminals.spec.ts` | a terminal that was hidden while it printed keeps its width | Excluded: xterm DOM rendering; Ghostty renders natively and keeps its grid while hidden |
| `terminals.spec.ts` | terminal forwards pointer capture and Ctrl+T to a raw PTY | Ported as a host check (a live reattach replays the alternate screen and mouse modes); pointer forwarding is Ghostty's |
| `terminals.spec.ts` | terminal sends alternate-scroll wheel gestures to the PTY | Ported as a host check (alternate scroll, mode 1007, is replayed with the mouse modes) |
| `bottom-panel.spec.ts` | full-height panel-header actions stay square | Ported (no chat or side-group menu buttons; SharpRail shows a group's fold button once its region has several groups) |
| `bottom-panel.spec.ts` | a new workspace starts with one accessible terminal group in a 30% bottom panel | Ported |
| `bottom-panel.spec.ts` | a hidden local frame keeps the host terminal reserved without attaching until shown | Ported (the peer-client attach needs multi-client sync) |
| `bottom-panel.spec.ts` | a completed initial-terminal handshake never recreates a terminal after explicit close | Ported (the tab is closed through the UI rather than a host request) |
| `bottom-panel.spec.ts` | Mod+Shift+J works from xterm, preserves its PTY through hide and reload, and is modal-aware | Ported (survival across hide and across a reload into a new window of the same app; native Ghostty focus is covered by `--native-terminal`) |
| `bottom-panel.spec.ts` | bottom height, all alignments, and keyboard resizing persist across reload | Ported |
| `bottom-panel.spec.ts` | bottom alignments give excluded lower corners to the actual side panels | Ported |
| `bottom-panel.spec.ts` | bottom alignments follow locally compressed side geometry at narrow widths | Ported |
| `bottom-panel.spec.ts` | narrow side resizing persists only the side whose separator moved | Ported (each scenario starts from the default side widths at a 560-pixel viewport, where the compressed center still leaves room to move) |
| `bottom-panel.spec.ts` | closing a final bottom resource retains its frame groups until explicit removal | Ported |
| `bottom-panel.spec.ts` | bottom alignments follow side geometry while a resize gesture is in progress | Ported |
| `bottom-panel.spec.ts` | bottom groups arrange left-to-right, resize, fold to 27px, restore, and enforce their own limit | Ported (the tool moves through Move to pane → bottom group) |
| `bottom-panel.spec.ts` | a narrow viewport locally compresses bottom groups without rewriting their topology | Ported |
| `bottom-panel.spec.ts` | bottom visibility and alignment stay local to each window and survive its reload | Ported (the peer is a second window with its own profile) |
| `bottom-panel.spec.ts` | an old host layout stays inert while a pristine surface starts Balanced | Excluded: SharpRail never stored layouts on the host, so there is no legacy host layout to ignore |
| `theme.spec.ts` | appearance switches a discovered theme and persists it across reload | Ported (Settings lists every bundled manifest) |
| `theme.spec.ts` | system mode follows each client and retains its explicit pair | Ported (the application theme variant stands in for the emulated media query; also checks the pair after reload). The peer is a second gRPC client with its own settings copy; both share one process and so one device appearance, so the peer's light-device theme is checked by resolving its synced mode and pair |
| `theme.spec.ts` | Monaco opens files and re-themes under every discovered manifest | Ported (macOS Scintilla; every bundled manifest, with the high-contrast selected-text overrides) |
| `theme.spec.ts` | selected workspace tabs keep their surface and edge marker in high contrast | Ported (center, right and terminal strips; brush, 2px geometry and rendered pixels) |
| `line-width-settings.spec.ts` | line-width controls validate drafts, converge on broadcasts, and persist | Ported: draft Escape/validation/Enter/Save, a held host broadcast keeping the toggle at the host's value until released, a second window converging, and persistence across a fresh window, with the Markdown width standing in for the excluded chat measure. SharpRail's "Limit lines to this width" toggle falls back to the pane width rather than exceeding it |
| `line-width-settings.spec.ts` | the file width wraps source and updates an already-mounted editor | Ported (macOS Scintilla) |
| `line-width-settings.spec.ts` | the default file width wraps both sides of a long-line diff | Ported |
| `line-width-settings.spec.ts` | chat uses the selected measure and optionally exceeds a narrow pane | Excluded: AI chat is a non-goal |
| `settings.spec.ts` | settings shows the Local GitHub status block and degrades gh gracefully | Ported |
| `settings.spec.ts` | macOS opens settings with its own Preferences chord, and other platforms do not | Ported (fork) |
| `changes.spec.ts` | Changes tab shows the active worktree's diff and swaps per workspace | Ported |
| `changes.spec.ts` | Rendered markdown diff of a large repetitive file never blocks the main thread | Ported (the merge runs off the dispatcher thread; no dispatcher turn may exceed 250 ms instead of the browser's 1 s long task) |
| `changes.spec.ts` | Rendered markdown diff shows an error placeholder when the merge worker fails | Ported (an injected failing merge replaces the aborted worker script) |
| `changes.spec.ts` | Rendered markdown diff follows live edits on disk (stale merge cancelled, fresh one lands) | Ported (a merge held for an intermediate edit must be cancelled by the next one) |
| `changes.spec.ts` | Changes has a List&#124;Tree toggle; Tree groups files into folders with +/- counts | Ported |
| `changes.spec.ts` | Changes scope selector filters by commit / uncommitted; each scope is its own diff tab | Ported |
| `changes.spec.ts` | Uncommitted scope converges when HEAD moves out-of-band (a commit in a terminal) | Ported |
| `changes.spec.ts` | The scope menu's target-branch picker re-points what the changes are measured against | Ported |
| `changes.spec.ts` | A target that advanced past the fork point adds no phantom changes (merge-base semantics) | Ported |
| `changes.spec.ts` | A change row's action menu opens from the ⌄ button and from right-click; Copy path writes the relative path | Ported |
| `changes.spec.ts` | The diff viewer collapses unchanged context and has a per-tab hide-whitespace + copy header | Ported |
| `changes.spec.ts` | Change rows stay one aligned, fully-highlighted row — menu slot included, long names truncated | Ported |
| `changes.spec.ts` | The diff header keeps its controls on a narrow pane, however long the file's path | Ported |
| `changes.spec.ts` | A commit scope keeps the header readable: short sha on the pill, subject in its tooltip | Ported |
| `changes.spec.ts` | The scope menu is per workspace: its commit rows never carry over to another worktree | Ported |
| `changes.spec.ts` | Re-pointing the target branch re-reads an open branch-scope diff tab — active or backgrounded | Ported |
| `changes.spec.ts` | A commit scope whose commit is rewritten away falls back to All changes with a toast | Ported |
| `changes.spec.ts` | A failed read says so — it never renders as an empty (clean) change set | Ported |
| `changes.spec.ts` | Closing a diff tab disposes Monaco cleanly — no 'TextModel got disposed' assertion | Excluded: Monaco model lifetime; SharpRail diff tabs do not use an editor |
| `changes.spec.ts` (fork) | The diff header keeps its controls on a narrow pane: an unpinned diff opens inline there, a click pins Split | Ported (the pane widths are SharpRail's own, so the wide step uses a wider window) |
| `changes.spec.ts` (fork) | The rendered markdown diff carries the outline and the properties block | Ported (properties are diffed per key rather than through the text merge; the marks are the same ins/del runs) |
| `changes.spec.ts` (fork) | A markdown diff drops to one column on a narrow pane like every other file | Covered by the width rule shared with source diffs; no separate case |
| `frontmatter.spec.ts` (fork) | frontmatter renders as editable properties, and an edit lands in the draft | Partial: the properties render and fold; SharpRail Markdown tabs have no editable source, so there is no draft to edit |
| `frontmatter.spec.ts` (fork) | list chips add and remove, and the block folds away | Partial: chips render and the block folds; add/remove is editing |
| `frontmatter.spec.ts` (fork) | the type menu converts between text, sequence, and mapping | Excluded: editing |
| `frontmatter.spec.ts` (fork) | a type property offers the spec vocabulary as suggestions | Excluded: editing |
| `frontmatter.spec.ts` (fork) | a block the editor cannot speak renders read-only instead of guessing | Ported |
| `spec-documents.spec.ts` (fork) | a spec is titled by its frontmatter and its [[links]] reach the spec they name | Ported |
| `spec-documents.spec.ts` (fork) | ordinary markdown keeps its own first heading and leaves [[text]] alone | Ported |
| `find.spec.ts` (fork) | Mod+F over a preview opens the find bar and highlights the matches | Partial: the current match is selected and counted; other matches are not painted |
| `find.spec.ts` (fork) | Mod+F inside the editor is left to Monaco's own find widget | Excluded: Markdown source is a read-only text view, not an editor with its own find |
| `outlineTree.test.ts` (fork) | the outline reads headings from the source, skips fences and jumps preview and source | Ported as an interaction check (`MarkdownDocumentE2E`) |
| `mcp-tools.spec.ts` (fork) | a companion pane embeds beside its host; Markdown gets a Split view | Partial: the Markdown Split view only; visualization and blueprint companions are AI surfaces |
| `live-refresh.spec.ts` | worktree changes on disk appear live in Specs, Files, Changes, and an open file tab | Ported |
| `live-refresh.spec.ts` | churn canary: a write storm coalesces to a few frames and the host stays responsive | Ported (fsChanged frames become coalesced watcher refreshes; `/health` becomes a timed host listing) |
| `topbar-chrome.spec.ts` | ordinary browsers have a fixed themed header with zero native insets | Ported (fixed 40px themed header, workbench below it, Settings inside at narrow width; native inset elements are unrepresentable) |
| `topbar-chrome.spec.ts` | live safe areas on either edge preserve header and workbench geometry | Pending: safe-area insets cannot be injected; the title bar has a fixed OS-dependent margin |
| `topbar-chrome.spec.ts` | the action cluster keeps Update, quota Retry and Settings out of the drag region | Pending: SharpRail has no Update or quota Retry actions |
| `plugins/fixture/external-plugin.spec.ts` (fork) | the roster lists a discovered external plugin as disabled | Ported (`PluginUiChecks`: the fixture is installed from disk into the host's state directory and run twice, through the app's in-process runtime and through a remote host over gRPC; the row shows external, `v1.0.0` and disabled) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | enabling mounts its settings section and its side tool, and its method answers over the wire | Ported (the switch in Settings › Plugins; the UI half is read from the plugin directory through `IPluginService.ReadFileAsync`; the section's settings update reaches the host half, and a start action's call and the board's state channel round-trip) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | disabling unmounts the side tool and the method reports disabled | Ported (the tool tab keeps its slot with "Fixture board is off"; every contribution leaves; the call reports `Disabled`) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | a manifest with a mismatched API generation is refused, naming both generations | Ported (a copy installed with `apiGeneration` 999 is refused by the host and its row names 999 and 1) |
| `plugins/fixture/external-plugin.spec.ts` (fork) | a plugin directory removed from disk disappears after a rescan | Ported (a second installed copy is deleted from disk; Rescan in Settings › Plugins drops its row) |

Upstream cases combining supported docking with terminal/chat content need adapted
fixtures while preserving the layout assertions. Multi-client cases use a second window
of the same app (`E2eWorkspace.NewWindow`, Mod+Shift+N) as the in-process peer, and
separate clients of a real gRPC host for remote peers; `CutProxy` drops and restores one
client's connection where upstream routes a WebSocket. Separately launched local
processes are independent by design, so no case runs two app processes. Terminal
session sharing across clients remains pending with host-owned sessions.
No benchmarks or Git commits are run.
