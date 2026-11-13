# What .NET 11 would give us

Researched and smoke-tested on 2026-11-13.

## Recommendation

Wait for the desktop and host, which already work, and do not bump the browser and Android heads yet. A bump is a mechanical change for the desktop NativeAOT projects: they restore, build and publish on the .NET 11.0.100 SDK with one new warning to fix. The gain is real but modest (NativeAOT compile about a quarter faster in the one measurement we could finish). The cost is that .NET 11 is STS (support ends 2028-11-14, the same date as .NET 10 LTS, so no support is lost by waiting or gained by moving), and the heads that matter for the product are not ready:

- Avalonia 12.1.6 (latest) still ships net10.0 assets only. A maintainer said net11 is planned for 12.2, together with CoreCLR on WebAssembly. The desktop assets load fine on net11 in our run, but it is unsupported ground.
- Browser: our Skia and HarfBuzz WebAssembly native assets (4.148.0, 14.2.1.301) predate the .NET 11 Emscripten/LLVM change (Avalonia issue 21700 reports `undefined symbol: saveSetjmp`; SkiaSharp 4.151+ is said to fix it). Threading needs Avalonia 12.1.5 or later (PR 22444, "Fix WASM MT on .NET 11 runtime") and `Microsoft.NET.WebAssembly.Threading` 11.0.0. WebAssembly stays on Mono in .NET 11.
- Android: .NET 11 replaces Mono with CoreCLR on mobile, drops x86 and API 23 and below (we use API 24 and arm64/x64, so those are fine), but Avalonia.Android is built for net10.0-android36.0 and nothing says it runs on CoreCLR. Not tried here.

Suggested order: bump Avalonia to 12.1.6, wait for 12.2 and for a SkiaSharp/HarfBuzzSharp pair that targets the .NET 11 toolchain, then move everything in one change. The desktop projects alone could move earlier, but the repo shares one `Directory.Build.props` TFM with the Android and Browser heads, so a split needs per-head TFMs.

## Status and support

