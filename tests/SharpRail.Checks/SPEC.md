# Headless checks and E2E translations

Upstream: e2e/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The system gate for SharpRail's host/UI integration: boot real hosts (direct local
and Kestrel WebSocket RPC remote), seed real Git and profile fixtures, drive the Avalonia
workbench through real headless pointer and keyboard input, and leave nothing
machine-global behind. `E2E.md` is the inventory of which upstream scenarios are
translated and how browser concepts map to Avalonia; this spec holds the harness
contract, not that list.

## Execution model

Startup checks retain useful Projects navigation before showing the window, withhold Files controls
through held initial host routing, and require the selected Files body after mounting. Existing held
document/Git cases cover retained dock chrome, focus, cancellation and stale workspace results.

`--windows` checks portable shell quoting, cross-platform profile paths, workspace
containment and local/remote Git worktrees on every desktop test host. On Windows
it additionally exercises real ConPTY input, Unicode, geometry, replay and
reconnect, watching, busy child processes, job cleanup, exit codes, file saves,
Recycle Bin operations, native editor Ctrl shortcuts and Skia rendering. The editor
checks Unicode selection replacement separately from undoing a single insertion
back to its clean save point; Scintilla's scalar input coalescing does not promise
one undo action for a whole multi-character text-input event. Those
runtime cases must report a skip on other operating systems. The host and UI
parts join the main gate as `windows-host` and `windows-ui`.
The ConPTY fixture waits for the rendered PowerShell prompt and flushes terminal
query replies before sending its first command. Reader failures propagate, and
timeouts include the current screen and a bounded tail of raw output.

`--drawn-rendering` covers viewport-bounded Markdown preview/diff controls, offscreen
selection and source spans, Find and anchors, plus virtualized tree navigation,
recycled row actions, bounded icon construction, provider invalidation and automation expansion.
File-icon UI checks cover shared SVG tint isolation and lifetime through sibling detach/remount.
`--drawn-trees` selects just the tree
portion; both belong to the `drawn-rendering` case in the full gate. Existing Files,
Specs and graph cases continue exercising actual pointer input and host actions.
Specs retry fixtures keep injected failures active until the actual Retry press, so a background
load cannot recover during pointer settling and hide the button before the gesture.
The failed-update fixture drains earlier reads before writing the new spec, so an already
accepted read cannot observe the change that the fixture intends to reject.
Double-clicking a change row waits for its standalone diff tab before querying its content;
the first click can already mount the review's embedded diffs while that tab is loading.

`--rendering-bench markdown|markdown-diff|markdown-selection|graph|files|files-mixed|specs` is an opt-in measurement
fixture, excluded from the gate and run only when measurement is requested. Each
scrolling process mounts 2,000 blocks or rows, captures actual headless Skia frames and
scrolls 120 steps. It reports mount time/allocations, retained managed-memory delta
after collection, process resident memory, mounted visual/logical counts and scroll
frame median/p95/allocations. Screenshots go to disposable `.bench/rendering`.
`files` uses C# files; `files-mixed` alternates five file types with every seventh entry ignored.
These synthetic results are not native presentation latency or whole-app RAM savings;
timing requires attention to concurrent workloads. Blank or non-scrolling surfaces fail.

`SharpRail.Checks` is an executable, not a test framework project. The argument-free
run is the complete gate: host transport parity, host terminals, project/Git parity,
host state, file saving, layout transitions, the open-world runtime probe, and then
the headless UI checks with every upstream translation. Named modes
(`--editor`, `--files`, `--host-state`, `--specs`, `--terminals`, `--ghostty-skia`, `--sync`, `--workspaces`, `--registry`, `--change-actions`, `--review`, `--design`, `--ui`, `--runner`, `--conformance`) are focused iteration subsets
of that same code, never separate coverage; anything they run is also in the full
run. `--agent-marks` covers Codex and Claude terminal icons in inactive Projects
previews, identity arrival/removal, retained rows and switching into and out of the workspace.
`--rail` also holds workspace routing open to verify retained Projects tab controls and compares
active and inactive row heights, including duplicate-name folder subtitles and cached-workspace returns.
`--agent-launches` covers POSIX process origin, command-scoped UI markers with additions disabled,
replacement processes between polls, and persistent manual-launch notices in both provider UIs.
UI launches and unknown origins show no manual notice; hook updates and local/remote state retain origin.
Claude Code and Codex launch checks cover separate SharpRail model/effort defaults, per-launch
precedence and settings persistence. Host checks cover latest session choices, sparse status reports,
new-session resets and revival with those choices; plugin host parity includes persisted effort.
`--native-terminal` and `--native-texture` drive real macOS windows; the latter
verifies GPU texture composition, overlays/clipping, theme, clipboard/input,
resize/remounting and local/remote texture–Skia switches retaining their shells.
It also checks bounded source texture retention through repeated resizing and repainting,
the final displayed pixels after buffer reuse, control opacity, and disposal with a repaint queued.
URL regressions cover soft wraps, resize reflow, hard newline boundaries,
balanced punctuation and Skia cursor/underline pixels when modifiers change.
OSC 52 checks use a private AppKit pasteboard and scripted read confirmation,
restoring both after each case. `--ghostty-skia` covers fragmented ST/BEL writes,
Unicode, selection destinations, malformed/cancelled data, embedded NULs, large
and empty payloads, approved/denied reads, repeated permission and terminal reset.
`--terminal-resume-hint` exercises the real overlay and offer state with a portable backend,
including delayed acknowledgements, input, query replies and teardown. Supported native platforms
add the same scenarios through Skia's actual keyboard/VT path; Linux retains the portable checks.
Backpressure fixtures count exact payload bytes between output sentinels, excluding shell prompts.

