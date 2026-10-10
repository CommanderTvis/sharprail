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
add only what the bundle lacks, plus newer framework assemblies the gate itself vets),
`android-ndk.sh` (installs the pinned Android NDK r27d into `.tools/android-ndk` and prints its path),
`android.sh` (builds the Android client in `src/SharpRail.Android`; `run` also installs and starts it on the
connected device or emulator, `apk` copies the signed Release package to `artifacts/android/SharpRail.apk`) and
`benchmark.mjs` with its `window-probe.m` helper (launch timing, only when a
benchmark is requested).

Their public surface is the script invocations named in `AGENTS.md` and
`README.md`, plus the Mermaid MSBuild hook in `SharpRail.UI.csproj`.
Ghostty's native build belongs to `src/Ghostty.Avalonia/build-native.sh` and runs
through that independent library's project. It applies the checked-in texture
export and scrollback memory patches to the pinned Ghostty source; native sources and patches are included
in the build fingerprint. The Android terminal and editor libraries likewise belong to their libraries
(`src/Ghostty.Avalonia/build-android.sh`, `src/SharpRail.Scintilla/build-android.sh`) and run from the Android
project's build; both take their compiler from `android-ndk.sh`. `android.sh` additionally needs the Android
SDK (`ANDROID_HOME`, default `~/Library/Android/sdk`) and a JDK 17 or 21 (`JAVA_HOME`), fails with a message
when either is missing, and runs `bootstrap.sh` and installs the `android` workload into `.tools/dotnet`
when those are absent. They may depend on POSIX `sh`, Git, curl, `shasum`, clang/Xcode
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
runtime identifier for a different architecture than the host. The Android builds run on macOS or Linux
and accept only `arm64-v8a` and `x86_64`.

`android-ndk.sh` takes an `install.lock` directory under `.tools/android-ndk`, unpacks into `partial` and
moves the NDK into place only when complete. Its SHA-256 pin is the macOS archive's and is checked on macOS
only; a Linux download is not verified.

`publish.sh` removes the previous outputs before publishing so stale files never
survive into the package, publishes self-contained non-composite ReadyToRun UI,
remote host and checks, and keeps exactly one canonical app bundle. Replacing that
bundle while the app runs is the caller's responsibility to rule out first.

`dmg.sh` packs that bundle, unchanged and with an Applications shortcut beside it, into a compressed disk
image (`artifacts/SharpRail.dmg` unless a path is given). The image is ad-hoc signed like the bundle and not
notarized. `.github/workflows/nightly.yml` builds it on a macOS runner and the Android package on a Linux
one every day at 03:00 UTC (and on demand), each uploaded as a workflow artifact named
`SharpRail-nightly-<date>-<commit>`. When both succeed it replaces the `nightly` prerelease: the tag moves to
the built commit and the files keep the names `SharpRail-nightly.dmg` and `SharpRail-nightly.apk`.

`android.sh` signs with the key named by `SHARPRAIL_ANDROID_KEYSTORE`, `SHARPRAIL_ANDROID_KEY_ALIAS` and
`SHARPRAIL_ANDROID_KEY_PASSWORD` (one password for the store and the key, passed to the build by name rather
than by value); without them the package carries the machine's debug key. The nightly uses one key, kept in
the repository secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`, so each
night's package installs over the last. Replacing that key breaks updating from every earlier package.

The benchmark refuses to run off AC power and measures process-tree memory and
readiness markers, not synthetic timers; it is never part of the check gate.

## Conformance gates

The dependency-boundary check and the public-surface check are repository gates rather than product
behavior, but they need the compiled assemblies and negative fixtures, so they live in the checks executable
(`-- --conformance`, also in the argument-free gate) and are specified in
[the checks spec](../tests/SharpRail.Checks/SPEC.md#conformance). No script duplicates them.
