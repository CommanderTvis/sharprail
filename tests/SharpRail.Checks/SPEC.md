# Headless checks and E2E translations

Upstream: e2e/SPEC.md @ 12830b08

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
(`--editor`, `--terminals`, `--sync`, `--workspaces`) are focused iteration subsets
of that same code, never separate coverage; anything they run is also in the full
run. `--native-terminal` and `--terminal-relay` are not check subsets: the first
drives the real Ghostty bridge on macOS, the second lets the checks binary act as
the relay child that terminal tabs launch.

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
operation adds its parity assertion here rather than a copied feature check.

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
Window moving, zooming, platform dialogs, Ghostty rendering and final visual
verification remain native checks (`scripts/check-terminal.sh`,
`--native-terminal`, own-window captures) because the headless platform cannot
prove them. Screenshots are evidence, never the assertion.

## Not yet ported

- Parallel lanes: splitting the gate across independent processes with lane-owned fixtures and merged results.
- A last-failed repair loop that reruns only the previously failing cases.
- An idle-sleep assertion held by the runner for the duration of a macOS run.
- Signal forwarding and forced cleanup of every descendant process when the runner is interrupted.
- A gate that runs the full suite against the packaged `artifacts/SharpRail.app` rather than the built assemblies.