`--terminals` also covers terminal revival: local and remote restarts, close, failed spawn, mode hygiene and store limits;
refused grids and start guidance (`TerminalLimitChecks.cs`); a stalled reader and a silent connection
(`TerminalBackpressureChecks.cs`); and the terminal catalog, its storage, catalog-keyed revival and a
removed worktree's shells (`TerminalCatalogChecks.cs`), including opening an empty workspace without
reserving an initial terminal on either transport; and the host replay size with its Settings control
(`TerminalReplayChecks.cs`). Yielding attach is checked locally and through WebSocket RPC in `TerminalHostChecks.cs`: a
held session keeps its client and grid, a free one is joined, and a plain attach takes over at its own size.
Watching is checked beside it on both paths: a watcher gets the screen, live output and the holder's grid, its
input, resize and kill change nothing, a watcher nobody reads holds back neither the shell nor the others and
starts over from a reset and a fresh screen, a take-over swaps roles and grid, a resize alone reaches
watchers, a watcher's resume replays only what it missed, and exit reaches every watcher.
`E2E/SharedTerminalsE2E.cs` runs two remote clients of one real WebSocket RPC host, each with its own profile, connection
and grid: the second shows the first's terminals, a terminal opened in one arrives in the other without selection,
focus or an attachment, closing in either removes it from both and from the catalog, a held terminal is shown
live and read-only rather than taken, resized or typed into, Take over and Take it back move the shell and its
size while the other side keeps watching, and a relaunched client watches the holder's terminal and attaches
once nobody holds it. A client composed as the Android phone (compact, tabs in Projects, bottom panel hidden)
lists a workspace's terminals under its row on the Projects page before opening it, returns to the current
page when one is chosen and has them all in the centre and its strip after, and a view whose renderer cannot watch, or whose host predates watching, falls back as specified.
`-- --shared-terminals` runs this file alone. The upstream translations whose second client
enters a workspace now press Take over first, and "a terminal opened in one browser never creates placement in
another" is replaced by the passive-arrival check, since SharpRail shares every catalogued terminal.
`--notifications` (`AwayNotificationChecks.cs`) enables the real Claude Code and Codex UI halves, injects
status pushes on their channels and records what would reach the system: which transitions notify, the
focused-app, continuation-Stop, interrupted-run, answered-attention, closed-tab and setting-off silences,
per-terminal replacement and coalescing, click routing to another project's workspace and tab, and the
Settings switch. It never posts a real notification; on macOS it also loads the native bridge unbundled.
`--terminal-replay` checks historical query filtering while retaining live terminal replies
(`TerminalQueryReplayChecks.cs`).
`--registry` runs one workspace-registry scenario through the direct and the WebSocket RPC adapter and requires the same
records, refusals and pushed lifecycle events from both, then the rail checks for attaching, forgetting,
background project rows and removed-workspace cleanup.
Force removal coverage requires explicit dirty-file approval, retains the branch, and preserves the
registry, label and files when Git refuses a lock; Default and attached workspaces still refuse.
Workspace lifecycle input checks Cancel preserves dirty files and Delete anyway removes only the checkout.

`--serving` covers spontaneous serving of the existing embedded host: two authenticated clients,
shared local/remote state broadcasts and plugin instance, independent project sessions, bind-failure
recovery, startup from a workspace with malformed appsettings.json, token rotation, and local/remote
shells surviving listener stop/restart. Headless Settings
coverage starts/stops serving across two app windows, applies remote state changes, retains local
controls and sessions, validates drafts, and keeps serving alive when Settings closes. Both run in
the full gate; serving configuration and secrets remain session-local.
`--native-editor-find` delivers AppKit key events through a real macOS window and its Edit menu,
covering Command+F in code and Markdown Source/Split, match navigation, focus, typing and undo.
`--markdown-find` covers Source and Split search, UTF-16 selection, match navigation, pane isolation,
overlay input, unsaved buffers and Escape focus; the same regressions run in `--markdown` and the full suite.
`--native-osc52` runs those checks plus real Metal shell output and encoded read
replies, plus local and remote clipboard round trips in both renderers and writes
after renderer switches. This native coverage also runs in `--native-texture`.
The library-only NSView fixture preserves mutable AppKit input coverage. The app
integration check uses texture controls. `--texture-fallback` forces software
rendering and verifies automatic Skia fallback with retained local/remote sessions.
`--native-direct` checks Metal and Skia session switches, resize, takeover/take-back,
clipboard, Ctrl-C and exit independently of the texture pixel probes. An HTTP
diagnostic subscriber requires zero requests for the local path and a nonzero
count for the remote control case. Metal checks must not silently fall back to Skia.
`--native-scroll` forces completed-texture handoff overlap, read-lease protection,
GPU failure/recovery and disposal wakeups. In a real Metal window, fractional
downward wheel events at the bottom preserve texture availability and rendered
pixels; upward scrolling changes the viewport and downward scrolling restores it.
`--terminal-relay` lets the checks binary act as the relay child that tabs launch.

`--design` is the design-system gate described in `src/SharpRail.UI/Rendering/COLOR.md`: it reads the UI
project's sources from the checkout around the executable (and reports a skip when there is none), then
runs the theme translations. `--design --write` only regenerates the committed output of `Rendering/Design`.

Host checks that block on async work run before the Avalonia synchronization
context is installed; UI checks run after it, on the dispatcher. A process is
serial: one headless platform, fixtures isolated per case. A failure ends that
process; there is no retry layer.

## Cases, lanes and shards

`Gate.Case` names each suite of the argument-free gate; `Runner.cs` is the entry point that decides which
of them a process runs before `Program.Checks` sees its arguments. Code outside a case is shared setup and
runs in every process; a case runs in exactly one. A suite added without a case is therefore never lost,
only repeated in each lane. Selection is by a case's ordinal in the order the gate reaches it, so it needs
no listing pass and is identical in every process of one build.

