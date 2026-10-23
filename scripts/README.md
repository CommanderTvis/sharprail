# Repository scripts

Run the examples from the repository root. Everything is a mise task: `scripts/mise run <task>`
(`scripts\mise.cmd` on Windows); the files in `scripts/tasks` are what the tasks run and are not entry
points. Builds use the pinned .NET SDK, which mise puts on PATH. The first task installs the pinned toolchain
(see Toolchain below).

## File icon assets

The UI kit's `BuildFileIcons` target (a skipped build when the generator and table are unchanged) runs `build-file-icons.sh` or `build-file-icons.ps1` to generate the pinned, checksummed Material Icon Theme SVGs and embed them locally. `dotnet run scripts/generate-file-icons.cs -- --check` checks the committed mapping and generated assets; omit `--check` to regenerate them. The generated SVGs go to the kit's `obj/file-icons`; the pinned archive is cached in `$SHARPRAIL_CACHE_DIR/file-icons`.

## Start the app or a server

### Desktop

```sh
scripts/mise run dev
```

Builds and runs the desktop app in Release by default, installing the SDK if
needed. `SHARPRAIL_CONFIGURATION=Debug` selects a Debug build for debugging, `SHARPRAIL_ROOT` selects
the initial workspace (default: this checkout), and `SHARPRAIL_PROFILE` selects a
separate profile instead of `~/.sharprail`. Additional arguments go to the app.
The script clears inherited Claude Code session identity before launch.
The Release build uses the memory settings in `DesktopGc.props` and the idle memory trim (`performance/README.md`).

### Browser

```sh
SHARPRAIL_TOKEN='your-session-token' \
SHARPRAIL_ROOT='/absolute/project/path' \
scripts/mise run browser run
```

Builds the browser client and remote host, then serves the client at
`http://localhost:54123`. Enter the same token in the browser. The server binds to
`127.0.0.1` by default; `SHARPRAIL_BIND` and `SHARPRAIL_PORT` change its address and
port. `SHARPRAIL_ROOT` defaults to this checkout and `SHARPRAIL_STATE_DIR` defaults
to `~/.sharprail/host`. Stop the foreground server with Ctrl+C.

The `browser` task defaults to `build`; `SHARPRAIL_CONFIGURATION` defaults to Release.
Release uses selective hybrid WASM AOT with preserved managed assemblies and reflection;
Debug remains interpreted. Initial native compilation is slower and the runtime download is
larger before compression. Release Build/Publish precompress browser assets as Brotli and gzip;
the standalone host negotiates sidecars without request-time compression. See the
[browser README](../src/SharpRail.Browser/README.md) for measured sizes,
the interpreted diagnostic override and the optional prose performance journey.
It installs the `wasm-experimental` workload when the
multithreaded browser runtime pack is missing. Browser builds install Node, pnpm, uv and
Binaryen through mise (`scripts/tool`), and need Git and a macOS or Linux build host.

| Command | Purpose and output |
| --- | --- |
| `scripts/mise run browser build` | Build into `src/SharpRail.Browser/bin/<configuration>/net10.0-browser/AppBundle`. |
| `scripts/mise run browser publish` | Publish the browser bundle into `artifacts/browser`. |
| `scripts/mise run browser check` | Build the desktop check harness (including the host) and browser test bundle, install Chromium, and run native selection, browser acceptance, local approval and terminal hover. |

### Remote host for desktop or Android clients

```sh
SHARPRAIL_ROOT='/absolute/project/path' scripts/mise run host
```

Builds and launches only the remote host in Release by default. It binds to
`0.0.0.0:54123` and prints a connection URI and QR code containing the session
token. The token is generated when `SHARPRAIL_TOKEN` is unset. Set
`SHARPRAIL_CONNECT_HOST` to override the advertised hostname/IP (without a port),
`SHARPRAIL_PORT` to change the port, or `SHARPRAIL_CONFIGURATION=Debug` for a diagnostic build.
The default advertised address is the first active non-loopback IPv4 address.
`SHARPRAIL_STATE_DIR` defaults to `~/.sharprail/host`. Stop with Ctrl+C.

## Toolchain: mise

