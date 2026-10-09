# Headless checks and E2E translations

Upstream: e2e/SPEC.md (revision: [UPSTREAM.md](../../UPSTREAM.md))

## Responsibility

The system gate for SharpRail's host/UI integration: boot real hosts (direct local
and Kestrel gRPC remote), seed real Git and profile fixtures, drive the Avalonia
workbench through real headless pointer and keyboard input, and leave nothing
machine-global behind. `E2E.md` is the inventory of which upstream scenarios are
translated and how browser concepts map to Avalonia; this spec holds the harness
contract, not that list.

## Execution model

`SharpRail.Checks` is an executable, not a test framework project. The argument-free
run is the complete gate: host transport parity, host terminals, project/Git parity,
host state, file saving, layout transitions, the open-world runtime probe, and then
the headless UI checks with every upstream translation. Named modes
(`--editor`, `--files`, `--host-state`, `--specs`, `--terminals`, `--ghostty-skia`, `--sync`, `--workspaces`, `--registry`, `--change-actions`, `--review`) are focused iteration subsets
of that same code, never separate coverage; anything they run is also in the full
run. `--native-terminal` and `--native-texture` drive real macOS windows; the latter
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
`--terminals` also covers terminal revival: local and remote restarts, close, failed spawn, mode hygiene and store limits;
refused grids and start guidance (`TerminalLimitChecks.cs`); a stalled reader and a silent connection
(`TerminalBackpressureChecks.cs`); and the terminal catalog, its storage, catalog-keyed revival and a
removed worktree's shells (`TerminalCatalogChecks.cs`); and the host replay size with its Settings control
(`TerminalReplayChecks.cs`).
`--registry` runs one workspace-registry scenario through the direct and the gRPC adapter and requires the same
records, refusals and pushed lifecycle events from both, then the rail checks for attaching, forgetting,
background project rows and removed-workspace cleanup.
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
`--terminal-relay` lets the checks binary act as the relay child that tabs launch.

Host checks that block on async work run before the Avalonia synchronization
context is installed; UI checks run after it, on the dispatcher. The runner is
serial: one process, one headless platform, fixtures isolated per case rather than
per lane. Failures throw and end the run; there is no retry layer.

Primary-modifier chords are chosen from the platform the product would see (Meta on
macOS, Control elsewhere), never hard-coded to the developer's machine.

The published checks (`artifacts/checks`) with `SHARPRAIL_REQUIRE_R2R=1` additionally
require ReadyToRun headers on the product assemblies, while the open-world probe
always requires JIT, runtime IL emission and collectible assembly loading. Trimming
or NativeAOT would therefore fail the gate rather than silently change the runtime.

## Adapter parity

Remoteness is an adapter choice, so shared behavior is proven through both
composition roots: the same Core host behind `LocalHostAdapter` and behind a
loopback `RemoteServer` with `RemoteHostAdapter` must return equal results,
propagate cancellation, and reject a bad token as unauthenticated. Multi-client
translations attach remote clients to one real gRPC host, and `E2E/CutProxy.cs`
drops and restores a single client's connection to exercise reconnect. A new host
operation adds its parity assertion here. The handshake checks cover local/remote equality, a rejected
wrong token, a stub host without the method (version 0), the capability table, and clearing and refetching
the version across a cut connection. rather than a copied feature check.

## Isolation contract

Every run creates its fixture under `.bench/check-fixture-<guid>` and sets
`GIT_CEILING_DIRECTORIES` so fixture Git discovery never climbs into the checkout.
Each E2E case gets its own repository root and its own profile directory; no path
may fall back to `~/.sharprail` or the developer's real profile or host state.
`E2E/IsolatedGit.cs` gives fixture repositories, and every Git process the app
starts, a private global configuration with a throwaway identity and signing off,
restoring the environment afterwards; seeded authorship never depends on the
developer's Git config. Cases that need a probe to degrade (for example `gh`) empty
`PATH` for that case only.

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
assertions are not synchronization. Before handoff, every app-affecting change runs
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

