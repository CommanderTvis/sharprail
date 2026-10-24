using System.Diagnostics;

using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>
/// A listing whose ignore check stalls: a fake git hangs on check-ignore and leaves an orphan holding its output pipe,
/// so the run can be neither waited for nor drained. The listing must still return its entries, unmarked, within the bound.
/// </summary>
internal static class ListingGitStallChecks
{
    internal static async Task Run(string root)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP listing git stall: POSIX shell shim"); return; }
        var parent = Path.Combine(root, "listing-git-stall");
        var shim = Path.Combine(parent, "shim");
        var work = Path.Combine(parent, "work");
        Directory.CreateDirectory(shim);
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "README.md"), "# stall\n");
        Directory.CreateDirectory(Path.Combine(work, "src"));
        File.WriteAllText(Path.Combine(work, "src", "a.txt"), "a\n");
        File.WriteAllText(Path.Combine(work, "b.txt"), "b\n");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var realGit = ChildProcess.Locate("git", path) ?? throw new InvalidOperationException("git is not on PATH.");
        var orphan = Path.Combine(shim, "orphan.pid");
        var git = Path.Combine(shim, "git");
        File.WriteAllText(git, $$"""
            #!/bin/sh
            if [ "$1" = "check-ignore" ]; then
              cat >/dev/null
              ( trap '' TERM; sleep 60 & echo $! > '{{orphan}}' )
              trap '' TERM
              sleep 60
              exit 0
            fi
            exec '{{realGit}}' "$@"
            """.Replace("\r", ""));
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bound = ProjectServices.ListingGitTimeout;
        try
        {
            Environment.SetEnvironmentVariable("PATH", shim + Path.PathSeparator + path);
            ProjectServices.ListingGitTimeout = TimeSpan.FromSeconds(1);
            var clock = Stopwatch.StartNew();
            var listing = await new ProjectServices(work).ListFilesAsync("").AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            var limit = ProjectServices.ListingGitTimeout + GitRepository.KillGrace + TimeSpan.FromSeconds(2);
            if (clock.Elapsed > limit)
                throw new InvalidOperationException($"A stalled ignore check held the listing for {clock.Elapsed.TotalSeconds:0.0} s (limit {limit.TotalSeconds:0.0} s).");
            if (!listing.Select(file => file.Path).SequenceEqual(["src", "b.txt", "README.md"]) || listing.Any(file => file.GitIgnored))
                throw new InvalidOperationException("A stalled ignore check must still list every entry, unmarked: " + string.Join(", ", listing.Select(file => file.Path)));
            Console.WriteLine($"PASS listing git stall: entries listed without ignore marks in {clock.Elapsed.TotalSeconds:0.0} s despite a hung check-ignore and an orphan holding its pipe");
        }
        finally
        {
            ProjectServices.ListingGitTimeout = bound;
            Environment.SetEnvironmentVariable("PATH", path);
            if (File.Exists(orphan) && int.TryParse(File.ReadAllText(orphan).Trim(), out var pid))
                try { Process.GetProcessById(pid).Kill(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        }
    }
}