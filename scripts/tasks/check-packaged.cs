#!/usr/bin/env dotnet
// `scripts/mise run check:packaged [-- args]`: runs the check gate against the packaged app. The checks are staged beside a copy of the
// bundle's own files, so the native libraries under test are the ones that ship, and the gate runs the shipped NativeAOT binaries
// (`--packaged` runs only that) and requires each payload to carry what the checks' own publish has for it. On Linux the bundle is the AppImage's payload, extracted from the image itself (`--linux` is the gate
// to pass). On Windows the gate runs from the published package of `-RuntimeIdentifier win-x64|win-arm64` (win-x64 by default); the other arguments go to the checks.
#:include ../lib/Tasks.cs
using System.Runtime.InteropServices;
using static Tasks;

return Guard(() =>
{
    if (OperatingSystem.IsWindows()) return Windows();
    var stage = Path.Combine(".bench", $"packaged-{Environment.ProcessId}");
    try { return OperatingSystem.IsLinux() ? Linux(stage) : Mac(stage); }
    finally { DeleteAll(stage); }

    int Windows()
    {
        var rid = "win-x64";
        var rest = args;
        if (args.Length > 0 && args[0].Equals("-RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))
        {
            rid = args.Length > 1 ? args[1] : "";
            rest = args[Math.Min(2, args.Length)..];
        }
        if (rid is not ("win-x64" or "win-arm64")) throw new TaskFailure($"Unknown RuntimeIdentifier '{rid}'; use win-x64 or win-arm64.");
        var directory = rid == "win-x64" ? "windows" : "windows-arm64";
        var checks = At("artifacts", directory, "checks", "Tereditor.Checks.exe");
        if (!File.Exists(checks)) throw new TaskFailure($"No packaged app to check: run scripts\\mise run package:windows {rid} first.");
        return Run(checks, rest, env: new Dictionary<string, string>
        {
            ["TEREDITOR_PACKAGED_CLIENT"] = At("artifacts", directory, "client"),
            ["TEREDITOR_PACKAGED_HOST"] = At("artifacts", directory, "host"),
            ["TEREDITOR_PACKAGED_CHECKS"] = At("artifacts", directory, "checks"),
        });
    }

    int Linux(string stage)
    {
        // The check runs the image, so it is the one built for this host's architecture.
        var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        var image = $"artifacts/tereditor-{rid}.AppImage";
        var checks = "artifacts/linux/checks";
        if (!File.Exists(image) || !File.Exists($"{checks}/Tereditor.Checks")) throw new TaskFailure($"No packaged app to check: run scripts/mise run package:linux {rid} first.");
        Directory.CreateDirectory(Path.Combine(stage, "extract"));
        // The runtime unpacks the image without FUSE; the payload then stands once as shipped and once with the checks beside it.
        if (Capture(At(image), ["--appimage-extract"], Path.Combine(stage, "extract")).Code != 0) throw new TaskFailure($"{image} --appimage-extract failed");
        var shipped = Path.Combine(stage, "extract", "squashfs-root", "usr", "lib", "tereditor");
        var run = Path.Combine(stage, "run");
        Copy(shipped, run);
        Check($"{checks}/Tereditor.Checks", ["--stage-packaged", checks, run]);
        return Run(Path.Combine(run, "Tereditor.Checks"), args, env: new Dictionary<string, string>
        {
            ["TEREDITOR_PACKAGED_CLIENT"] = At(shipped),
            ["TEREDITOR_PACKAGED_CHECKS"] = At(checks),
            ["TEREDITOR_PACKAGED_APP"] = At(shipped),
        });
    }

    int Mac(string stage)
    {
        var bundle = "artifacts/tereditor.app";
        if (!Directory.Exists($"{bundle}/Contents/MacOS") || !File.Exists("artifacts/checks/Tereditor.Checks")) throw new TaskFailure("No packaged app to check: run scripts/mise run package:macos first.");
        Directory.CreateDirectory(stage);
        CopyInto(bundle, stage);
        Environment.SetEnvironmentVariable("TEREDITOR_PACKAGED_CLIENT", At(bundle, "Contents", "MacOS"));
        Environment.SetEnvironmentVariable("TEREDITOR_PACKAGED_CHECKS", At("artifacts", "checks"));
        if (Directory.Exists("artifacts/host")) Environment.SetEnvironmentVariable("TEREDITOR_PACKAGED_HOST", At("artifacts", "host"));
        var staged = Path.Combine(stage, "tereditor.app", "Contents", "MacOS");
        Check("artifacts/checks/Tereditor.Checks", ["--stage-packaged", "artifacts/checks", staged]);
        return Run(Path.Combine(staged, "Tereditor.Checks"), args, env: new Dictionary<string, string> { ["TEREDITOR_PACKAGED_APP"] = At(bundle) });
    }
});
