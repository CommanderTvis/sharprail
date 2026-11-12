using System.Diagnostics;

namespace SharpRail.Checks;

/// <summary>
/// scripts/lock.sh is the directory lock of the native build scripts: it must remove a lock whose owner is gone instead of waiting
/// for it forever, wait for a live holder, and release its own lock when the script exits.
/// </summary>
internal static class LockScriptChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        if (BoundaryChecks.Checkout() is not { } root || OperatingSystem.IsWindows()) { Console.WriteLine("SKIP lock-script: no source checkout or no POSIX shell"); return; }
        var lib = Path.Combine(root, "scripts", "lock.sh");
        var work = CheckRoot.Create("lock-script");

        // A lock left by a process that no longer exists is removed and taken over.
        var dead = Path.Combine(work, "dead");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "pid"), "999999");
        var result = Hold(lib, dead, 0);
        Require(result.Exit == 0 && result.Output.Contains("Removing the stale lock") && result.Output.Contains("held by"), "A lock whose owner is gone must be taken over: " + result.Output);
        Require(!Directory.Exists(dead), "The script must release its lock when it exits.");

        // A lock directory without an owner file is stale once it is two minutes old, and not before.
        var old = Path.Combine(work, "old");
        Directory.CreateDirectory(old);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));
        result = Hold(lib, old, 0);
        Require(result.Exit == 0 && result.Output.Contains("names no owner"), "An old lock without an owner file must be taken over: " + result.Output);

        // A live holder is waited for, not displaced.
        var live = Path.Combine(work, "live");
        using var holder = Start(lib, live, 3);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(Path.Combine(live, "pid")) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Require(File.Exists(Path.Combine(live, "pid")), "The first script must hold the lock.");
        var started = DateTime.UtcNow;
        result = Hold(lib, live, 0);
        Require(result.Exit == 0 && DateTime.UtcNow - started > TimeSpan.FromSeconds(1) && !result.Output.Contains("Removing"), "A live holder must be waited for: " + result.Output);
        holder.WaitForExit();
        Console.WriteLine("PASS lock script: stale locks are taken over, live holders are waited for, the lock is released on exit");
    }

    private static Process Start(string lib, string lockDirectory, int seconds)
    {
        var start = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(". \"$1\"; acquire_lock \"$2\"; echo held by $$; sleep \"$3\"");
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add(lib);
        start.ArgumentList.Add(lockDirectory);
        start.ArgumentList.Add(seconds.ToString());
        return Process.Start(start)!;
    }

    private static (int Exit, string Output) Hold(string lib, string lockDirectory, int seconds)
    {
        using var process = Start(lib, lockDirectory, seconds);
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}