using System.Diagnostics;
using System.Globalization;

using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>
/// The one place every check creates fixtures, screenshots and scratch files: a per-process directory under the OS
/// temporary directory (<c>SHARPRAIL_CHECKS_ROOT</c> names another parent), never under the checkout. The directory is
/// deleted when the process ends; <c>SHARPRAIL_CHECKS_KEEP=1</c> keeps it, and a failed gate keeps it as evidence
/// until the next successful run in the checkout. Roots of processes that died without cleaning up are removed by the
/// next run.
/// </summary>
internal static class CheckRoot
{
    internal const string RootVariable = "SHARPRAIL_CHECKS_ROOT";
    internal const string KeepVariable = "SHARPRAIL_CHECKS_KEEP";
    internal const string Prefix = "sharprail-checks-";
    internal const string FailedMarker = ".failed";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedStaleAfter = TimeSpan.FromDays(1);
    private static readonly object Gate = new();
    private static string? root;
    private static bool failed;
    private static bool registered;

    /// <summary>The records of kept failed roots, in the checkout's disposable folder next to the last-run record.</summary>
    private static string FailedRecord => Path.Combine(Path.GetDirectoryName(Runner.LastRun)!, "checks-failed-fixtures.txt");

    internal static string Parent
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(RootVariable);
            var parent = Path.GetFullPath(string.IsNullOrEmpty(configured) ? Path.GetTempPath() : configured);
            Directory.CreateDirectory(parent);
            // Fixtures compare paths with what Git and the watchers report, which is the physical path.
            return Path.TrimEndingDirectorySeparator(Posix.RealPath(parent) ?? parent);
        }
    }

    /// <summary>The process's root, created on first use after reclaiming the roots of dead runs.</summary>
    internal static string Root
    {
        get
        {
            lock (Gate)
            {
                if (root is not null) return root;
                var parent = Parent;
                Reclaim(parent, Path.GetDirectoryName(Runner.LastRun));
                root = Path.Combine(parent, Prefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(root);
                if (!registered)
                {
                    registered = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Finish();
                    Console.CancelKeyPress += (_, _) => { failed = false; Finish(); };
                    AppDomain.CurrentDomain.UnhandledException += (_, _) => { failed = true; Finish(); };
                }
                return root;
            }
        }
    }

    /// <summary>A new empty directory inside the root.</summary>
    internal static string Create(string name)
    {
        var path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The path of a screenshot or other evidence file inside the root; its folder exists.</summary>
    internal static string Artifact(params string[] parts)
    {
        var path = Path.Combine([Root, "artifacts", .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Records that the gate failed, so the root stays as evidence.</summary>
    internal static void MarkFailed() => failed = true;

    /// <summary>Deletes the root unless the run is kept; a failed run keeps it and says where.</summary>
    internal static void Finish()
    {
        string? path;
        lock (Gate) { path = root; root = null; }
        if (path is null) return;
        if (Environment.GetEnvironmentVariable(KeepVariable) == "1") { Console.WriteLine("Check fixtures kept in " + path); return; }
        if (failed)
        {
            try
            {
                File.WriteAllText(Path.Combine(path, FailedMarker), "");
                Directory.CreateDirectory(Path.GetDirectoryName(FailedRecord)!);
                File.AppendAllText(FailedRecord, path + "\n");
            }
            catch (IOException) { }
            Console.WriteLine("Failed check fixtures kept in " + path + " (removed by the next successful run)");
            return;
        }
        TryDelete(path);
        ForgetFailed();
    }

    private static void ForgetFailed()
    {
        try
        {
            if (!File.Exists(FailedRecord)) return;
            foreach (var line in File.ReadAllLines(FailedRecord))
                if (Path.GetFileName(line).StartsWith(Prefix, StringComparison.Ordinal) && File.Exists(Path.Combine(line, FailedMarker))) TryDelete(line);
            File.Delete(FailedRecord);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes roots whose process is gone and old leftovers of the checkout's former fixture folder; returns the bytes freed.</summary>
    internal static long Reclaim(string parent, string? legacy)
    {
        long freed = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(parent, Prefix + "*"))
                if (Stale(directory)) freed += TryDelete(directory);
            if (legacy is not null && Directory.Exists(legacy))
                foreach (var directory in Directory.EnumerateDirectories(legacy, "check-fixture-*"))
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > StaleAfter) freed += TryDelete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (freed > 0) Console.WriteLine($"Reclaimed {freed / 1024.0 / 1024.0:F1} MiB ({freed} bytes) of stale check fixtures");
        return freed;
    }

    private static bool Stale(string directory)
    {
        var name = Path.GetFileName(directory);
        var age = DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory);
        var failedRoot = File.Exists(Path.Combine(directory, FailedMarker));
        if (age > FailedStaleAfter) return true;
        if (failedRoot || age <= StaleAfter) return false;
        var rest = name[Prefix.Length..];
        var end = rest.IndexOf('-');
        if (end <= 0 || !int.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return false;
        return !Alive(pid);
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Deletes a tree without following links and clearing read-only files; returns the bytes freed.</summary>
    internal static long TryDelete(string path)
    {
        long bytes = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { bytes += Remove(new DirectoryInfo(path)); return bytes; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
        return bytes;
    }

    private static long Remove(DirectoryInfo directory)
    {
        if (!directory.Exists) return 0;
        long bytes = 0;
        // A link is removed, never entered.
        if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0) { directory.Delete(); return 0; }
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo child && child.LinkTarget is null && (child.Attributes & FileAttributes.ReparsePoint) == 0) { bytes += Remove(child); continue; }
            if (entry is FileInfo file && file.LinkTarget is null) bytes += file.Length;
            if (entry is DirectoryInfo link) { link.Delete(); continue; }
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
        directory.Attributes &= ~FileAttributes.ReadOnly;
        directory.Delete();
        return bytes;
    }
}