Every command goes through `scripts/mise` (`scripts\mise.cmd` on Windows), a checksum-pinned release of
[mise](https://mise.jdx.dev) that downloads itself on first use and ignores your own mise configuration.
`scripts/mise tasks` lists the tasks, `scripts/mise run targets` prints the artifact matrix (which
native libraries, terminal renderer, publish task and package each target has).

`mise.toml` is the manifest: the .NET SDK (`global.json` must name the same version), both Zig toolchains,
Merman, the Temurin JDK 21 and the pinned Ghostty commits; `mise.android.toml` pins the
NDK and Merman's Android archive, `mise.browser.toml` Node, pnpm, uv and Binaryen. `mise.lock`, `mise.android.lock` and
`mise.browser.lock` record the resolved version and SHA-256 of every archive for every platform and are
committed (`scripts/mise lock` refreshes them; CI sets `MISE_LOCKED=1`, which refuses anything not in them).
The .NET SDK is an `http:dotnet` tool for the same reason: mise's core dotnet tool records no URL or checksum, so
`mise.toml` names Microsoft's per-platform archives (from the 10.0 `releases.json`, whose SHA-512 the pinned SHA-256s
were checked against) and `global.json` pins the same version with `rollForward` disabled.

With a mise installed on the machine, `scripts/mise` runs it as is; without one it downloads the pinned mise release. Either
way tools install into mise's data directory (default `~/.local/share/mise`), one directory per version, which every
checkout and worktree of the user shares, so checkouts at the same pin share one copy and a version bump leaves older
checkouts valid. mise installs into a temporary directory and renames it, under its own lock, so concurrent installs are
safe. There is no bootstrap step and no link in the checkout: a task installs the tools it needs on first use, tasks and
`scripts/mise x -- <command>` get them on PATH, and `scripts/tool <name>` (`scripts\tool.cmd` on Windows) prints a tool's
directory (installing it first when missing): `dotnet`, `zig-0.14.1`, `zig-0.16.0`, `merman`, `jdk`, `appimagetool`,
`appimage-runtime`, and from the android and browser mise files `android-ndk`, `merman-android`, `node`, `pnpm`, `uv`,
`binaryen`, `pdfium-wasm`. The SDK directory stays writable because `dotnet workload install` adds packs to it; those
installs are serialised with a lock (`scripts/tasks/workload.sh`).

Caches written during builds live in one machine-wide directory every checkout and worktree shares, never in the
checkout (there is no `.tools`): `SHARPRAIL_CACHE_DIR` from `mise.toml`, `${XDG_CACHE_HOME:-~/.cache}/sharprail`
(Windows: `%LOCALAPPDATA%\sharprail\cache`); scripts run outside mise fall back to the same path. The scripts that create the root exclude it from Time Machine on macOS
(`tmutil addexclusion`). Entries:

| Entry | Contents | Key and concurrency |
| --- | --- | --- |
| `zig` (`ZIG_GLOBAL_CACHE_DIR`) | Zig's global package cache | content-addressed; Zig locks per entry |
| `ghostty-avalonia` | pinned Ghostty sources, Zig caches, the patched macOS SDK copy; `results/<platform>-<library>-<key>` | key: hash of the build's fingerprint (pinned commits, Zig, sources, script); one `build.lock` |
| `wasm-native` | patched libghostty-vt source, Zig cache; `results/<key>` | key: commit, patch, Zig, Emscripten SDK, sources; `build.lock` |
| `tailscale-go` | `go<version>-<platform>`, Go's build and module caches | per pinned version, installed by rename under `install.lock`; Go locks its caches |
| `headscale-test` | what `--headscale` runs | version-stamped download, files renamed into place under `headscale-test.lock` |
| `browser-compression` | Brotli/gzip of browser assets | content hash; files written beside and renamed |
| `playwright` (`PLAYWRIGHT_BROWSERS_PATH`) | Chromium for the browser checks | Playwright's own install lock |
| `file-icons` | the pinned Material Icon Theme archive | version and checksum; renamed into place |
| `lsp-validation`, `lsp-shared-cache`, `language-check` | opt-in language integration runs | repositories and managed servers |

A native result is built in `<result>.partial` and published by rename (`scripts/native-cache.sh`); the recipe then
copies it into the checkout's `obj` (Ghostty: `src/Ghostty.Avalonia/obj/native`, WASM: `src/SharpRail.Browser/obj/wasm-native`,
Tailscale: `src/SharpRail.Android/obj/tailscale`, icons: `src/SharpRail.Plugins.UI.Kit/obj/file-icons`), where MSBuild's
incremental targets see it. Results are keyed, so branches never overwrite each other's and a new worktree reuses one
instead of rebuilding; old results are not pruned (delete the directory to reclaim space). NuGet's cache is NuGet's own.

