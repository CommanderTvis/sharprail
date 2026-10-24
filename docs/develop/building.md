# Building

From a source checkout; [Machine setup](setup.md) lists what to install per platform and what the
repo provisions itself. macOS builds need the Xcode command-line tools, LLVM at
`/opt/homebrew/opt/llvm`, and its Metal Toolchain
(`xcodebuild -downloadComponent MetalToolchain`). The first build downloads pinned
Ghostty 1.2.3, Zig 0.14.1 and Merman into machine-wide caches shared by every checkout. No installed
Ghostty application is required.

```sh
scripts/mise x -- dotnet run --project src/SharpRail.UI -c Release
```

To build the ReadyToRun macOS app bundle:

```sh
scripts/mise run package:macos
open artifacts/SharpRail.app
```
