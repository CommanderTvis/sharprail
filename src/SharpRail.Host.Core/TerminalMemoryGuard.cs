using System.Globalization;

namespace SharpRail.Host.Core;

/// <summary>One process of a terminal's tree. <see cref="Started"/> tells a reused pid from the process it replaced.</summary>
internal readonly record struct GuardedProcess(int Pid, string Name, long Bytes, long Started);

/// <summary>Where the guard reads: the real process table in the host, fakes in the checks.</summary>
internal interface IGuardSampler
{
    /// <summary>Takes the snapshot the following <see cref="Tree"/> calls answer from; once per guard tick.</summary>
    void Refresh();

    /// <summary>Every process below a terminal's shell (never the shell itself), each with its memory.</summary>
    IReadOnlyList<GuardedProcess> Tree(int shellPid);

    long PhysicalBytes { get; }
    bool UnderPressure();
}

/// <summary>How the guard ends processes: a stop request (SIGTERM, on Windows a close request) and a forced end.</summary>
internal interface IGuardKiller
{
    void Terminate(GuardedProcess process);
    void Kill(GuardedProcess process);
}

internal enum GuardState { Ok, Warned, Terminating, Killing }

internal enum GuardEventKind { Warned, Stopped, Killed, TreeKilled, Protected }

/// <summary>Something the guard did or noticed: the line shown in the terminal, the same text for the notification and the Processes pane.</summary>
internal sealed record GuardEvent(string Session, GuardEventKind Kind, string Text, DateTimeOffset At);

/// <summary>A terminal the guard watches: its session, the name shown in notices and its shell's pid.</summary>
internal readonly record struct GuardTarget(string Session, string Name, int ShellPid);

/// <summary>A terminal's memory against its limit, as the last tick saw it.</summary>
internal readonly record struct GuardUsage(long Bytes, long Limit, GuardState State, string Largest);

/// <summary>
/// The terminals' OOM killer (see Terminals SPEC "Memory guard"). Each <see cref="Tick"/> reads every terminal's process tree and moves a
/// per-terminal state machine: Ok, Warned (at 80% of a limit, until under 70%), Terminating (the largest process was asked to stop and has
/// the grace period), Killing (it was killed; the next tick that still finds the terminal over its limit kills the whole tree). The shell
/// and this host are never ended. The clock, sampler and killer are injected so checks need no real memory pressure.
/// </summary>
internal sealed class TerminalMemoryGuard(IGuardSampler sampler, IGuardKiller killer, Func<TerminalMemoryLimits> limits, Action<GuardEvent> report)
{
    internal const double WarnAt = 0.8, ClearBelow = 0.7;
    private static readonly string[] AgentNames = ["claude", "codex", "omp"];

    private sealed class Watch
    {
        internal GuardState State;
        internal GuardedProcess Victim;
        internal TimeSpan Deadline;
        internal bool ProtectedReported;
        internal GuardUsage Usage;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Watch> watches = [];
    private readonly Queue<GuardEvent> events = [];
    private readonly List<GuardEvent> raised = [];

    internal Func<TimeSpan> Clock { get; init; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);
    internal Func<DateTimeOffset> Wall { get; init; } = () => DateTimeOffset.UtcNow;
    internal int OwnPid { get; init; } = Environment.ProcessId;
    internal const int MaxEvents = 50;

    /// <summary>The terminal's memory against its limit as of the last tick, or null when it was not watched.</summary>
    internal GuardUsage? UsageOf(string session)
    {
        lock (gate) return watches.TryGetValue(session, out var watch) ? watch.Usage : null;
    }

    /// <summary>The most recent events, oldest first.</summary>
    internal IReadOnlyList<GuardEvent> Events()
    {
        lock (gate) return [.. events];
    }

    internal static string Size(long bytes) => (bytes / (double)(1L << 30)).ToString(bytes >= 10L << 30 ? "0" : "0.#", CultureInfo.InvariantCulture) + " GB";

    internal void Tick(IReadOnlyList<GuardTarget> targets)
    {
        var setting = limits();
        lock (gate)
        {
            foreach (var gone in watches.Keys.Where(session => targets.All(target => target.Session != session)).ToArray()) watches.Remove(gone);
            if (!setting.Enabled) { watches.Clear(); return; }
        }
        try { Run(targets, setting); }
        finally { Flush(); }
    }

    // Events are reported after the lock is released: the report writes into a terminal and raises a notification.
    private void Flush()
    {
        GuardEvent[] pending;
        lock (gate) { pending = [.. raised]; raised.Clear(); }
        foreach (var item in pending) report(item);
    }

    private void Run(IReadOnlyList<GuardTarget> targets, TerminalMemoryLimits setting)
    {
        sampler.Refresh();
        var physical = sampler.PhysicalBytes;
        var perTerminal = setting.PerTerminalBytes(physical);
        var trees = targets.Select(target => (Target: target, Tree: sampler.Tree(target.ShellPid).Where(process => process.Pid != target.ShellPid && process.Pid != OwnPid).ToArray())).ToArray();
        var sums = trees.Select(item => item.Tree.Sum(process => process.Bytes)).ToArray();
        // The host-wide limit and system memory pressure both single out the biggest terminal rather than everyone.
        var biggest = sums.Length == 0 ? -1 : Array.IndexOf(sums, sums.Max());
        var overTotal = sums.Sum() > setting.TotalBytes(physical);
        var pressure = biggest >= 0 && sums[biggest] > 0 && sums[biggest] >= perTerminal / 2 && sampler.UnderPressure();
        lock (gate)
            for (var i = 0; i < trees.Length; i++)
            {
                var (target, tree) = trees[i];
                string? reason = null;
                if (sums[i] > perTerminal) reason = $"terminal memory limit {Size(perTerminal)}";
                else if (i == biggest && overTotal) reason = $"total terminal memory limit {Size(setting.TotalBytes(physical))}";
                else if (i == biggest && pressure) reason = "system memory pressure";
                Step(target, tree, sums[i], perTerminal, reason, setting);
            }
    }

