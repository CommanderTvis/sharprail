# Building

From a source checkout; [Machine setup](setup.md) lists what to install per platform and what the
repo provisions itself. macOS builds need the Xcode command-line tools, LLVM at
`/opt/homebrew/opt/llvm`, and its Metal Toolchain
(`xcodebuild -downloadComponent MetalToolchain`). The first build downloads pinned
Ghostty (libghostty-vt 1.0.0), Zig 0.17.1 and Merman into machine-wide caches shared by every checkout. No installed
Ghostty application is required.

```sh
scripts/mise x -- dotnet run --project src/SharpRail.UI -c Release
```

To build the macOS app bundle (the app as a NativeAOT binary):

```sh
scripts/mise run package:macos
open artifacts/SharpRail.app
```

## Run the optimised local build

```sh
scripts/mise run native
```

Publishes the desktop app as NativeAOT with `IlcInstructionSet=native` and `OptimizationPreference=Speed` into `artifacts/native` and runs it (`--no-run` publishes only, `--print` shows the commands). The binary is tied to this machine's CPU and may not start on another one. The publish is slow compared with `mise run dev` and uses several GB of memory, and it does not go through `queue-heavy`; use it only for profiling or for daily use of an optimised build. Agents never run it.

## Memory

A build target that runs a file-based script from a project directory must use `dotnet run --file <script>`. `dotnet run <script>` in a directory with a `.csproj` passes the script to the project as an argument, so it builds and runs that project, whose build runs the same target again: a chain of nested `dotnet` processes that grows until memory is gone. The Browser project's asset staging, bundle compression and preserved-assembly check use `--file` for this reason.

Expected size of a Browser build: the compile and the non-AOT bundle stay near 2 GB; the Release AOT link (wasm-ld, then wasm-opt) is the largest step and is the one place a Browser build goes above 6 GB. Run every heavy command through `queue-heavy`; if one is killed at its ceiling, look for a recursive process chain before raising the ceiling.
