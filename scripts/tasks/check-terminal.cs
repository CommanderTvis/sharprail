#!/usr/bin/env dotnet
// `scripts/mise run check:terminal`: builds the Ghostty libraries and runs the native shell/Metal probe.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    var cache = At("src", "Ghostty.Avalonia", "obj", "native");
    Check("sh", ["src/Ghostty.Avalonia/build-native.sh", "", cache]);
    Directory.CreateDirectory(".bench");
    Check("clang", ["-fobjc-arc", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
        "src/Ghostty.Avalonia/Native/terminal-check.m", "-L", cache, "-lGhosttyAvaloniaView",
        "-framework", "AppKit", $"-Wl,-rpath,{cache}", "-o", ".bench/terminal-check"]);
    return Run(".bench/terminal-check", [Root], env: new Dictionary<string, string> { ["GHOSTTY_RESOURCES_DIR"] = Path.Combine(cache, "resources", "ghostty") });
});