- `--lanes N` (1–16, or `auto` for half the processors clamped to 1–8) runs the gate as `N` processes of
  the same executable. Each owns shard `i/N`, its own `.bench/check-fixture-<guid>` and its own hosts on
  ephemeral ports, so lanes share nothing but the checkout. Output is relayed with a `[lane i]` prefix and
  the owner merges the lanes' results into one verdict, rejecting a case reported by two lanes. The
  argument-free run stays one serial process: unlike upstream it does not default to lanes, because a
  focused repair is the common local run and the machine is often shared.
- `SHARPRAIL_CHECKS_JOB_SHARD=k/N` gives a CI machine slice `k` of `N`; its lanes subdivide that slice into
  global shards `(k-1)·L+i` of `N·L`, so the union of all jobs is exactly the gate. The runner owns
  `--shard`: an explicit `--shard` beside the variable or beside `--lanes` is rejected. CI splits the full
  gate across three jobs this way. A run with arguments of its own ignores the variable: the children a check
  starts from this executable (a fake agent, a relay) inherit it and are no slice of the gate.
- A failed process stops at its failure and records what it still owed, the failing case and the rest of
  its shard, in `.bench/checks-last-run.txt`; lanes merge their records. `--last-failed` is a serial run of
  exactly those cases. A repair that fails again narrows the record to what is still owed, a passing one
  clears it, and a record whose case no longer sits at its ordinal is refused in favour of the whole gate.
- `--case a,b` runs named cases alone and `--list-cases` prints the selection; neither touches the record.
- These options apply to the argument-free gate only. A focused mode runs whole, in one lane, and is
  rejected with them.

## Runner ownership

Every run except the relay child holds one idle-sleep assertion on macOS before fixtures are created:
`/usr/bin/caffeinate -i -w <runner pid>`, so the display may still sleep and an abrupt exit releases it. A
run that cannot establish it fails at once; other platforms are a no-op. A lane carries its owner's pid in
`SHARPRAIL_CHECKS_OWNER` and reuses that assertion rather than holding another.

The same owner is the only signal manager. On SIGINT or SIGTERM it snapshots every descendant, forwards the
signal once to each descendant process group other than its own and individually to the processes that
share its group, waits a bounded grace, then kills the same targets whether or not they are still its
children, and exits with the signal's status. Lanes lead their own process group so one signal reaches a
lane and everything it started. `-- --runner` (also in the gate) proves this against a fixture owner with a
plain child, a child that ignores the signal and a lane with its own deaf grandchild, together with shard
composition, option conflicts, lanes, the repair loop through real child processes of a stand-in gate, the
live idle-sleep assertion in `pmset`, and packaged-file identity.

## Conformance

`-- --conformance` (also in the gate) runs the repository's two source gates; both skip visibly when the
executable runs outside a checkout.

- The boundary check discovers every project under `src` and `tests` and requires an explicit rule for
  each, so a new project cannot silently inherit access. Abstractions, Protocol, Scintilla and Ghostty
  reach nothing; Core reaches Abstractions; Client reaches Abstractions and Protocol; Remote reaches
  Abstractions, Protocol and Core; the UI reaches Abstractions, Core, Client, Scintilla and Ghostty; the
  checks reach everything. Project files (and the shared `Directory.Build.*` files, counted against every
  project) are scanned for any item naming another project; C# with comments and literals blanked, and
  XAML, are scanned for any mention of another project's root namespace, which covers using directives,
  aliases, static and global usings, qualified names and type forwarding. `bin`, `obj`, `artifacts` and
  dot-directories are excluded. Negative fixture trees go through the same scan as the repository.
- Public API analyzers guard declarations during compilation using committed API baselines.
  Missing declarations and stale signatures fail the warnings-as-errors build. Specs describe
  behavior and boundaries; the executable gate does not compare Markdown lists to assemblies.

## Packaged gate

`scripts/check-packaged.sh` runs the gate against `artifacts/SharpRail.app`: it stages a copy of the bundle
under `.bench`, adds from `artifacts/checks` the files the bundle does not ship, and starts the checks from
the bundle's own directory with `SHARPRAIL_REQUIRE_R2R=1` and `SHARPRAIL_PACKAGED_APP`. Under that variable
the gate's first case requires each file the bundle ships to sit byte-identical beside the running checks and
the product assemblies to have loaded from there. The one permitted difference is a newer version of the same
non-product assembly: the remote host the checks start needs newer `Microsoft.Extensions` abstractions than
the app ships, and one process can load only one of each, so those are replaced and named in the result
rather than counted. A product assembly that differs, an older or different assembly, or any other changed
or missing file fails. Each lane starts its own hosts in-process, so packaged lanes share no artifact host.
Arguments pass through, so lanes, shards, named cases and the repair loop apply; the stage is removed on exit.
Staging preserves shipped composite components even when the checks' corresponding assemblies
have the same version but different ReadyToRun headers. The checks executable adds missing files
and replaces only newer versions of the same non-product assembly. The macOS checks themselves
remain non-composite R2R: mixing two composite version bubbles failed in headless
render-timer initialization in the full packaged gate. Windows checks use only their own
published component set and remain composite. Same-version
framework components and product assemblies remain the shipped bytes; verification is not relaxed.
Runner fixtures cover these staging choices alongside the existing rejection cases.

## Adapter parity

Remoteness is an adapter choice, so shared behavior is proven through both
composition roots: the same Core host behind `LocalHostAdapter` and behind a
loopback `RemoteServer` with `RemoteHostAdapter` must return equal results,
propagate cancellation, and reject a bad token as unauthenticated. Multi-client
translations attach remote clients to one real WebSocket RPC host, and `E2E/CutProxy.cs`
drops and restores a single client's connection to exercise reconnect. A new host
operation adds its parity assertion here. The handshake checks cover local/remote equality, a rejected
wrong token, a stub host without the method (version 0), the capability table, and clearing and refetching
the version across a cut connection. rather than a copied feature check.

