#!/usr/bin/env dotnet
// `scripts/mise run check:fsevents`. Test-process-only native FSEvents accounting; never inject into a user application.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    if (!OperatingSystem.IsMacOS()) throw new TaskFailure("FSEvents checks require macOS.");
    if (!(args.Length == 0 || args is [""] || args is ["--no-build"])) throw new TaskFailure("Usage: scripts/mise run check:fsevents [-- --no-build]");
    if (Env("DYLD_INSERT_LIBRARIES") is not null) throw new TaskFailure("Run without an existing DYLD_INSERT_LIBRARIES injection.");
    if (args is not ["--no-build"]) Check(Dotnet, ["build", "Tereditor.slnx", "-c", "Release"]);
    var probeRoot = At(".bench", "fsevents-probe");
    Directory.CreateDirectory(probeRoot);
    var scratch = Directory.CreateTempSubdirectory(Path.Combine(probeRoot, "run.")).FullName;
    try
    {
        var probe = Path.Combine(scratch, "libFSEventsProbe.dylib");
        Check("clang", ["-dynamiclib", "-O2", "-Wall", "-Wextra", "-Werror", "tests/Tereditor.Checks/Native/FSEventsProbe.c", "-framework", "CoreServices", "-o", probe]);
        var work = Path.Combine(scratch, "work");
        Directory.CreateDirectory(work);
        return Run(Dotnet, [At("tests", "Tereditor.Checks", "bin", "Release", "net10.0", "Tereditor.Checks.dll"), "--native-fsevents"], work,
            new Dictionary<string, string> { ["DYLD_INSERT_LIBRARIES"] = probe, ["TEREDITOR_FSEVENTS_PROBE"] = probe });
    }
    finally
    {
        DeleteAll(scratch);
        try { Directory.Delete(probeRoot); } catch (IOException) { }
    }
});
