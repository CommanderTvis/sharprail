using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpRail.Checks;

/// <summary>
/// Makes the runner the one signal manager of everything it started: an interrupt is forwarded to every
/// descendant and whatever survives the grace period is killed, even once the runner itself is going.
/// </summary>
internal static class ProcessTree
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
    private const int Interrupt = 2, Kill = 9, Terminate = 15;
    private static readonly List<PosixSignalRegistration> registrations = [];
    private static int stopping;

    [DllImport("libc", EntryPoint = "kill")] private static extern int Signal(int pid, int signal);
    [DllImport("libc", EntryPoint = "setpgid")] private static extern int SetProcessGroup(int pid, int group);

    internal readonly record struct Entry(int Pid, int Parent, int Group, bool Zombie);

    /// <summary>The groups to signal as a whole and the processes that share the runner's own group.</summary>
    internal readonly record struct Targets(int[] Groups, int[] Pids)
    {
        internal bool Alive(IEnumerable<Entry> processes) => processes.Any(Contains);
        private bool Contains(Entry process) => !process.Zombie && (Pids.Contains(process.Pid) || Groups.Contains(process.Group));
    }

    /// <summary>A lane leads its own group, so the owner can signal the lane and its children in one call.</summary>
    internal static void LeadGroup()
    {
        if (!OperatingSystem.IsWindows()) _ = SetProcessGroup(0, 0);
    }

    internal static void Send(int pid, PosixSignal signal) => _ = Signal(pid, signal == PosixSignal.SIGINT ? Interrupt : Terminate);

    internal static void Own()
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var (signal, number) in new[] { (PosixSignal.SIGINT, Interrupt), (PosixSignal.SIGTERM, Terminate) })
            registrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                if (Interlocked.Exchange(ref stopping, 1) != 0) return;
                Stop(number);
                Environment.Exit(128 + number);
            }));
    }

    internal static List<Entry> Snapshot()
    {
        var start = new ProcessStartInfo("ps") { RedirectStandardOutput = true };
        foreach (var argument in new[] { "-axo", "pid=,ppid=,pgid=,stat=" }) start.ArgumentList.Add(argument);
        using var ps = Process.Start(start)!;
        var entries = new List<Entry>();
        while (ps.StandardOutput.ReadLine() is { } line)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0] == ps.Id.ToString(CultureInfo.InvariantCulture)) continue;
            entries.Add(new(int.Parse(fields[0], CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture),
                int.Parse(fields[2], CultureInfo.InvariantCulture), fields[3].StartsWith('Z')));
        }
        ps.WaitForExit();
        return entries;
    }

    internal static Targets Descendants(IReadOnlyList<Entry> processes, int root)
    {
        var own = processes.FirstOrDefault(process => process.Pid == root).Group;
        var found = new List<Entry>();
        var parents = new Queue<int>([root]);
        while (parents.TryDequeue(out var parent))
            foreach (var child in processes.Where(process => process.Parent == parent && found.All(seen => seen.Pid != process.Pid)))
            {
                found.Add(child); parents.Enqueue(child.Pid);
            }
        bool Separate(Entry process) => process.Group > 1 && process.Group != own;
        return new([.. found.Where(Separate).Select(process => process.Group).Distinct()], [.. found.Where(process => !Separate(process)).Select(process => process.Pid)]);
    }

    private static void Stop(int signal)
    {
        var targets = Descendants(Snapshot(), Environment.ProcessId);
        Deliver(targets, signal);
        var deadline = DateTime.UtcNow + Grace;
        while (DateTime.UtcNow < deadline && targets.Alive(Snapshot())) Thread.Sleep(100);
        Deliver(targets, Kill);
    }

    private static void Deliver(Targets targets, int signal)
    {
        foreach (var group in targets.Groups) _ = Signal(-group, signal);
        foreach (var pid in targets.Pids) _ = Signal(pid, signal);
    }
}