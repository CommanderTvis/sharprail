# Repository scripts

Run the examples from the repository root. Use `sh scripts/<name>.sh` so the
executable bit is not required. Shell scripts resolve the checkout from their own
location; the benchmark expects the repository root as its working directory.
Builds use the checkout-local `.tools/dotnet/dotnet`. Initial setup and native or
browser builds can download tools and dependencies into `.tools`.

## File icon assets

The UI kit build invokes `build-file-icons.sh` or `build-file-icons.ps1` to generate the pinned, checksummed Material Icon Theme SVGs and embed them locally. `.tools/dotnet/dotnet run scripts/generate-file-icons.cs -- --check` checks the committed mapping and generated assets; omit `--check` to regenerate them. Outputs stay under `.tools/file-icons`.

## Start the app or a server

### Desktop

```sh
sh scripts/dev.sh
```

Builds and runs the desktop app in Debug by default, bootstrapping the SDK if
needed. `SHARPRAIL_CONFIGURATION=Release` selects Release, `SHARPRAIL_ROOT` selects
the initial workspace (default: this checkout), and `SHARPRAIL_PROFILE` selects a
separate profile instead of `~/.sharprail`. Additional arguments go to the app.
The script clears inherited Claude Code session identity before launch.

### Browser

```sh
SHARPRAIL_TOKEN='your-session-token' \
SHARPRAIL_ROOT='/absolute/project/path' \
sh scripts/browser.sh run
```

Builds the browser client and remote host, then serves the client at
`http://localhost:54123`. Enter the same token in the browser. The server binds to
`127.0.0.1` by default; `SHARPRAIL_BIND` and `SHARPRAIL_PORT` change its address and
port. `SHARPRAIL_ROOT` defaults to this checkout and `SHARPRAIL_STATE_DIR` defaults
to `~/.sharprail/host`. Stop the foreground server with Ctrl+C.

`browser.sh` defaults to `build`; `SHARPRAIL_CONFIGURATION` defaults to Release.
Release uses selective hybrid WASM AOT with preserved managed assemblies and reflection;
Debug remains interpreted. Initial native compilation is slower and the runtime download is
larger before compression. Release Build/Publish precompress browser assets as Brotli and gzip;
the standalone host negotiates sidecars without request-time compression. See the
[browser README](../src/SharpRail.Browser/README.md) for measured sizes,
the interpreted diagnostic override and the optional prose performance journey.
It bootstraps the SDK and installs the `wasm-experimental` workload when the
multithreaded browser runtime pack is missing. Browser builds require Node/npm,
`uv`, Git and the supported macOS or Linux build hosts listed below.

| Command | Purpose and output |
| --- | --- |
| `sh scripts/browser.sh build` | Build into `src/SharpRail.Browser/bin/<configuration>/net10.0-browser/AppBundle`. |
| `sh scripts/browser.sh publish` | Publish the browser bundle into `artifacts/browser`. |
| `sh scripts/browser.sh check` | Build the desktop check harness (including the host) and browser test bundle, install Chromium, and run native selection, browser acceptance, local approval and terminal hover. |

### Remote host for desktop or Android clients

```sh
SHARPRAIL_ROOT='/absolute/project/path' sh scripts/host.sh
```

Builds and launches only the remote host in Release by default. It binds to
`0.0.0.0:54123` and prints a connection URI and QR code containing the session
token. The token is generated when `SHARPRAIL_TOKEN` is unset. Set
`SHARPRAIL_CONNECT_HOST` to override the advertised hostname/IP (without a port),
`SHARPRAIL_PORT` to change the port, or `SHARPRAIL_CONFIGURATION=Debug` for a diagnostic build.
The default advertised address is the first active non-loopback IPv4 address.
`SHARPRAIL_STATE_DIR` defaults to `~/.sharprail/host`. Stop with Ctrl+C.

## SDK and native dependencies

These are normally invoked by the build or launch scripts. macOS native builds
need clang and the Xcode command-line tools.

| Script | Invocation and behavior |
| --- | --- |
| `bootstrap.sh` | `sh scripts/bootstrap.sh` installs the pinned .NET SDK into `.tools/dotnet`, without changing PATH. |
| `build-merman.sh` | `sh scripts/build-merman.sh` downloads the pinned, checksummed Merman xcframework and links `.tools/merman/libSharpRailMermaid.dylib`. No-op outside macOS. |
| `build-notifications.sh` | `sh scripts/build-notifications.sh` compiles the macOS User Notifications bridge into `.tools/notifications/libSharpRailNotifications.dylib`. No-op outside macOS. |
| `android-ndk.sh` | `sh scripts/android-ndk.sh` installs pinned NDK r27d under `.tools/android-ndk` and prints its path. Supports macOS and Linux; archive checksum verification is macOS-only. |
| `browser-binaryen.sh` | `sh scripts/browser-binaryen.sh` installs pinned, checksummed Binaryen into `.tools/wasm-native/binaryen`. Supports macOS arm64/x86_64 and Linux aarch64/x86_64. |
| `wasm-native.sh` | `sh scripts/wasm-native.sh` builds Ghostty VT and Scintilla static WASM libraries into `.tools/wasm-native`. Requires checkout-local Emscripten packs from the WASM workload, `uv`, and the same build hosts as Binaryen; downloads pinned native sources and Zig. Invoked by the browser project. |
| `browser-assets.mjs` | `node scripts/browser-assets.mjs` stages browser renderer assets, fonts and licenses into `src/SharpRail.Browser/wwwroot/vendor`. Runs `npm ci --ignore-scripts` when dependencies are missing and downloads checksummed PDFium into `.tools/pdfium-web`. Invoked by the browser project. |