- .NET 11.0.0 shipped 2026-11-10 (SDK 11.0.100, C# 15). Release index: https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json
- 11.0 is STS with end of support 2028-11-14 (24 months). 10.0 is LTS with the same end date. 9.0 and 8.0 are already end of life.
- What's new: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview, runtime https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime, SDK https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/sdk, C# 15 https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15, breaking changes https://learn.microsoft.com/en-us/dotnet/core/compatibility/11

## Measured

Same machine (Apple Silicon, 24 GB, 12 cores), same command, everything through `queue-heavy`. The NativeAOT publish is `scripts/tasks/native.cs`'s publish line (`-r osx-arm64 -p:IlcInstructionSet=native -p:OptimizationPreference=Speed`), second run after a warm-up run, with the ILC output directory deleted before the timed run. Numbers are single runs and the machine was shared with other agents' work, so treat differences under 10% as noise. `queue-heavy` turns the MSBuild server off, so .NET 11's server-by-default gain is not in these numbers.

NativeAOT publish of `src/Tereditor.Host.Headless` (osx-arm64):

| | .NET 10.0.403 | .NET 11.0.100 |
| --- | --- | --- |
| Whole publish, wall | 45.1 s | 34.0 s |
| ILC compile step | 39.2 s | 28.3 s |
| Peak RSS (whole publish) | 1.96 GB | 1.88 GB |
| Binary size | 31,461,264 B | 31,486,368 B (+0.08%) |

- The compile step is about 28% faster and the binary the same size.
- `-p:UseOptimizedPublish=true` (the SDK's skip-the-full-Build publish) made no visible difference on a built tree (34.6 s).
- NativeAOT publish of `src/Tereditor.UI` could not be measured on either SDK. On .NET 10 it was terminated with SIGTERM (ILC exit code 143) twice, at about 110 s with 4.4 GB resident, while `queue-heavy` reported no kill and no ceiling reached. The terminated process is ILC, so this is most likely tereditor's own terminal memory guard stopping the biggest process of the agent's terminal (it runs for any terminal, and measures physical footprint, which is larger than RSS). Run `scripts/mise run native --no-run` from your own terminal to get the UI number; the headless host is the same toolchain on a smaller graph. Not a .NET 11 issue.
- Build of `tests/Tereditor.Checks` (full, `--no-incremental`, `--no-restore`): 87 s on .NET 10, 97 s on .NET 11 (noise level; 63 s once when warm caches helped).

## Smoke result on .NET 11

The experiment (retargeting only) restored with `RestoreLockedMode=false`, built, and ran:

- `--state`: pass.
- `--case vector-icons`: pass on the second run (the first run failed in the unrelated `runner` case, see below).
- `--case runner`: fails identically on .NET 10 and .NET 11 in this agent terminal ("An interrupted runner must exit", `RunnerChecks.cs:333`). It is environmental here, not a bump regression.
- One new compile error under `TreatWarningsAsErrors`: SYSLIB0063 (the `NamedPipeClientStream(PipeDirection, bool, bool, SafePipeHandle)` constructor is obsolete, "isConnected has no effect") at `src/Tereditor.Plugins.Isolation/FdTransport.cs` lines 56 and 416. Fix: use the constructor without the `bool isConnected` argument. No IL/AOT analyzer warnings appeared, and no new CA warnings.
- The non-blittable `UnmanagedCallersOnly` rejection ("Native AOT rejects non-blittable UnmanagedCallersOnly signatures") did not trigger in the headless host. The desktop project (MacNotifier, GhosttyView, Vt, GhosttyExternalIo) was not published, so that check is still open for the UI.

Not tried: UI NativeAOT publish, Android, browser-wasm, Windows, Linux, arm64 Linux.

## What .NET 11 gives us

- NativeAOT: faster interface and generic virtual dispatch through a shared helper (smaller call sites), runtime-native async works under NativeAOT, and ILC was faster in our one headless measurement. Avalonia and our code are interface-heavy, so some startup and throughput gain is plausible but unmeasured.
- GC: the runtime notes list no change to `ConserveMemory`, regions or DATAS, so `DesktopGc.props` needs nothing. Nothing in the notes promises less memory.
- JIT and codegen: better bounds-check elimination, devirtualization of generic virtual methods, switch folding. This matters for the JIT'd heads and the plugin runner, less for AOT code.
- SDK: MSBuild server and the NativeAOT `dotnet` CLI entry point on by default (faster repeated builds outside `queue-heavy`), about 10% faster project evaluation, `dotnet format` is faster with `--include`, `#:include` and NativeAOT output reuse for file-based apps (our `scripts/tasks/*.cs` already use `#:include`), `dotnet test` additions.
- C# 15: collection expression arguments (`[with(capacity: n), ..xs]`), union types and closed hierarchies (exhaustive switches for our state and message types), extension indexers, labeled break and continue. Union scaffolding needs the net11 runtime; the memory-safety rules are preview and opt-in (needs `LangVersion=preview`). No required changes.
- Libraries: Zstandard in System.IO.Compression, `Process` run-and-capture and `StartSuspended`, JSON Lines and union support in System.Text.Json, new Stream types. Useful for the process-heavy plugins but nothing we are blocked on.
- Android: CoreCLR instead of Mono, in line with the mobile direction. Unverified with Avalonia.
- Minimum hardware: x86-64-v2 on Intel and no change on Apple Silicon (M1 baseline), so macOS arm64 is unaffected.

## Breaking changes that touch us

- NativeAOT output on Unix gets a `lib` prefix for native library outputs (only matters if we publish a NativeAOT shared library; we load our own dylibs and publish executables).
- Native AOT rejects non-blittable `UnmanagedCallersOnly` signatures (see above).
- Full `Build` is skipped for NativeAOT publish only when `UseOptimizedPublish=true`. The two Microsoft pages disagree: the compatibility page says it is the default, but the SDK 11.0.100 targets set `UseOptimizedPublish` to false for `PublishAot`, i.e. opt-in at GA. If it is ever turned on, `AfterBuild`-style targets (such as the copy targets in Tereditor.Checks, which are not AOT) would not run; ours hook `Build` only in the checks project.
- "NuGet locks directory moved on Linux" and "Restore doesn't search the global packages folder for a higher version" (restore behavior, relevant to CI on Linux).
- Android: x86 unsupported, minimum API 24 (we already use 24), Mono properties and embedding APIs no longer configure mobile apps, `AndroidEnableMarshalMethods` and any Mono-specific properties in `Tereditor.Android.csproj` need review.
- Obsoletions with non-default IDs, e.g. SYSLIB0063 above.
- `Environment.GetCommandLineArgs()[0]` keeps the apphost invocation name; named mutexes default to user scope on Unix; floating-point to small integer conversions saturate.

## What the repo change would be

1. `global.json` and the `http:dotnet` entry in `mise.toml` (version, six archive URLs and SHA-256 values) to 11.0.100, then `mise.lock`; `scripts/pin-dotnet-sdk.cs` prints the `ManagedRuntimes.cs` entries (`src/Tereditor.Host.Core/Language/ManagedRuntimes.cs`) for the language host.
2. TFM `net10.0` to `net11.0`: `Directory.Build.props`, `src/Ghostty.Avalonia/Ghostty.Avalonia.csproj`, `net11.0-android` in `Tereditor.Android.csproj`, `net11.0-browser` and `WasmAppDir` in `Tereditor.Browser.csproj`.
3. `Microsoft.NET.WebAssembly.Threading` 10.0.14 to 11.0.0, Avalonia to 12.1.6 or later, SkiaSharp and HarfBuzzSharp WebAssembly assets to a version built for the new Emscripten (SkiaSharp 4.151+; HarfBuzzSharp has only a 15.0 preview at the moment), `System.IO.Hashing` 10.0.12 to an 11.x build when it is wanted.
4. Hard-coded `net10.0` output paths in `scripts/tasks/dev.cs`, `host.cs`, `check-fsevents.cs`, `android.cs`, `browser.cs`, `benchmarks/ghostty/run.cs`, `docker/headless/Dockerfile`, and the CI workflows' SDK setup.
5. `scripts/mise run restore:update` for the 100+ `packages.lock.json` files (a bump rewrites all of them).
6. Fix SYSLIB0063 in `FdTransport.cs`.
7. Fixtures in tests that create `net10.0` projects (`LanguageChecks.cs`, `LanguageFreshnessChecks.cs`, `WorkspaceCreateStallChecks.cs`, language integration scripts) can stay on net10.0 if the language host still resolves a runtime, or move with the pin.

## Where the experiment lives

The retargeting is the uncommitted working-tree change of workspace-6 (TFMs, `global.json` 11.0.100, the WebAssembly threading package, rewritten lock files); it is not part of the doc commit. The .NET 11 SDK was unpacked into a scratch directory, outside any global install or mise config, so `scripts/mise` still resolves 10.0.403 and a plain `dotnet build` in the worktree needs `DOTNET_ROOT`, `DOTNET_ROOT_ARM64` and `PATH` pointing at an 11 SDK (the machine sets `DOTNET_ROOT_ARM64` to the 10.0.403 install, which wins over `DOTNET_ROOT`).