Native libraries are MSBuild targets in the project that owns them, with their sources, recipe and
lockfile as inputs: `scripts/mise run build` (or any `dotnet build`) rebuilds a library only when one
changed. The recipes (`build-merman.sh`, `build-notifications.sh`, `wasm-native.sh`, and the
`build-native`, `build-android`, `build-linux` and `build-windows` scripts beside Ghostty) are run by those targets under
`scripts/mise x --`, which supplies the pins in `[env]`; they are not entry points. They serialise on a directory lock taken through `scripts/lock.sh`, which records the owner's PID: a lock whose owner is gone (a killed build, a machine that slept) is removed instead of waited for, and a wait names the holder. macOS native builds
need clang, the Xcode command-line tools and, for libghostty, the Metal toolchain. The Linux libraries need only the pinned Zig, which
cross-compiles them for `x86_64-linux-gnu` or `aarch64-linux-gnu` against glibc 2.31 from macOS or Linux alike.

### NuGet packages

Packages are locked: every project commits a `packages.lock.json` (direct and transitive versions with content
hashes), `nuget.org` is the only feed (`nuget.config`, with `<clear />` and a source mapping), and mise tasks
restore in locked mode (`RestoreLockedMode` in `mise.toml` `[env]`), so drift fails with NU1004 and a changed package
with NU1403. `scripts/mise run restore:update` is the one task that rewrites the locks: run it after changing a
`PackageReference` or a version in `Directory.Packages.props` and commit the locks with the change. Plain `dotnet`
outside mise is not locked and updates the files itself. The locks list `osx-arm64;osx-x64;win-x64;win-arm64;linux-x64;linux-arm64`
(`RuntimeIdentifiers` in `Directory.Build.props`); because a `-r` restore changes the project's RID list, the package
tasks restore once without `-r` (with `SelfContained` and `PublishReadyToRun`, which also fetches runtime and
Crossgen packs) and publish with `--no-restore`. Do the same for any new `-r` publish.

Excluded from locking (`RestorePackagesWithLockFile=false`, reason in the project file): `SharpRail.Android` and
`SharpRail.Browser`, whose restores resolve workload packs and per-RID runtime packs from the installed workload
manifest. `benchmarks/` and any project outside `SharpRail.slnx` are locked too if restored; do not commit stray locks
you did not mean to.

Audit: `NuGetAudit` covers all packages, transitive included, at moderate severity and above. Advisories (NU1901 to
NU1904) are warnings in builds, so a new advisory cannot break an unrelated build such as the nightly; the CI `build`
job runs `scripts/mise run audit`, which restores with them as errors. To resolve one, bump the package and run
`restore:update`.

`zig-deps.sh` (and `Restore-ZigDeps` in `windows-native.ps1`) restores the Zig packages that Ghostty fetches from hosts other than
github.com and deps.files.ghostty.org (the three Codeberg ones: zg, zig-wayland, translate-c) into Zig's global cache before every native Ghostty build, from a mirror archive
pinned by SHA-256 in `zig-deps.lock`. The archive is a release asset of this repository (tag `zig-deps-<hash of the package list>`).
Only absent packages are restored, so a warm cache needs no network; a missing or corrupt mirror falls back to the upstream URLs
and fails with the package and URL named if one is unreachable. After changing a Ghostty pin run `scripts/mise run zig-deps:mirror`
on macOS (measures the packages from a real build into `.bench/zig-deps`), then publish the asset it prints a command for
and commit the lock. the `--zig-deps` check mode fails when the lock is stale.

