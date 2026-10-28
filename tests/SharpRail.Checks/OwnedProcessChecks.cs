using System.Diagnostics;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Plugins.Api;

namespace SharpRail.Checks;

// Processes an agent reports with owned_process_report are stopped with their terminal, on host shutdown and by the
// start-up sweep, never by a reused pid, and never when they are not reported or were released.
internal static class OwnedProcessChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static readonly List<Process> Spawned = [];

    private static Process Sleep()
    {
        var process = Process.Start(new ProcessStartInfo("sleep", "600") { UseShellExecute = false }) ?? throw new InvalidOperationException("sleep did not start.");
        Spawned.Add(process);
        return process;
    }

    private static bool Gone(Process process, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return true;
            Thread.Sleep(50);
        }
        return process.HasExited;
    }

    public static async Task Run(string root)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP owned processes: the checks start sleep and a POSIX shell"); return; }
        var workspace = Path.Combine(root, "owned-workspace");
        Directory.CreateDirectory(workspace);
        var directory = Path.Combine(root, "owned-state");
        try
        {
            await TabCloseAndRefusals(directory, workspace);
            await ShutdownAndRelease(Path.Combine(root, "owned-state-2"), workspace);
            await Sweep(Path.Combine(root, "owned-state-3"));
        }
        finally
        {
            foreach (var process in Spawned) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
        }
        Console.WriteLine("PASS owned processes: a reported process is stopped when its terminal closes and when the host shuts down, a release keeps it running, pid 1, the host, a shell, a dead pid and a foreign process are refused, and the start-up sweep stops a matching stale record but never a process whose start time differs");
    }

    private static async Task<TerminalRef> OpenTab(PtyTerminalService service, string workspace)
    {
        var key = "owned-" + Guid.NewGuid().ToString("N");
        await service.ReserveAsync(workspace, new(key, "Owned"));
        await service.AttachAsync(new(TerminalTab.SessionFor(workspace, key), workspace, "client", 100, 30) { TabKey = key });
        return new(workspace, key);
    }

    private static async Task TabCloseAndRefusals(string directory, string workspace)
    {
        await using var service = new PtyTerminalService("/bin/sh", recordingsDirectory: Path.Combine(directory, "terminals"));
        service.Owned.Grace = TimeSpan.FromSeconds(3);
        var tab = await OpenTab(service, workspace);
        var shells = service.List().Where(process => process.Pid is not null).Select(process => process.Pid!.Value).ToArray();
        Require(shells.Length > 0, "The tab must have a shell.");
        var child = Sleep();
        var changes = 0;
        service.Owned.Changed += () => changes++;
        service.Owned.Report(tab, child.Id, "sleep check", shells);
        Require(service.Owned.List().Single() is { Pid: var pid, Description: "sleep check" } && pid == child.Id && changes == 1, "A reported process must be listed once with its description.");
        Require(File.Exists(Path.Combine(directory, $"owned-processes-{Environment.ProcessId}.json")), "A report must be persisted for the start-up sweep.");

        void Refused(int pid, string why)
        {
            try { service.Owned.Report(tab, pid, null, shells); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException(why);
        }
        Refused(1, "pid 1 must be refused.");
        Refused(Environment.ProcessId, "The host itself must be refused.");
        Refused(shells[0], "A terminal's shell must be refused.");
        var dead = Sleep();
        dead.Kill(); dead.WaitForExit();
        Refused(dead.Id, "A dead pid must be refused.");
        Refused(ForeignPid(), "A process of another user must be refused.");
        Require(service.Owned.List().Count == 1, "Refusals must change nothing.");

        await service.CloseTabAsync(workspace, tab.TabKey);
        Require(Gone(child), "Closing the terminal must stop its reported process.");
        Require(service.Owned.List().Count == 0, "A stopped process must leave the registry.");
        Require(!File.Exists(Path.Combine(directory, $"owned-processes-{Environment.ProcessId}.json")), "An empty registry must delete its file.");
    }

    private static async Task ShutdownAndRelease(string directory, string workspace)
    {
        var released = Sleep();
        var kept = Sleep();
        var service = new PtyTerminalService("/bin/sh", recordingsDirectory: Path.Combine(directory, "terminals"));
        var tab = await OpenTab(service, workspace);
        var other = await OpenTab(service, workspace);
        service.Owned.Report(tab, released.Id, null, []);
        service.Owned.Report(other, kept.Id, "other tab", []);
        Require(!service.Owned.Release(other, released.Id), "A terminal cannot release another terminal's process.");
        Require(service.Owned.Release(tab, released.Id) && service.Owned.List().Count == 1, "Release must forget the record.");
        Require(!released.HasExited, "Release must not stop the process.");
        await service.DisposeAsync();
        Require(Gone(kept), "Host shutdown must stop the reported process.");
        Require(!released.HasExited, "Host shutdown must not stop a released process.");
    }

    private static async Task Sweep(string directory)
    {
        Directory.CreateDirectory(directory);
        var reused = Sleep();
        var stale = Sleep();
        var deadHost = Sleep();
        deadHost.Kill(); deadHost.WaitForExit();
        var ghost = new TerminalRef("/nowhere", "tab");
        void Write(string name, int hostPid, DateTime hostStarted, params OwnedProcesses.Entry[] entries) =>
            File.WriteAllText(Path.Combine(directory, $"owned-processes-{name}.json"), JsonSerializer.Serialize(new { HostPid = hostPid, HostStarted = hostStarted, Entries = entries }));
        // A crashed host's records: one for a reused pid (start time differs), one that still matches.
        Write("1001", deadHost.Id, DateTime.UtcNow.AddDays(-1),
            new OwnedProcesses.Entry(ghost, reused.Id, "sleep 600", "reused", reused.StartTime.ToUniversalTime().AddHours(1)),
            new OwnedProcesses.Entry(ghost, stale.Id, "sleep 600", "stale", stale.StartTime.ToUniversalTime()));
        // A running host's file is left alone, whatever it holds.
        var living = Sleep();
        Write("1002", Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            new OwnedProcesses.Entry(ghost, living.Id, "sleep 600", "living", living.StartTime.ToUniversalTime()));
        await new OwnedProcesses(directory) { Grace = TimeSpan.FromSeconds(3) }.SweepAsync();
        Require(Gone(stale), "The sweep must stop a record whose pid and start time still match.");
        Require(!reused.HasExited, "The sweep must not stop a process whose start time differs from the record's.");
        Require(!File.Exists(Path.Combine(directory, "owned-processes-1001.json")), "The sweep must drop the records it handled.");
        Require(!living.HasExited && File.Exists(Path.Combine(directory, "owned-processes-1002.json")), "The sweep must leave a running host's records alone.");
    }

    // A live process another user owns (root's, from the process table), which this user may not signal.
    private static int ForeignPid()
    {
        var table = Process.Start(new ProcessStartInfo("ps", "-A -o pid=,uid=") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = table.StandardOutput.ReadToEnd();
        table.WaitForExit();
        var mine = Environment.UserName;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && pid > 1 && parts[1] == "0" && mine != "root") return pid;
        }
        throw new InvalidOperationException("No process of another user to refuse.");
    }
}