    private void Step(GuardTarget target, GuardedProcess[] tree, long bytes, long limit, string? reason, TerminalMemoryLimits setting)
    {
        if (!watches.TryGetValue(target.Session, out var watch)) watches[target.Session] = watch = new();
        var now = Clock();
        var over = reason is not null;
        var largest = tree.Length == 0 ? "" : tree.MaxBy(process => process.Bytes).Name;
        watch.Usage = new(bytes, limit, watch.State, largest);
        switch (watch.State)
        {
            case GuardState.Terminating:
            {
                if (!over) { Settle(watch); break; }
                var alive = tree.Any(process => Same(process, watch.Victim));
                if (alive && now < watch.Deadline) break;
                if (!alive) { Settle(watch); goto default; }
                var victim = tree.First(process => Same(process, watch.Victim));
                killer.Kill(victim);
                Raise(target, GuardEventKind.Killed, $"SharpRail killed {victim.Name} ({victim.Pid}) at {Size(victim.Bytes)}: it did not stop within {setting.GraceSeconds} s ({reason})");
                watch.State = GuardState.Killing;
                break;
            }
            case GuardState.Killing:
            {
                if (!over) { Settle(watch); break; }
                var rest = tree.Where(process => !setting.Protects(process.Name)).ToArray();
                // Children first, so a parent cannot restart what was just ended.
                foreach (var process in rest.OrderByDescending(process => process.Pid)) killer.Kill(process);
                Raise(target, GuardEventKind.TreeKilled, $"SharpRail killed everything below the shell ({rest.Length} processes, {Size(rest.Sum(process => process.Bytes))}): the terminal stayed over its limit ({reason})");
                Settle(watch);
                break;
            }
            default:
            {
                if (over && Victim(tree, setting) is { } victim)
                {
                    killer.Terminate(victim);
                    Raise(target, GuardEventKind.Stopped, $"SharpRail stopped {victim.Name} ({victim.Pid}) at {Size(victim.Bytes)}: {reason}");
                    watch.State = GuardState.Terminating;
                    watch.Victim = victim;
                    watch.Deadline = now + TimeSpan.FromSeconds(setting.GraceSeconds);
                    watch.ProtectedReported = false;
                }
                else if (over)
                {
                    // The biggest process is protected (or nothing is left to end): only warn, once per episode.
                    watch.State = GuardState.Warned;
                    if (!watch.ProtectedReported)
                    {
                        watch.ProtectedReported = true;
                        Raise(target, GuardEventKind.Protected, $"{target.Name} uses {Size(bytes)}, over its limit ({reason}), but {largest} is protected: SharpRail will not stop it");
                    }
                }
                else if (bytes >= limit * WarnAt && watch.State == GuardState.Ok)
                {
                    watch.State = GuardState.Warned;
                    Raise(target, GuardEventKind.Warned, $"{target.Name} uses {Size(bytes)} of {Size(limit)}: {largest}");
                }
                else if (watch.State == GuardState.Warned && bytes < limit * ClearBelow)
                {
                    watch.State = GuardState.Ok;
                    watch.ProtectedReported = false;
                }
                else if (watch.State == GuardState.Warned && !over) watch.ProtectedReported = false;
                break;
            }
        }
        watch.Usage = watch.Usage with { State = watch.State };
    }

    private static void Settle(Watch watch) { watch.State = GuardState.Ok; watch.Victim = default; }

    private static bool Same(GuardedProcess left, GuardedProcess right) => left.Pid == right.Pid && left.Started == right.Started && left.Name == right.Name;

    /// <summary>
    /// The process to end: the largest one, unless it is protected (then none). A known agent is passed over for the largest other
    /// process, and ended only when nothing else is left or it is at least twice as large as everything else.
    /// </summary>
    internal static GuardedProcess? Victim(IReadOnlyList<GuardedProcess> tree, TerminalMemoryLimits setting)
    {
        if (tree.Count == 0) return null;
        var largest = tree.OrderByDescending(process => process.Bytes).ThenBy(process => process.Pid).First();
        if (setting.Protects(largest.Name)) return null;
        if (!AgentNames.Contains(largest.Name, StringComparer.OrdinalIgnoreCase)) return largest;
        var others = tree.Where(process => !AgentNames.Contains(process.Name, StringComparer.OrdinalIgnoreCase) && !setting.Protects(process.Name))
            .OrderByDescending(process => process.Bytes).ThenBy(process => process.Pid).ToArray();
        return others.Length == 0 || largest.Bytes >= 2 * others[0].Bytes ? largest : others[0];
    }

    private void Raise(GuardTarget target, GuardEventKind kind, string text)
    {
        var item = new GuardEvent(target.Session, kind, text, Wall());
        events.Enqueue(item);
        while (events.Count > MaxEvents) events.Dequeue();
        raised.Add(item);
    }
}
