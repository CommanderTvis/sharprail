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
(`--editor`, `--files`, `--terminals`, `--ghostty-skia`, `--sync`, `--workspaces`, `--runner`, `--conformance`) are focused iteration subsets
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
`--terminals` also covers terminal revival: local and remote restarts, close, failed spawn, mode hygiene and store limits.
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
  gate across three jobs this way.
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
- The public-surface check enrols a spec by the `public-surface-checked` tag. An enrolled spec must keep a
  `## Public surface` section of bare backticked identifiers and sit beside its project; the list must equal
  the assembly's public top-level types in both directions. `src/SharpRail.Host.Abstractions/SPEC.md` is
  enrolled. Unenrolled specs stay descriptive.

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

## Content checks

`ContentChecks.cs` (`-- --content`, also in the default run) covers the classifier (magic numbers, ASCII-only
PDF byte-only, SVG and HTML flagged active, NUL and invalid UTF-8, LFS pointer), then the same fixture
through the embedded host and a real gRPC host with equal results: byte-only diff sides empty with metadata
and a frozen original commit, no commit for a root commit or untracked file, a binary notice for an untracked
byte-only diff, exact bytes for the commit, working tree and index, an original that survives the branch
moving, and refusal of refs, abbreviations, unknown, tree and blob ids, path escape, missing paths and a
symbolic link. The headless UI part opens `BinaryDiffView` for a PNG (two pictures) and a PDF (cards only)
and requires no replacement characters.

## Mermaid viewer

The Markdown Mermaid translation also covers the full-screen viewer's gestures. A trackpad pinch is raised as
Avalonia's magnify event, which the headless platform cannot inject as raw input: the viewer must claim it,
reach 150% from one half-step as upstream's gesture case does, and stay within 25–500%. A primary-button drag
pans the zoomed diagram by the pointer's travel, clamps at the edges, ends on release and ignores other
buttons and unpressed movement; Reset returns the scroll origin. Malformed-source fallback, full-screen
growth, zoom reset and Escape are in the same case; theme checks cover re-rendering.

## Quit and close commands

`QuitConfirmationChecks` translates upstream's quit-confirmation cases against a fake clock and scheduler:
tap expiry, double press, hold, key repeat, re-arming, focus loss before and during release, unreadable key
state, a missing hint surface, and idempotent quit. `AppCommandChecks` drives a headless window with an
injected shutdown action: quit hint and confirmation paths, Alt+F4 left alone, direct quit, Mod+W on the
selected tab, tool/folded/hidden no-ops, modal dismissal, and typed-letter versus physical-key matching.
`-- --commands` runs both. A busy-terminal confirmation from Mod+W and a terminal retaining Ctrl+W off
macOS are not asserted.

## Not yet ported

- Fixture commits outside `E2E/IsolatedGit.cs`: `ChangeChecks`, `ContentChecks`, the seed commits of
  `PullRequestChecks`, `E2E/ChangesFixture.cs` and callers of the static `IsolatedGit.Repository` without an
  `IsolatedGit` in scope still commit under the developer's global Git configuration. Where that signs
  commits through an agent, the gate needs the agent for throwaway commits and aborts with the agent's error
  when it does not answer, which breaks the isolation contract above.
- Interrupt cleanup on Windows (retained descendant ids and `taskkill`); POSIX is covered.
- Login-shell PATH repair fixture isolation: if host startup begins probing the login shell, checks must
  supply a fixture shell that answers the environment probe with the hermetic PATH and delegates normal
  terminal execution to a real shell, so developer tools cannot leak into isolated fixtures.
