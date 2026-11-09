#!/usr/bin/env dotnet
// Builds the Ghostty benchmarks: the macOS-only native probes (AppKit, Metal, ScreenCaptureKit) and the three benchmark programs.
// Run from anywhere: dotnet run benchmarks/ghostty/build.cs
#:include ../../scripts/lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    if (!OperatingSystem.IsMacOS()) throw new TaskFailure("The Ghostty benchmarks need macOS.");
    var bench = "benchmarks/ghostty";
    Check("clang", ["-O2", "-Wall", "-Wextra", $"{bench}/native/workload.c", "-o", $"{bench}/native/workload"]);
    Check("clang", ["-O2", "-dynamiclib", "-fobjc-arc", "-Wall", "-Wextra", "-Wno-unused-parameter", "-mmacosx-version-min=14.0",
        $"{bench}/native/Capture.m", "-framework", "AppKit", "-framework", "ScreenCaptureKit", "-framework", "CoreMedia", "-framework", "CoreVideo",
        "-o", $"{bench}/native/libBenchCapture.dylib"]);
    Check("clang", ["-O2", "-dynamiclib", "-fobjc-arc", "-mmacosx-version-min=14.0", $"{bench}/native/MetalTrace.m",
        "-framework", "Foundation", "-framework", "Metal", "-framework", "IOSurface", "-o", $"{bench}/native/libMetalTrace.dylib"]);
    Check("clang", ["-O2", "-dynamiclib", $"{bench}/native/IOTrace.c", "-framework", "IOKit", "-o", $"{bench}/native/libIOTrace.dylib"]);
    foreach (var project in new[] { "presentation/Bench", "memory/Memory", "attribution/Attribution" })
        Check(Dotnet, ["build", $"{bench}/{project}.csproj", "-c", "Release"]);
    return 0;
});