## Isolation contract

Shared input helpers reacquire redrawn controls by row and control identity, including hunk keys
when a review section contains several buttons with the same name. Double-clicking a Changes row
waits for the individual file tab so resource checks cannot inspect the intermediate review section.

Every run creates its fixture under `.bench/check-fixture-<guid>` and sets
`GIT_CEILING_DIRECTORIES` so fixture Git discovery never climbs into the checkout.
Each E2E case gets its own repository root and its own profile directory; no path
may fall back to `~/.sharprail` or the developer's real profile or host state.
`E2E/IsolatedGit.cs` gives fixture repositories, and every Git process the app
starts, a private global configuration with a throwaway identity and signing off,
restoring the environment afterwards; seeded authorship never depends on the
developer's Git config. Startup-isolation and Windows-gate cleanup clear read-only
bits on files inside their disposable fixtures before deleting them, since Git
objects are read-only on Windows. Enumeration includes hidden and system files
but does not traverse reparse points. Cases that need a probe to degrade (for example `gh`) empty
`PATH` for that case only. Pull-request checks retain only their fixture's Git
executable when testing a missing `gh`, excluding system directories even if
the runner has GitHub CLI installed; the original path is restored on failure too.
Project expansion checks observe the retained workspace container and toggle state,
including plain folders that intentionally have no Git workspace selection row.

Headless terminal tabs run real host PTY sessions (`/bin/sh`) rendered as text.
Commands print split markers so an echoed command line never satisfies an output
assertion. Held or failing host starts stand in for delayed or failed attaches.

Git-history fixtures that need a real upstream clone read it from
`SHARPRAIL_TEST_GIT_SOURCE` via a shallow, no-checkout, sparse clone. Without it
those cases print `SKIP` and the rest of the gate still runs; a skip is visible, not
a pass.

## Boundary

The checks own the scenarios under `tests/SharpRail.Checks` and `E2E/`, their
fixtures, and the isolation rules above. `E2E/E2eWorkspace.cs` is the one shared
place for input, window, fixture and `NewWindow()` helpers; `E2E/IsolatedGit.cs` is
the one place fixture repositories are created and Git is shelled out to.
When a redraw replaces an input target, the helper resolves it by name and tag
within its owning row, so repeated hunk actions remain distinct.

They consume the UI, Core, Client and Remote projects through their public
composition (`Workbench`, `WorkbenchWindow`, host adapters, `RemoteServer`), plus
Git and `/bin/sh`. Injected fakes are limited to what cannot run headless or is a
platform dialog: the folder picker, the terminal factory, and held host calls.
Fake application backends, dependence on developer state, and product code that
references the checks are forbidden.

## Verification policy

During iteration run the affected focused mode. Flake repairs replace incidental
setup with equivalent fixture state and wait for observable readiness (`Until`
conditions on real control or host state); arbitrary sleeps, retries and weakened
assertions are not synchronization. The shared E2E wait is a 30-second liveness bound rather than an
assertion: workspace flows start a dozen Git processes, which a loaded machine or parallel lanes stretch
well past ten seconds. Wait bounds in the headless checks read `Awake.Now`, a clock that stands still
while the machine sleeps, so a suspended run does not resume with every wait already expired. Before handoff, every app-affecting change runs
the full argument-free gate, with `SHARPRAIL_TEST_GIT_SOURCE` set when Git or
layout history is affected, and formatting verification. Translated upstream
coverage stays recorded in `E2E.md`, separate from SharpRail-only regression checks.
Skia terminal checks render real pixels and encode real headless input, verify
a host PTY surviving a view restart, and exercise Settings choice persistence.
Incremental rendering must match a full repaint after erasure, cursor movement
and wide/combining text updates, while leaving unrelated rows cached.
Output checks require replay to yield to input, disposal to cancel queued work,
and final output (including split UTF-8) to precede exit. `--native-skia` verifies
the GPU framebuffer, incremental pixels, theme parity, Retina resize and disposal
in a real macOS window, capturing only that window.
Window moving, zooming, platform dialogs, native Metal Ghostty rendering and final visual
verification remain native checks (`scripts/check-terminal.sh`,
`--native-terminal`, own-window captures) because the headless platform cannot
prove them. Screenshots are evidence, never the assertion.
Disclosure-anchor probes place the entire header below any sticky breadcrumb before asserting its
position; intersection with the scroll viewport alone does not prove that it is unobscured.

## Content checks

The syntax checks exercise source diff layout as well as highlighting: added and deleted files open
inline through narrow/wide resizes and live status changes, while a manual split remains available
and survives refresh. Modified files retain the width-based default.

`ContentChecks.cs` (`-- --content`, also in the default run) covers the classifier (magic numbers, ASCII-only
PDF byte-only, SVG and HTML flagged active, NUL and invalid UTF-8, LFS pointer), then the same fixture
through the embedded host and a real WebSocket RPC host with equal results: byte-only diff sides empty with metadata
and a frozen original commit, no commit for a root commit or untracked file, a binary notice for an untracked
byte-only diff, exact bytes for the commit, working tree and index, an original that survives the branch
moving, and refusal of refs, abbreviations, unknown, tree and blob ids, path escape, missing paths and a
symbolic link. The headless UI part checks how a byte diff is recognised and then runs the resource checks.

## Resource checks

`--frontmatter` runs the preview-property translations independently; `--markdown` and the UI gate
include them. Coverage follows text/sequence/mapping edits through the shared source draft, undo/redo,
Enter/blur/Escape, folding and focus retention, chips, suggestions, unique property/mapping keys,
conversion, Save and external-write conflicts. Serialization guards preserve the body verbatim and
round-trip quoted Unicode values, punctuation and empty shapes; unsupported YAML stays read-only.
Platforms without the native source editor assert read-only values, chips, folding and file
immutability; supported platforms retain the full editing, undo and save checks.

