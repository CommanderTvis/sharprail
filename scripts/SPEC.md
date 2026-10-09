# Repository scripts

Upstream: scripts/SPEC.md (revision: [UPSTREAM.md](../UPSTREAM.md))

## Responsibility

Repository-wide tooling that does not belong to one project: installing the
checkout-local SDK, building pinned native dependencies, native probes, packaging,
and on-request measurement. Scripts prepare, package or verify; they never
implement product behavior.

## Boundary

The scripts own `dev.sh` (builds and runs the app from source in one step, the counterpart of the
reference's `bun run desktop:dev`: it bootstraps the SDK when missing, and the build itself prepares
the native Ghostty and Mermaid libraries), `bootstrap.sh` (installs the `global.json` SDK into `.tools/dotnet`),
`build-merman.sh` (pinned native library under `.tools`),
`check-terminal.sh` (the native Ghostty shell/Metal probe), `publish.sh` (R2R
publish into `artifacts/` and the canonical signed `artifacts/SharpRail.app`), and
`check-packaged.sh` (runs the check gate from a staged copy of the packaged bundle; the published checks
add only what the bundle lacks, plus newer framework assemblies the gate itself vets) and
`benchmark.mjs` with its `window-probe.m` helper (launch timing, only when a
benchmark is requested).

Their public surface is the script invocations named in `AGENTS.md` and
`README.md`, plus the Mermaid MSBuild hook in `SharpRail.UI.csproj`.
Ghostty's native build belongs to `src/Ghostty.Avalonia/build-native.sh` and runs
through that independent library's project. It applies the checked-in texture
export and scrollback memory patches to the pinned Ghostty source; native sources and patches are included
in the build fingerprint. They may depend on POSIX `sh`, Git, curl, `shasum`, clang/Xcode
tools and, for the benchmark only, Node. They read source trees and write only to
`.tools`, `.bench` and `artifacts`. Product logic, a second source of package
versions, and editing sources are forbidden.

## Invariants

Native dependencies are pinned by exact revision or version and verified (Ghostty's
commit hash, Merman's archive SHA-256) before use; an unexpected revision or
checksum fails rather than builds. Downloads land in `.partial` locations and are
moved into place only when complete, so an interrupted run never leaves a
half-installed tool.

Native builds are idempotent and cheap on the no-op path: a fingerprint of the
architecture, pinned version and the inputs (including the script itself) skips
the work when nothing changed. Because MSBuild may invoke them from several
referencing projects at once, each takes a directory lock under `.tools` released
on exit or signal. They are no-ops off macOS, and the Ghostty build refuses a
runtime identifier for a different architecture than the host.

`publish.sh` removes the previous outputs before publishing so stale files never
survive into the package, publishes self-contained non-composite ReadyToRun UI,
remote host and checks, and keeps exactly one canonical app bundle. Replacing that
bundle while the app runs is the caller's responsibility to rule out first.

The benchmark refuses to run off AC power and measures process-tree memory and
readiness markers, not synthetic timers; it is never part of the check gate.

## Conformance gates

The dependency-boundary check and the public-surface check are repository gates rather than product
behavior, but they need the compiled assemblies and negative fixtures, so they live in the checks executable
(`-- --conformance`, also in the argument-free gate) and are specified in
[the checks spec](../tests/SharpRail.Checks/SPEC.md#conformance). No script duplicates them.