Ghostty and Scintilla's platform build scripts live in their respective
`src/Ghostty.Avalonia` and `src/SharpRail.Scintilla` directories, rather than here.

## Android

Set `ANDROID_HOME` to an installed Android SDK (default:
`~/Library/Android/sdk`) and `JAVA_HOME` to a JDK 17 or 21 managed by sdkman.
Do not use a Homebrew JDK. The script bootstraps .NET, installs its Android
workload when missing, and installs the pinned NDK.

| Command | Purpose and output |
| --- | --- |
| `sh scripts/android.sh build` | Build the client; this is the default mode. Configuration defaults to Debug and can be set with `SHARPRAIL_CONFIGURATION`. |
| `sh scripts/android.sh run` | Build, install on the connected device/emulator with adb, and launch `dev.thinkrail.sharprail`. |
| `sh scripts/android.sh apk` | Build Release and copy the signed package to `artifacts/android/SharpRail.apk`. |

Set `SHARPRAIL_ANDROID_KEYSTORE`, `SHARPRAIL_ANDROID_KEY_ALIAS` and
`SHARPRAIL_ANDROID_KEY_PASSWORD` to sign with your own keystore. The password is
used for both the store and key. Without these settings, the package uses the
machine's debug key.

## Packaging and checks

```sh
sh scripts/bootstrap.sh       # if the checkout-local SDK is missing
sh scripts/publish.sh         # defaults to osx-arm64
sh scripts/check-packaged.sh
sh scripts/dmg.sh
```

`publish.sh [runtime-identifier]` publishes self-contained Release composite ReadyToRun
UI/host outputs into `artifacts/ui` and `artifacts/host`, with non-composite R2R
checks in `artifacts/checks` for staging beside the shipped UI. For
`osx-arm64` or `osx-x64`, it also assembles and ad-hoc signs
`artifacts/SharpRail.app`. `DOTNET` overrides the dotnet executable; this script
does not bootstrap it. Publishing removes the previous outputs and app bundle,
so stop any app using that bundle before replacing it.

`check-packaged.sh [check arguments...]` requires the published app and checks.
It stages a copy under `.bench`, requires ReadyToRun output, forwards arguments
to the checks executable, and cleans the staging directory on exit. For example:

```sh
sh scripts/check-packaged.sh --terminals
```

`publish-windows.ps1 [-RuntimeIdentifier win-x64|win-arm64]` (PowerShell, on Windows) publishes the
self-contained client, host and checks to `artifacts/windows` (x64, default) or `artifacts/windows-arm64`
and packages `artifacts/SharpRail-<rid>.zip`. Build the ARM64 package on a Windows ARM64 machine: the
bootstrap SDK, Zig compiler and native cache (`obj/native/<rid>`) follow the host and runtime identifier.

`dmg.sh [output-path]` requires macOS and the existing app bundle. It creates a
compressed disk image with an Applications shortcut, defaulting to
`artifacts/SharpRail.dmg`; it replaces an existing image at the chosen path.

`sh scripts/check-terminal.sh` builds the native Ghostty bridge and a macOS
shell/Metal probe at `.bench/terminal-check`, then runs the probe. It requires
the macOS native build tools and a graphical session. It is separate from the
managed check suite.

For source checks, use the checkout-local SDK:

```sh
.tools/dotnet/dotnet run --project tests/SharpRail.Checks -c Release -- --terminals
```

The available focused modes and verification rules are documented in
[AGENTS.md](../AGENTS.md).

## Development helpers

| File | Invocation and purpose |
| --- | --- |
| `generate-rpc.py` | `uv run --no-project python scripts/generate-rpc.py` regenerates `src/SharpRail.Host.Protocol/RpcServices.Generated.cs` and `src/SharpRail.Host.Remote/RpcDispatch.Generated.cs` from `*Contract.cs`. It edits tracked source; review and format the generated diff. |
| `host-link.cs` | `.tools/dotnet/dotnet run --file scripts/host-link.cs` prints the connection URI and QR code using `SHARPRAIL_TOKEN`, `SHARPRAIL_PORT` and optional `SHARPRAIL_CONNECT_HOST`. Used by `host.sh`; restores QRCoder as a file-based app dependency. |
| `benchmark.mjs` | `node scripts/benchmark.mjs [embedded\|remote] [rounds] [workspace]` measures published app startup and process-tree RSS. Defaults: embedded, 10 rounds, current directory. Requires macOS, AC power, Node, clang and published artifacts; run only when measurement is requested. Writes `.bench/results-<mode>-<timestamp>.json` and compiles the helper below. |
| `window-probe.m` | Native CoreGraphics helper compiled by the benchmark as `.bench/window-probe`; reports when CoreGraphics first lists a normal window at least 300 × 200 for a process ID. Not a standalone build/launch entry point. |

Keep `.bench` scratch outputs disposable; record needed evidence in the status
documents and remove scratch files when the task finishes.

For repeatable macOS startup and physical-footprint measurements, use the [.NET performance toolkit](../performance/README.md). The older `benchmark.mjs` measures RSS with a different sampling anchor and should not be mixed into those reports.

### Native FSEvents hygiene (macOS)

`sh scripts/check-fsevents.sh` builds Release, then runs real Specs/Blueprint repeated reads, RPC
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

The explicit headless MCP host also exposes context_search, context_status and
context_index for its fixed workspace. Enablement and vendor terms must first be
handled by the actual user through Code Intelligence; agent tools cannot enable or
accept them. --context and --context-ui run synthetic provisioning/runtime/transport/
UI fixtures without a JetBrains account or cloud indexing. They include Node-based
command/MCP fixtures on macOS/Linux; no expensive real-provider corpus joins the gate.
A real authenticated Context smoke test remains a separate, user-authorized action.