`MarkdownTableDiffChecks.cs` (`-- --markdown-tables`, also in the default gate) checks added and removed
Markdown tables, cell/header edits, row changes and differing column counts. It asserts both parsed table
structure and native preview grids with marked cell text, including quoted tables, escaped pipes, empty
cells, header-only tables and frontmatter. Fenced code and ordinary prose with pipes remain outside table
handling; unchanged table source and cancellation are preserved.

`ResourceChecks.cs` (`-- --content` and `-- --documents`, also in the default run) covers the renderer
registry (rank order, glob and MIME matching with the extension fallback, the required fallbacks, intent
support), the file pane (toggle, view reuse, view state written to the tab, dropped on a renderer change,
late writes ignored, captured on close and restored), SVG sanitising and rasterising, the table model
(delimiter sniffing, quoting, row alignment and changed cells), the JSON model (dialect and invalid text,
structural diff by key and identity, formatting-only changes), the notebook model and the rendered-diff
focus (context runs, single-unit gaps, twin blocks, unmarked changes, list items). It then drives a real
headless window over a fixture repository: a byte-only file as a card with Save a copy, an LFS pointer
card, a picture's fit, natural size and zoom with an in-place reload, an SVG drawn with Source beside it, a
table, a JSON tree, a notebook, a Markdown file's toggle surviving a reload, raw HTML in Markdown (placed
pictures, dropped script, closed and open disclosures), and diffs of each: picture
2-up, swipe, onion skin and difference, a byte diff refreshing when the file changes under its tab, PDF
cards only, table, JSON and notebook diffs, a Markdown diff's rendered and source views with the choice
kept across a refresh, collapsed unchanged runs with kept list numbers and an expansion surviving a
refresh, an oversized Markdown diff falling back to source, and an LFS pointer diff.
`-- --documents` also runs the upstream translations that open file and diff bodies (preview tabs,
Markdown links, alerts and Mermaid, the editor, and with `SHARPRAIL_TEST_GIT_SOURCE` the Changes diff,
rendered diff and live refresh suites).

## Continuous Changes review checks

`ChangesReviewChecks` is native regression coverage for continuous review, separate from upstream
translations: single-click review/double-click file diff, deferred generated sections, content-keyed Keep
with duplicate and line-shift cases, explicit unviewed state across remounts, app layout switches,
sidebar marks/navigation, bulk viewed progress, keyboard input, live edits, Revert/Undo, sticky header
ownership, historical scope immutability, compact headers, unwrapped horizontal code scrolling and
outer-only vertical input over code and naturally sized Markdown previews through resizing. It runs through the ordinary UI gate over an isolated Git
fixture; rendered captures are supporting evidence.
On platforms without Scintilla, the same case verifies complete read-only patches,
Preview/Source selection and navigation, hidden unsupported source controls, resizing,
stacked/one-file navigation and bulk viewed progress. Async retention verifies source
control identity, text replacement and scroll on both renderers; editor selection,
hunk actions and editor input remain assertions on editor-supported platforms.
The focused `--case ChangesReviewChecks` passed on macOS after this portable coverage
was added; actual Linux fallback execution remains the CI runner's acceptance check.

Review regression coverage also checks compact end-marker sizing, bordered file spacing, retained Markdown diff bodies across attachment, and refresh after changes while hidden. Git Graph commit review coverage opens by left click through both host modes.
`--review-retention` runs the watcher-independent retention subset: delayed hidden and visible refreshes
keep existing content, source selection and scroll offset until fresh content arrives, while changed
diffs still replace the body. Markdown coverage includes natural height, resizing and returning to a tab.

Dock input startup pumps the headless main loop and layout while awaiting the Git
workspace, because startup crosses animation frames before its Git probe. The same
15-second bound and mounted workspace/Changes-tool assertions remain; draining
only dispatcher jobs does not advance those frames. The focused `DockInputChecks`
case passed on macOS with this pump; Linux acceptance is the CI job.

## Reconnect checks

`ReconnectChecks.cs` (`-- --reconnect`, also in the default run) runs against the embedded host and real
WebSocket RPC hosts. Named failures: a vanished commit is `HostErrorCode.UnknownCommit` with the same message from
the snapshot, diff, diff-sides and byte reads of both hosts, an unnamed failure stays unnamed, and `NotGit`
and `AlreadyOpen` survive the state service's transport from a host that refuses with them. Replay: a
state change whose connection `CutProxy` drops in flight completes with the first run's reply or refusal
once the proxy allows reconnecting, the host having run it once and to completion; a host reporting an
older version gets no replay and the call fails. With explicit request ids the host returns a kept
reply, scopes ids to their client, refuses a reused id with another payload, releases results a resume no
longer names, refuses a client over its request limit and never reruns a reply it could not keep; a save
sent twice under one id succeeds twice through `RemoteServer`, while a new id meets the conflict check.
`E2E/ReconnectE2E.cs` then drives a remote window behind `CutProxy` through two losses: the connection
reports each transition once, every reconnect is one new generation, a file written while disconnected
appears after it, and one written afterwards appears through the resubscribed watch; the capability gate
is open for the host's own version, closed above it and closed while disconnected. A second case moves
the branches of the Default and a created workspace while disconnected and requires both rows corrected
after the reconnect with no row added, removed or navigated away from. A per-request timeout is checked
from the `grpc-timeout` the host receives: the adapter default, a raised scope on a read and a mutation,
a nested scope across threads, restoration on leaving each, and expiry of a lowered one.

## Process checks

`ProcessChecks.cs` (`-- --process`, also in the default run) drives the host's bounded child runner with
real `sh` and `git` children; [Terminals.SPEC.md](../../src/SharpRail.Host.Core/Terminals.SPEC.md) lists
the cases.

