#!/usr/bin/env dotnet
// `scripts\mise.cmd run package:windows [-RuntimeIdentifier] <win-x64|win-arm64>`: publishes the Windows client, host and checks and zips them
// into artifacts/tereditor-<rid>.zip. win-x64 keeps its original locations (artifacts/windows); win-arm64 publishes beside them (artifacts/windows-arm64).
#:include ../lib/Tasks.cs
using System.Diagnostics;
using System.IO.Compression;
using static Tasks;

return Guard(() =>
{
    if (!OperatingSystem.IsWindows()) throw new TaskFailure("Windows packages are built on Windows.");
    var requested = args.Length > 0 && args[0].Equals("-RuntimeIdentifier", StringComparison.OrdinalIgnoreCase) ? args.Skip(1) : args;
    var rid = requested.FirstOrDefault() is { Length: > 0 } first ? first : "win-x64";
    if (rid is not ("win-x64" or "win-arm64")) throw new TaskFailure($"Unknown RuntimeIdentifier '{rid}'; use win-x64 or win-arm64.");
    var directory = rid == "win-x64" ? "windows" : "windows-arm64";
    var output = At("artifacts", directory);
    var running = new[] { "Tereditor.UI", "Tereditor.Host.Remote", "Tereditor.Checks" }.SelectMany(Process.GetProcessesByName).Any(process =>
    {
        try { return process.MainModule?.FileName?.StartsWith(output + "\\", StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    });
    if (running) throw new TaskFailure("Close the running Windows package before replacing it.");
    DeleteAll(output);
    Check(Dotnet, ["build", "Tereditor.slnx", "-c", "Release"]);
    // One locked restore (packages.lock.json lists every shipped RID) that also fetches the runtime, ILCompiler and Crossgen
    // packs; the publishes reuse it, since a `-r` restore would not match the lock's RuntimeIdentifiers.
    Check(Dotnet, ["restore", "Tereditor.slnx", "-p:SelfContained=true", "-p:PublishReadyToRun=true"]);
    string[] publish = ["-c", "Release", "-r", rid, "--no-restore", $"-p:TereditorBuildStamp={BuildStamp()}"];
    // The client and host are NativeAOT, linked by Visual Studio's C++ linker; the checks stay a JIT program (ReadyToRun).
    Check(Dotnet, ["publish", "src/Tereditor.UI", .. publish, "-o", Path.Combine(output, "client")]);
    Check(Dotnet, ["publish", "src/Tereditor.Host.Remote", .. publish, "-o", Path.Combine(output, "host")]);
    Check(Dotnet, ["publish", "tests/Tereditor.Checks", .. publish, "--self-contained", "true", "-p:PublishReadyToRun=true", "-o", Path.Combine(output, "checks")]);
    foreach (var item in new[] { "LICENSE.md", "NOTICE", "THIRD-PARTY-NOTICES.md", "licenses" }) CopyInto(item, output);
    Copy(Path.Combine("docs", "windows.md"), Path.Combine(output, "WINDOWS.md"));
    var zip = At("artifacts", $"tereditor-{rid}.zip");
    DeleteAll(zip);
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        foreach (var item in new[] { "client", "host", "licenses", "LICENSE.md", "NOTICE", "THIRD-PARTY-NOTICES.md", "WINDOWS.md" })
        {
            var path = Path.Combine(output, item);
            if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(output, file).Replace('\\', '/'), CompressionLevel.Optimal);
            else archive.CreateEntryFromFile(path, item, CompressionLevel.Optimal);
        }
    return 0;
});