`fetch-git.sh` checks out a source pinned by commit; archives are pinned by checksum in `mise.toml`.
`browser-assets.mjs` (the browser project's `StageBrowserAssets` target, rerun when `pnpm-lock.yaml`, the PDFium pin or a licence changes) stages the browser renderer assets, fonts and licenses into
`src/SharpRail.Browser/wwwroot/vendor`.

## Android

Set `ANDROID_HOME` to an installed Android SDK (default:
`~/Library/Android/sdk`). The JDK is provisioned like the .NET SDK: mise installs the pinned Temurin 21
from `mise.toml` on first use and `scripts/tool jdk` prints it (on macOS, at the bundle's `Contents/Home`; Windows has no pin, see `mise.toml`); `scripts/tool merman-android`
prints Merman's Android archive, whose per-ABI `.so` the APK packages as `libSharpRailMermaid.so`. A
`JAVA_HOME` in the environment, when set, names another JDK 17 or 21 instead. The task installs the .NET
Android workload when missing and the pinned NDK.

| Command | Purpose and output |
| --- | --- |
| `scripts/mise run android build` | Build the client; this is the default mode. Configuration defaults to Debug and can be set with `SHARPRAIL_CONFIGURATION`. |
| `scripts/mise run android run` | Build, install on the connected device/emulator with adb, and launch `dev.thinkrail.sharprail`. |
| `scripts/mise run package:android` | Build Release and copy the signed package to `artifacts/android/SharpRail.apk`. |

Set `SHARPRAIL_ANDROID_KEYSTORE`, `SHARPRAIL_ANDROID_KEY_ALIAS` and
`SHARPRAIL_ANDROID_KEY_PASSWORD` to sign with your own keystore. The password is
used for both the store and key. Without these settings, the package uses the
machine's debug key.

## Packaging and checks

```sh
scripts/mise run package:macos        # defaults to osx-arm64; osx-x64 as an argument
scripts/mise run check:packaged
scripts/mise run dmg
```

`package:macos` publishes self-contained Release composite ReadyToRun
UI/host outputs into `artifacts/ui` and `artifacts/host`, with non-composite R2R
checks in `artifacts/checks` for staging beside the shipped UI. It also assembles and
ad-hoc signs `artifacts/SharpRail.app`. Publishing removes the previous outputs and app bundle,
so stop any app using that bundle before replacing it.

`check:packaged [-- check arguments...]` requires the published app and checks.
It stages a copy under `.bench`, requires ReadyToRun output, forwards arguments
to the checks executable, and cleans the staging directory on exit. For example:

```sh
scripts/mise run check:packaged -- --terminals
```

`scripts\mise.cmd run package:windows -- -RuntimeIdentifier win-x64|win-arm64` (on Windows) publishes the
self-contained client, host and checks to `artifacts/windows` (x64, default) or `artifacts/windows-arm64`
and packages `artifacts/SharpRail-<rid>.zip`. Build the ARM64 package on a Windows ARM64 machine: the
SDK, Zig compiler and native cache (`obj/native/<rid>`) follow the host and runtime identifier.
`scripts\mise.cmd run check:packaged -- -RuntimeIdentifier <rid> --windows` runs the packaged Windows gate.

`scripts/mise run package:linux [linux-arm64]` (on Linux of the same architecture; x64 by default) builds, publishes the
self-contained ReadyToRun client and the checks to `artifacts/linux` and packs the client into
`artifacts/SharpRail-linux-x64.AppImage` (`SharpRail-linux-arm64.AppImage` for `linux-arm64`, with the aarch64 appimagetool and runtime): an `AppDir` (`AppRun`, a desktop file, the
icon, the client under `usr/lib/sharprail`) written as a squashfs image behind the pinned type 2 runtime by the pinned appimagetool
(`scripts/tool` installs both from checksummed releases). Neither FUSE nor a display is needed to build it. On another host
`SHARPRAIL_APPDIR_ONLY=1 scripts/mise run package:linux [linux-arm64]` publishes the Linux client (the natives cross-compile with Zig) and stops at
`artifacts/linux/AppDir`. `scripts/mise run check:packaged -- --linux` unpacks the image with the runtime's own
`--appimage-extract` (the image for the host's architecture), stages the checks beside a copy of that payload and runs the Linux gate. See [LINUX.md](../LINUX.md).

`dmg [output-path]` requires macOS and the existing app bundle. It creates a
compressed disk image with an Applications shortcut, defaulting to
`artifacts/SharpRail.dmg`; it replaces an existing image at the chosen path.

`check:terminal` builds the native Ghostty bridge and a macOS
shell/Metal probe at `.bench/terminal-check`, then runs the probe. It requires
the macOS native build tools and a graphical session. It is separate from the
managed check suite.

For source checks, use the checkout-local SDK:

```sh
dotnet run --project tests/SharpRail.Checks -c Release -- --terminals
```

The available focused modes and verification rules are documented in
[AGENTS.md](../AGENTS.md).

## Development helpers

| File | Invocation and purpose |
| --- | --- |
| `generate-rpc.py` | `scripts/mise run rpc` regenerates `src/SharpRail.Host.Protocol/RpcServices.Generated.cs` and `src/SharpRail.Host.Remote/RpcDispatch.Generated.cs` from `*Contract.cs`. It edits tracked source; review and format the generated diff. |
| `host-link.cs` | `dotnet run --file scripts/host-link.cs` prints the connection URI and QR code using `SHARPRAIL_TOKEN`, `SHARPRAIL_PORT` and optional `SHARPRAIL_CONNECT_HOST`. Used by the `host` task; restores QRCoder as a file-based app dependency, locked in its own `host-link.packages.lock.json` (refreshed by `restore:update`) because `scripts/packages.lock.json` is shared by the other file-based scripts. |
| `benchmark.mjs` | `node scripts/benchmark.mjs [embedded\|remote] [rounds] [workspace]` measures published app startup and process-tree RSS. Defaults: embedded, 10 rounds, current directory. Requires macOS, AC power, Node, clang and published artifacts; run only when measurement is requested. Writes `.bench/results-<mode>-<timestamp>.json` and compiles the helper below. |
| `window-probe.m` | Native CoreGraphics helper compiled by the benchmark as `.bench/window-probe`; reports when CoreGraphics first lists a normal window at least 300 × 200 for a process ID. Not a standalone build/launch entry point. |

Keep `.bench` scratch outputs disposable; record needed evidence in the status
documents and remove scratch files when the task finishes.

For repeatable macOS startup and physical-footprint measurements, use the [.NET performance toolkit](../performance/README.md). The older `benchmark.mjs` measures RSS with a different sampling anchor and should not be mixed into those reports.

### Native FSEvents hygiene (macOS)

`scripts/mise run check:fsevents` builds Release, then runs real Specs/Blueprint repeated reads, RPC
reconnects and real Git-host teardown (including metadata/refs watchers) under a test-only FSEventStream interposer. `--no-build` reuses built checks.
It proves interception first and fails if stream allocations grow or do not return to baseline.
Generated native code and fixtures are scoped to the subprocess scratch tree and cleaned on exit;
no injection is applied to a user app. See Host Core's `Files.SPEC.md` for registry/EventPipe diagnostics
and the distinction between logical leases, managed watcher instances and native stream counts.

### Headless language MCP and real-project validation

Set `SHARPRAIL_HEADLESS_MCP=1` with the standalone host's existing root, state,
bind/port and token environment variables to enable bearer-authenticated POST
`/mcp` without a terminal or graphical app. The production language tools are
available; license acceptance is not. See
[`tests/SharpRail.Language.Integration/README.md`](../tests/SharpRail.Language.Integration/README.md)
for direct launch instructions and the explicitly opt-in, pinned-project tool-call
matrix. Expensive real-server scenarios are excluded from the full check suite.

The explicit headless MCP host also exposes the tools of the enabled plugins for its fixed workspace,
including Air Context's context_search, context_status and context_index. Enablement and vendor terms must first be
handled by the actual user in the Air Context pane; agent tools cannot enable or
accept them. --context and --context-ui run synthetic provisioning/runtime/transport/
UI fixtures without a JetBrains account or cloud indexing. They include Node-based
command/MCP fixtures on macOS/Linux; no expensive real-provider corpus joins the gate.
A real authenticated Context smoke test remains a separate, user-authorized action.

### Android embedded Tailscale

`sh scripts/build-tailscale-android.sh [arm64-v8a|x86_64]` builds the app-private
userspace node using checksum-pinned Go and the checkout's Android NDK. It stages
`libSharpRailTailscale.so` and dependency licenses under `src/SharpRail.Android/obj/tailscale/<abi>`.
The Android build invokes it for both packaged ABIs. The NDK is the one `scripts/tool android-ndk` prints. See the [bridge README](../src/SharpRail.Android/Tailscale.Native/README.md)
for its ABI and native tests; no system Go installation or separate Android VPN app is required.

### Headscale connectivity check

`sh scripts/build-headscale-test.sh` provisions what `--headscale` needs under `$SHARPRAIL_CACHE_DIR/headscale-test`:
the checksum-pinned Headscale v0.29.5 release for this platform (macOS or Linux, arm64 or x64), the
`tsforward` userspace node and a desktop build of the embedded Tailscale library. The Go toolchain comes
from `scripts/tailscale-go.sh`, which the Android build shares. Then
`dotnet run --project tests/SharpRail.Checks -c Release -- --headscale` runs the slow check.