## Git host checks

`GitHostChecks.cs` (`-- --git-host`, also in the default run) runs one scenario list against the embedded
host and a real WebSocket RPC host over twin fixtures (a bare origin and a clone one commit ahead on `feature`) and
requires equal logs. It covers the ref-shape table, and every door refusing option, range, reflog and
revision syntax before Git runs, with refs unchanged afterwards and a crafted `origin/HEAD` skipped; the
pinned scope (commits, working edits and untracked files from one commit, unmoved by a later commit,
abbreviated, unknown and malformed ids, revert and undo); and the creation base and re-pointed review
target through create, re-point, restore, an unresolvable ref, a refused range and removal; badge totals
without a target, against one, with working edits, equal to the snapshot's sums and refused outside the
project; and a prefetch that reports a move once, nudges the watcher of the workspace measured against the
ref, stays silent when nothing changed and refuses a local branch or a range. The mode then
runs `ChangesScopeE2E` headless, whose SharpRail-only last case requires the rail badge to total the listed changes, stores a picked target
on the host and follows one re-pointed by another client; it needs `SHARPRAIL_TEST_GIT_SOURCE`.

## Host state store checks

`StateStoreChecks.cs` (`-- --host-state`, with `StateChecks.cs`; also in the default run) covers what the
store keeps on disk: unknown settings surviving a valid update while unknown change keys stay rejected, a
half system theme pair dropped on load, a theme change without a mode switching to fixed mode, and one
stable installation identity across reopen and sixteen racing first launches, with a malformed identity
file never replaced. It then covers project identity (ids and unique slugs minted at open, kept across
close and reopen with `lastOpened` advancing, persisted, dropped when forgotten, minted for an older state
file) and the `HostErrorCode.AlreadyOpen` refusal of an open project's linked worktree. `ProjectPathChecks.cs` in the
same mode covers `~` and relative path resolution and equal path classification through the embedded host
and a real WebSocket RPC host; `ProjectPickerE2E` adds the UI refusal of a missing folder and of a file in the notice dialog.

## File read checks

`ProjectChecks.cs` (`-- --files`) reads the same fixture through the embedded host and a real WebSocket RPC host:
NUL-bearing and invalid UTF-8 files answered as empty text with metadata, other control characters kept
as text, a PNG under another name returned as a picture and text named `.png` as text, `.git` refused for
reads, listings, saves and byte reads in any letter case, and a missing leaf reported by the read rather
than by containment. `FilesE2E` opens a byte-only file and requires the byte card with its size and no
window error.

`WatchChecks.cs` (`-- --watchers`, also in `--files` and the default run) covers the shared watchers:
two subscriptions on one root sharing one set and both notified, a root deleted and recreated restarting
the set once with a rescan to existing subscribers, release with the last subscriber, and the pre-warm
pool through the embedded host and a real WebSocket RPC host: eight kept, the least recently warmed evicted,
re-warming, a subscription taking over a pre-warmed set and never being evicted, a vanished folder reaped
and a relative path refused. `ProjectPathChecks.cs` checks that untracked line counts follow the shared
classification.

## Spec catalog checks

`SpecChecks.cs` (`-- --specs`, also in the default run) builds a fixture with a byte order mark, CRLF,
quoted and commented scalars, flow and block lists, a `SPEC.md` without frontmatter, a triple duplicate id
across a directory, a file and another directory, a parent ring, a dangling link and an ignored directory.
It requires the winner chosen by the one-list sort, every link kind forwards and backwards, the three
validation lists, parse and build counters that stay still for an unchanged workspace and move by one for
one edited file, equal graphs through the embedded host and a real WebSocket RPC host, the durable-spec query
ignoring a lone task spec and answering false for a missing folder, and the index dropped with its
worktree. Authoring covers the path rule's refusals, create, an exact-text frontmatter update that keeps
comments, CRLF, the byte order mark and the body, refused identity edits, and delete.
`E2E/SpecsPanelE2E.cs` (in `--workspaces`; `-- --specs-panel` runs it with the Welcome translations)
covers the panels on top: Welcome leading with Set up project for a project whose only spec is a task
spec, that card opening the Create workspace dialog, the cards following a durable spec appearing and
disappearing on disk, and a failed Specs read keeping the previous tree behind the inline hint until
Retry succeeds.

## Change actions

`ChangeActionChecks.cs` (`-- --change-actions`, also in the default run) is SharpRail-only coverage on an
isolated repository with `SHARPRAIL_TRASH_DIR` pointed into the fixture. It checks the toast queue as a
model (coalescing, the cap evicting only actionless toasts, default and own lifetimes) and as cards (order,
width, expiry without rebuilding the others, action, dismissal), then reverts through the diff tab: one of
two change blocks with its spans, Undo, a file moved by `E2eHost.BeforeRevert` after the tab drew it (nothing
reverted, notice, reloaded diff), Revert file and Undo, a new file to the trash and back, and no revert
controls on a staged diff. Block controls are asserted on macOS only. It also opens a missing folder
through the Add project menu and requires the single-button notice instead of the error line, and checks
that the scope menu reads its commit rows and the “No uncommitted changes” probe only when opened. The translated
scope-menu cases open the menu before expecting commit rows and require “No uncommitted changes” on a clean
worktree; the create-workspace fetch failure is asserted on its toast.

## Review panel

`ReviewChecks.cs` (`-- --review`, also in the default run) drives the Review panel's pull request flow against
the host checks' bare origin and `gh` PATH shim: the compose dialog starting from the host draft, a failed push
keeping the dialog and its edits, creation from the edited fields, the chip and its link, the dirty-file
notice, unpushed commits and Push updates through the dialog, an unauthenticated `gh` handing over to setup
guidance with Try again resubmitting the last edits, and the compare page. The diverged state cannot be
produced by a fixture origin that is reachable only for pushes, so `E2eHost.Review` supplies the behind count
at the host boundary; the check then requires the integrate command and that nothing was pushed. Links go
to an injected `OpenLink`, never a browser.

