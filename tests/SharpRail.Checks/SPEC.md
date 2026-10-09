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
(`--editor`, `--files`, `--terminals`, `--ghostty-skia`, `--sync`, `--workspaces`, `--registry`) are focused iteration subsets
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
symbolic link. The headless UI part opens `BinaryDiffView` for a PNG (two pictures) and a PDF (cards only)
and requires no replacement characters.

## Quit and close commands

`QuitConfirmationChecks` translates upstream's quit-confirmation cases against a fake clock and scheduler:
tap expiry, double press, hold, key repeat, re-arming, focus loss before and during release, unreadable key
state, a missing hint surface, and idempotent quit. `AppCommandChecks` drives a headless window with an
injected shutdown action: quit hint and confirmation paths, Alt+F4 left alone, direct quit, Mod+W on the
selected tab, tool/folded/hidden no-ops, modal dismissal, and typed-letter versus physical-key matching.
`-- --commands` runs both. A busy-terminal confirmation from Mod+W and a terminal retaining Ctrl+W off
macOS are not asserted.

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
