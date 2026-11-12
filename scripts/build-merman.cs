#!/usr/bin/env dotnet
// Links Merman's static xcframework (pinned and checksummed in mise.toml; `scripts/tool merman` prints where it is) into the dylib the UI loads (macOS only).
// Usage: dotnet run --file scripts/build-merman.cs -- <output dylib>
#:include lib/Tasks.cs
using System.Runtime.InteropServices;
using static Tasks;

return Guard(() =>
{
    if (!OperatingSystem.IsMacOS()) return 0;
    if (args.Length != 1) throw new TaskFailure("usage: build-merman.cs <output dylib>");
    var output = Path.GetFullPath(args[0]);
    var library = Path.Combine(Tool("merman"), "macos-arm64_x86_64", "libmerman_ffi.a");
    if (!File.Exists(library)) throw new TaskFailure($"No Merman library at {library}.");
    var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    Check("clang", ["-dynamiclib", "-arch", arch, "-mmacosx-version-min=13.0", $"-Wl,-force_load,{library}", "-Wl,-exported_symbol,_merman_*",
        "-framework", "CoreFoundation", "-install_name", "@rpath/libTereditorMermaid.dylib", "-o", output + ".partial"], Root);
    File.Move(output + ".partial", output, true);
    return 0;
});