## Mermaid viewer

The Markdown Mermaid translation also covers the full-screen viewer's gestures. A trackpad pinch is raised as
Avalonia's magnify event, which the headless platform cannot inject as raw input: the viewer must claim it,
reach 150% from one half-step as upstream's gesture case does, and stay within 25–500%. A primary-button drag
pans the zoomed diagram by the pointer's travel, clamps at the edges, ends on release and ignores other
buttons and unpressed movement; Reset returns the scroll origin. Malformed-source fallback, full-screen
growth, zoom reset and Escape are in the same case; theme checks cover re-rendering.

## Android client

`AndroidClientChecks` compiles the client's `HostEndpoint.cs` into the checks, since the Android project builds
only with the Android workload: address parsing (default port, written ports including the scheme's own,
HTTPS, IPv6, rejected input) and the remembered endpoint (absent, saved, damaged, forgotten). `-- --android`
runs it. The boundary scan gives `SharpRail.Android` the UI's dependencies without `SharpRail.Host.Remote`.
Windowing, the connect screen and everything on a device are not asserted.

## Quit and close commands

`QuitConfirmationChecks` translates upstream's quit-confirmation cases against a fake clock and scheduler:
tap expiry, double press, hold, key repeat, re-arming, focus loss before and during release, unreadable key
state, a missing hint surface, and idempotent quit. `AppCommandChecks` drives a headless window with an
injected shutdown action: quit hint and confirmation paths, Alt+F4 left alone, direct quit, Mod+W on the
selected tab, tool/folded/hidden no-ops, modal dismissal, and typed-letter versus physical-key matching.
`-- --commands` runs both. A busy-terminal confirmation from Mod+W and a terminal retaining Ctrl+W off
macOS are not asserted.

## Shell checks

`ShellChecks` (`-- --shell`, also in the default run) covers the window shell. `LocationBarChecks` drives
the header's segments with real input: captions and Project Home state, the project and workspace
switchers, the header's workspace actions sharing Projects' rename (one input, one host label change,
abandoned when the window leaves the workspace) and Remove (the dialog names its workspace and dismisses
on a workspace change, removing nothing), the branch card's Copy and its comparison picker agreeing with
Changes in both directions, pills staying out of the title bar's drag region, and the drop order as the
header narrows. A rename left pending by an unreachable host being abandoned is not asserted.