## Content checks

`ContentChecks.cs` (`-- --content`, also in the default run) covers the classifier (magic numbers, ASCII-only
PDF byte-only, SVG and HTML flagged active, NUL and invalid UTF-8, LFS pointer), then the same fixture
through the embedded host and a real gRPC host with equal results: byte-only diff sides empty with metadata
and a frozen original commit, no commit for a root commit or untracked file, a binary notice for an untracked
byte-only diff, exact bytes for the commit, working tree and index, an original that survives the branch
moving, and refusal of refs, abbreviations, unknown, tree and blob ids, path escape, missing paths and a
symbolic link. The headless UI part checks how a byte diff is recognised and then runs the resource checks.

## Resource checks

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

## Reconnect checks

`ReconnectChecks.cs` (`-- --reconnect`, also in the default run) runs against the embedded host and real
gRPC hosts. Named failures: a vanished commit is `HostErrorCode.UnknownCommit` with the same message from
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
host and a real gRPC host over twin fixtures (a bare origin and a clone one commit ahead on `feature`) and
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
and a real gRPC host; `ProjectPickerE2E` adds the UI refusal of a missing folder and of a file in the notice dialog.

## File read checks

`ProjectChecks.cs` (`-- --files`) reads the same fixture through the embedded host and a real gRPC host:
NUL-bearing and invalid UTF-8 files answered as empty text with metadata, other control characters kept
as text, a PNG under another name returned as a picture and text named `.png` as text, `.git` refused for
reads, listings, saves and byte reads in any letter case, and a missing leaf reported by the read rather
than by containment. `FilesE2E` opens a byte-only file and requires the byte card with its size and no
window error.

`WatchChecks.cs` (`-- --watchers`, also in `--files` and the default run) covers the shared watchers:
two subscriptions on one root sharing one set and both notified, a root deleted and recreated restarting
the set once with a rescan to existing subscribers, release with the last subscriber, and the pre-warm
pool through the embedded host and a real gRPC host: eight kept, the least recently warmed evicted,
re-warming, a subscription taking over a pre-warmed set and never being evicted, a vanished folder reaped
and a relative path refused. `ProjectPathChecks.cs` checks that untracked line counts follow the shared
classification.

## Spec catalog checks

`SpecChecks.cs` (`-- --specs`, also in the default run) builds a fixture with a byte order mark, CRLF,
quoted and commented scalars, flow and block lists, a `SPEC.md` without frontmatter, a triple duplicate id
across a directory, a file and another directory, a parent ring, a dangling link and an ignored directory.
It requires the winner chosen by the one-list sort, every link kind forwards and backwards, the three
validation lists, parse and build counters that stay still for an unchanged workspace and move by one for
one edited file, equal graphs through the embedded host and a real gRPC host, the durable-spec query
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

- Parallel lanes: splitting the gate across independent processes with lane-owned fixtures and merged results.
- CI machine sharding composed with local lanes so every case runs exactly once across all jobs, with
  conflicting explicit shard selection rejected; packaged checks need their own isolated artifact hosts.
- A last-failed repair loop that reruns only the previously failing cases.
- An idle-sleep assertion held by the runner for the duration of a macOS run.
- Signal forwarding and forced cleanup of every descendant process when the runner is interrupted.
- A gate that runs the full suite against the packaged `artifacts/SharpRail.app` rather than the built assemblies.
- Login-shell PATH repair fixture isolation: if host startup begins probing the login shell, checks must
  supply a fixture shell that answers the environment probe with the hermetic PATH and delegates normal
  terminal execution to a real shell, so developer tools cannot leak into isolated fixtures.
- Mermaid drag-pan and pinch-gesture regression checks. Existing provider-free Markdown checks cover
  malformed-source fallback, full-screen growth, zoom reset and Escape; theme checks cover re-rendering.
