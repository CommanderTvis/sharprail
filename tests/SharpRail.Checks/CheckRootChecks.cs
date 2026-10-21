using System.Diagnostics;
using System.Globalization;

namespace SharpRail.Checks;

/// <summary>Checks of <see cref="CheckRoot"/>: where fixtures live and that every way a run can end leaves nothing behind.</summary>
internal static class CheckRootChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    /// <summary>The body of the child processes below: makes a root and ends in the requested way.</summary>
    internal static int Child(string mode)
    {
        var root = CheckRoot.Root;
        File.WriteAllText(Path.Combine(root, "marker.txt"), "x");
        Console.WriteLine("ROOT " + root);
        Console.Out.Flush();
        switch (mode)
        {
            case "hang": Thread.Sleep(Timeout.Infinite); return 0;
            case "failure": CheckRoot.MarkFailed(); return 1;
            case "throw": throw new InvalidOperationException("Child crash.");
            default: return 0;
        }
    }

    internal static void Run()
    {
        var sandbox = CheckRoot.Create("check-root");
        var checkout = Path.GetFullPath(Directory.GetCurrentDirectory());
        Require(!CheckRoot.Root.StartsWith(checkout + Path.DirectorySeparatorChar, StringComparison.Ordinal), "The check root must not be inside the checkout.");
        Require(CheckRoot.Root.StartsWith(CheckRoot.Parent, StringComparison.Ordinal) && sandbox.StartsWith(CheckRoot.Root, StringComparison.Ordinal), "Directories belong to the process root.");

        Reclaiming(sandbox);
        Lifecycle(sandbox);
        Console.WriteLine("PASS check root: temporary location, deletion on success, failure kept until the next success, crash and kill recovery, read-only files and links");
    }

    private static string Fake(string parent, string pid, TimeSpan age, out string sentinelFolder, bool failed = false)
    {
        var directory = Path.Combine(parent, CheckRoot.Prefix + pid + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(directory, "repo", ".git", "objects"));
        var readOnly = Path.Combine(directory, "repo", ".git", "objects", "pack.idx");
        File.WriteAllText(readOnly, new string('x', 4096));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        sentinelFolder = Path.Combine(parent, "outside-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(sentinelFolder);
        File.WriteAllText(Path.Combine(sentinelFolder, "keep.txt"), "keep");
        if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(directory, "link"), sentinelFolder);
        if (failed) File.WriteAllText(Path.Combine(directory, CheckRoot.FailedMarker), "");
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - age);
        return directory;
    }

    private static void Reclaiming(string sandbox)
    {
        var parent = Path.Combine(sandbox, "temp");
        Directory.CreateDirectory(parent);
        var dead = Fake(parent, "2000000000", TimeSpan.FromHours(2), out var outside);
        var recent = Fake(parent, "2000000000", TimeSpan.FromMinutes(5), out _);
        var alive = Fake(parent, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), TimeSpan.FromHours(2), out _);
        var failed = Fake(parent, "2000000000", TimeSpan.FromHours(2), out _, failed: true);
        var ancient = Fake(parent, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), TimeSpan.FromDays(3), out _);
        var legacy = Path.Combine(sandbox, "bench");
        var oldFixture = Path.Combine(legacy, "check-fixture-" + Guid.NewGuid().ToString("N"));
        var newFixture = Path.Combine(legacy, "check-fixture-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(legacy, "keep-me");
        foreach (var directory in new[] { oldFixture, newFixture, other }) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "f"), "data"); }
        Directory.SetLastWriteTimeUtc(oldFixture, DateTime.UtcNow - TimeSpan.FromHours(3));
        Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromHours(3));

        var freed = CheckRoot.Reclaim(parent, legacy);
        Require(!Directory.Exists(dead) && !Directory.Exists(ancient) && !Directory.Exists(oldFixture), "A stale root of a dead run, an ancient root and an old legacy fixture are reclaimed.");
        Require(Directory.Exists(recent) && Directory.Exists(alive) && Directory.Exists(failed) && Directory.Exists(newFixture) && Directory.Exists(other),
            "Recent roots, the roots of live processes, kept failure evidence, new legacy fixtures and unrelated folders stay.");
        Require(freed >= 2 * 4096, "The reclaimed bytes are reported (" + freed + ").");
        Require(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "keep", "A link inside a root is removed, never followed.");
    }

    private static string Wait(Process process, out string root)
    {
        var output = new List<string>();
        string? line;
        root = "";
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            output.Add(line);
            if (line.StartsWith("ROOT ", StringComparison.Ordinal)) { root = line[5..]; if (!process.StartInfo.ArgumentList.Contains("hang")) continue; break; }
        }
        return string.Join('\n', output);
    }

    private static (int Code, string Root, string Output) Child(string sandbox, string mode, bool wait = true)
    {
        var start = RunnerChecks.Self("--check-root-child", mode);
        start.WorkingDirectory = sandbox;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.Environment[CheckRoot.RootVariable] = Path.Combine(sandbox, "temp");
        start.Environment.Remove(CheckRoot.KeepVariable);
        using var process = Process.Start(start)!;
        var output = Wait(process, out var root);
        if (!wait) { process.Kill(true); process.WaitForExit(); return (process.ExitCode, root, output); }
        output += process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, root, output);
    }

    private static void Lifecycle(string sandbox)
    {
        var folder = Path.Combine(sandbox, "run");
        Directory.CreateDirectory(Path.Combine(folder, ".bench"));
        Directory.CreateDirectory(Path.Combine(folder, "temp"));

        var success = Child(folder, "success");
        Require(success.Code == 0 && success.Root.Length > 0 && success.Root.StartsWith(Path.Combine(folder, "temp"), StringComparison.Ordinal) && !Directory.Exists(success.Root),
            "A successful run deletes its root. " + success.Output);

        var failure = Child(folder, "failure");
        Require(failure.Code == 1 && File.Exists(Path.Combine(failure.Root, CheckRoot.FailedMarker)) && failure.Output.Contains("kept in", StringComparison.Ordinal),
            "A failed run keeps its root as evidence and names it. " + failure.Output);
        var crash = Child(folder, "throw");
        Require(crash.Code != 0 && Directory.Exists(crash.Root) && File.Exists(Path.Combine(crash.Root, CheckRoot.FailedMarker)), "A crash keeps its root as evidence.");

        Child(folder, "success");
        Require(!Directory.Exists(failure.Root) && !Directory.Exists(crash.Root), "The next successful run removes the evidence of failed ones.");
        Require(!Directory.EnumerateFileSystemEntries(Path.Combine(folder, ".bench")).Any(), "Nothing is created in the checkout's scratch folder.");

        var killed = Child(folder, "hang", wait: false);
        Require(killed.Root.Length > 0 && Directory.Exists(killed.Root), "A killed run cannot clean up after itself.");
        Directory.SetLastWriteTimeUtc(killed.Root, DateTime.UtcNow - TimeSpan.FromHours(2));
        var recovery = Child(folder, "success");
        Require(!Directory.Exists(killed.Root) && recovery.Output.Contains("Reclaimed", StringComparison.Ordinal), "The next run reclaims the root of a killed run and says so. " + recovery.Output);
    }
}