The rest of `ShellChecks` covers the application menu (composition, chords, a text box taking editing
commands directly, any other control receiving them as chords with Delete never forwarded, Zoom, Close
closing a dialog but only the selected tab of a workbench window, Minimize), window chrome (the full-screen
inset, the double-click preference mapping and its effect, ignored in full screen; pinch against one
baseline, both bounds, resting between steps, persistence, the chords stepping from there, the routed
magnify gesture and a claimed one), region errors (a body that throws while being built and one that throws
during layout each show a notice in their own group while a sibling body and the window carry on, and the
region shows the next body that works) and arrangement isolation (a tab drag with its drop preview, its
cancellation and a side resize preview detach no body and replace no tab strip; selecting a tab swaps only
that group's body and no strip; focusing groups touches nothing). The native menu bar, a real trackpad and
reading `AppleActionOnDoubleClick` need a real macOS window and are not asserted.

It also covers locations (the link codec round trip and every invalid form, the history list, Back and
Forward by method and by chord across files, workspaces and Project Home, a link opening a file, a link to
a removed workspace landing on Project Home, a link to an unlisted project landing on Welcome and Back
returning from it), the Create workspace project picker (absent with one project; with two it lists and
checks them, loads the picked project's branches without moving the window, and Create makes the workspace
in the picked project), commit menus (relative-time wording against a fixed now, forty long-subject commits
each reading `just now`, the open menu inside its bounds and scrolling vertically with no horizontal
overflow) and inert links (resolution inside the worktree, every escaping and malformed form refused, and a
rendered document where only the followable link is a control). `--link` at launch and the mouse's
back and forward buttons are not asserted.

## Not yet ported

- The upstream tool-rail journeys: pane clusters and their distinct join/create drop targets,
  independent terminal groups, dimmed unplaced tools, remembered terminal selection across folding and
  reload, linked collapsed regions, focus without activation, and workspace switches during a drag.
  Shape-change probes must retain the same document, terminal, Projects and Changes controls through
  fold/hide/show; compressed-resize probes assert actual compression without accidentally folding panes.
- Rail layout regressions for replacement before bottom measurement, explicit drops into folded
  destinations and workspace switches during every resize gesture. Assert live rollback, unchanged
  persistence, retained controls, reload and a subsequent working resize, beyond the cancellation notice.
  Project Home and Welcome also need the left rail/header geometry, visibility chords and focus endpoints
  checked across a workspace round trip, with no right or bottom rail there. The upstream bottom-fold
  contract now occupies no body space and restores through the edge rail or `Ctrl+F6`; existing native
  translations still assert the earlier 27px fold.
- Fixture commits outside `E2E/IsolatedGit.cs`: `ChangeChecks`, `ContentChecks`, the seed commits of
  `PullRequestChecks`, `E2E/ChangesFixture.cs` and callers of the static `IsolatedGit.Repository` without an
  `IsolatedGit` in scope still commit under the developer's global Git configuration. Where that signs
  commits through an agent, the gate needs the agent for throwaway commits and aborts with the agent's error
  when it does not answer, which breaks the isolation contract above.
- Interrupt cleanup on Windows (retained descendant ids and `taskkill`); POSIX is covered.
- Login-shell PATH repair fixture isolation: if host startup begins probing the login shell, checks must
  supply a fixture shell that recognizes the probe by a shared marker and answers with that marker and
  the hermetic PATH, without importing the developer's login environment, and delegates normal
  terminal execution to a real shell, so developer tools cannot leak into isolated fixtures.

`--tailscale` covers builtin Tailscale bootstrap, approved-token authentication and revocation using fake
peer identities over real HTTP/WebSocket RPC. It never reaches the developer's Tailscale installation or tailnet.

`MarkdownSelectionChecks` runs with `--drawn-rendering` and the default gate. It checks reverse
and shrinking drags in a virtual document, source-line observers, offscreen Shift-click and
Copy, plus rendered selection, inline/font invalidation and detach/remount under every theme.
Mixed-height headings, paragraphs and styled code fences keep the scroll extent and requested
offset stable across viewport realization; narrowing the preview reflows its geometry while
retaining bounded mounted blocks.
`--markdown-link-layout` isolates long-link geometry at narrow/wide widths and normal/larger fonts.
It covers normal Markdown, mixed and whole-link insert/delete diffs, and inline HTML, rejecting
intersection with surrounding text while retaining navigation, selection and diff decoration.
It also runs with `--drawn-rendering` and the default gate.
`--markdown-code` isolates fenced-code Copy geometry at wide/narrow widths and with
long first lines, horizontal scrolling and exact clipboard content. It also runs with
`--drawn-rendering` and the default gate.
`--rendering-bench markdown-selection` measures 120 pointer updates with a source-line observer
and headless Skia frame capture. `SHARPRAIL_BENCH_MARKDOWN` selects a source file; otherwise it
uses a large generated document. It reports input/frame median and p95 plus allocated bytes,
not native presentation latency.
Start work checks cover custom terminal commands, preserved command/prompt/image drafts, removable
image previews and host attachment delivery. Workspace-tool checks verify image upload over both
adapters and first-title workspace naming without replacing manual or subsequent workspace names.

Markdown Mermaid diff checks cover unchanged, changed, added/removed and nested fences, exact fence syntax, source-side ranges and shared renderer/error fallback. They run in the full gate and the focused `--markdown-tables` mode alongside structural Markdown diff checks.

`--file-drop` covers direct/remote uploads, collision and containment refusals, copy/move, private
attachments and cancellation, then headless drag thresholds and routed tree/agent drop behavior.
The same host, gesture and UI cases join the full gate.

## Recorded verification scope

A pass belongs to the tested source tree, runtime, adapter and fixture. Compiling Android or WASM is
not device/browser acceptance; a source check is not a packaged check. Reused byte-identical slices
retain their existing evidence, but interrupted lanes and skipped platform branches never count as
passes. Record new durable scope or limits in the owning spec and dated task outcomes in context-log.md;
keep translated-case coverage in E2E.md and current cross-module gates in UPSTREAM.md.

The 2026-10-15 workspace-13 integration completed all 131 catalog cases in aggregate, reusing completed
cases and running failed/unfinished cases after interrupted lanes. It was not one uninterrupted full
gate. The final shared focus/startup changes received a desktop Release build and focused checks;
Android and browser compilation had passed before that final delta and were not repeated for it.

That source result does not close the distinct 2026-10-14 packaged audit: its settings observer timed
out, terminal/lane work was interrupted, Blueprint UI ended unfinished and Codex recovery failed.
Thirteen targeted packaged continuation cases passed; a clean complete packaged gate was not obtained.
The isolated package passed ReadyToRun/file-identity and strict/deep signature checks without replacing
the canonical running application. Current unresolved acceptance is consolidated in
[UPSTREAM.md](../../UPSTREAM.md#outstanding-acceptance-work).

Spec validation at that integration reported four existing Branch Graph links to absent upstream IDs
(module-contracts, module-plugin-ui, module-server, module-web). This is a recorded graph finding,
not a reason to treat later validation as automatically green or to weaken the validator.

The later 2026-10-15 workspace-48 source integration completed all 139 registered cases in aggregate:
110 from the initial four-lane gate, then 12, 3, 5, 6 and 3 from serial unfinished-case resumes.
Focused checks covered each repaired fixture and the final Git-probe and Host-settings deltas; the
desktop Release compiler and touched-file formatting also passed. This was not an uninterrupted
full pass. Proven Gitless fixtures remain Gitless; fixtures exercising Git panes initialize their own
repositories and wait for Git capability before interacting. A pre-existing caught change-row
reconciliation index exception was observed during rendered-diff checks and remains unresolved.
This source result does not establish packaged, browser, Android-device, native-GPU or Windows-runtime
acceptance, and predates the separately integrated browser prose/AOT and Windows-script changes.

Watcher hygiene: `--watcher-hygiene` exercises real Specs/Blueprint concurrent and repeated reads, canonical-root sharing, owner accounting, plugin disable, project close/reopen, runtime teardown and startup synchronization-context isolation. `--watchers` covers native-start failure/backoff/replacement lifecycle. Accounting distinguishes managed allocations, enabled sets, logical leases, idle roots and retries; it is not an OS FSEvents allocation measurement.

`--native-fsevents` requires `scripts/check-fsevents.sh` and its scoped test-only DYLD interposer.
It proves interception with a control watcher and checks native allocation/start/stop/release balance
across gitless reads/removal and real isolated Git-host lifecycles, including metadata/refs and framework-created streams. It is deliberately outside
the argument-free managed gate; no production native interposition is enabled by these checks.

Fake-agent child processes recognize their fixture marker alongside launcher-supplied options, including `--plugin-dir`, so they never enter the check gate recursively.

Git UI fixtures wait for deferred scope reconciliation and Git-required tool availability before interacting; mounting the workspace alone does not establish optional Git-pane readiness. Commit-catalog isolation assertions still apply after reconciliation.
