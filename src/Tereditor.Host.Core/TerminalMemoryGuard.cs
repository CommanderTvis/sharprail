using System.Globalization;

using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core;

/// <summary>
/// One process of a terminal's tree. <see cref="Started"/> tells a reused pid from the process it replaced. <see cref="Agent"/> is the
/// agent's name when the sampler recognised one by its executable (a native Claude Code runs as a binary named after its version).
/// </summary>
internal readonly record struct GuardedProcess(int Pid, string Name, long Bytes, long Started, int Parent = 0, string? Agent = null);

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
    private static readonly IReadOnlyList<string> AgentNames = AgentExecutables.Names;

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

    /// <summary>The agent a process is, by its executable path (<c>…/claude/versions/2.1.3</c> or <c>…/codex</c>) or its name, against <see cref="AgentExecutables"/>; null for any other program.</summary>
    internal static string? AgentOf(string name, string? path)
    {
        if (AgentNames.FirstOrDefault(agent => agent.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } named) return named;
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Split('/', '\\');
        var file = Path.GetFileNameWithoutExtension(parts[^1]);
        if (AgentNames.FirstOrDefault(agent => agent.Equals(file, StringComparison.OrdinalIgnoreCase)) is { } executable) return executable;
        return parts.Length >= 3 && parts[^2] == "versions" ? AgentNames.FirstOrDefault(agent => agent.Equals(parts[^3], StringComparison.OrdinalIgnoreCase)) : null;
    }

    private static string? AgentOf(GuardedProcess process) => process.Agent ?? AgentOf(process.Name, null);

    // An agent is named as the user knows it, not by its version-numbered binary.
    private static string Shown(GuardedProcess process) => AgentOf(process) ?? process.Name;

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
                if (sums[i] > perTerminal) reason = $"this terminal uses {Size(sums[i])}, over its limit of {Size(perTerminal)}";
                else if (i == biggest && overTotal) reason = $"all terminals use {Size(sums.Sum())}, over their total limit of {Size(setting.TotalBytes(physical))}";
                else if (i == biggest && pressure) reason = "the system is under memory pressure";
                Step(target, tree, sums[i], perTerminal, reason, setting);
            }
    }

    private void Step(GuardTarget target, GuardedProcess[] tree, long bytes, long limit, string? reason, TerminalMemoryLimits setting)
    {
        if (!watches.TryGetValue(target.Session, out var watch)) watches[target.Session] = watch = new();
        var now = Clock();
        var over = reason is not null;
        var largest = tree.Length == 0 ? "" : Shown(tree.MaxBy(process => process.Bytes));
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
                    Raise(target, GuardEventKind.Killed, $"tereditor memory guard: killed {Shown(victim)} (pid {victim.Pid}, {Size(victim.Bytes)}) because it did not stop within {setting.GraceSeconds} s and {reason}");
                    watch.State = GuardState.Killing;
                    break;
                }
            case GuardState.Killing:
                {
                    if (!over) { Settle(watch); break; }
                    // An agent is spared unless it alone is over the limit: ending its subprocesses brings the terminal back under.
                    var rest = tree.Where(process => !setting.Protects(process.Name) && (AgentOf(process) is null || process.Bytes > limit)).ToArray();
                    // Children first, so a parent cannot restart what was just ended.
                    foreach (var process in rest.OrderByDescending(process => process.Pid)) killer.Kill(process);
                    var spared = tree.Length > rest.Length ? $", sparing {string.Join(", ", tree.Except(rest).Select(Shown).Distinct())}" : "";
                    if (rest.Length > 0)
                        Raise(target, GuardEventKind.TreeKilled, $"tereditor memory guard: killed everything below the shell ({rest.Length} processes, {Size(rest.Sum(process => process.Bytes))}{spared}) because {reason} after its largest process was killed");
                    Settle(watch);
                    break;
                }
            default:
                {
                    if (over && Victim(tree, setting, limit) is { } victim)
                    {
                        killer.Terminate(victim);
                        Raise(target, GuardEventKind.Stopped, $"tereditor memory guard: stopped {Shown(victim)} (pid {victim.Pid}, {Size(victim.Bytes)}) because {reason}");
                        watch.State = GuardState.Terminating;
                        watch.Victim = victim;
                        watch.Deadline = now + TimeSpan.FromSeconds(setting.GraceSeconds);
                        watch.ProtectedReported = false;
                    }
                    else if (over)
                    {
                        // The biggest process is protected, or an agent with nothing else to end: only warn, once per episode.
                        watch.State = GuardState.Warned;
                        if (!watch.ProtectedReported)
                        {
                            watch.ProtectedReported = true;
                            var why = AgentOf(tree.MaxBy(process => process.Bytes)) is null ? "is protected" : "is an agent with no subprocess to stop and is under the limit by itself";
                            Raise(target, GuardEventKind.Protected, $"tereditor memory guard: {target.Name} is over its limit ({reason}), but {largest} {why}, so tereditor will not stop it");
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
    /// The process to end: the largest one, unless it is protected (then none). A known agent is what the user works with, so it is
    /// passed over for its heaviest subprocess (a build, a test run, a language server), then for the heaviest other program; it is
    /// ended only as a last resort, when it alone is over the per-terminal <paramref name="limit"/>.
    /// </summary>
    internal static GuardedProcess? Victim(IReadOnlyList<GuardedProcess> tree, TerminalMemoryLimits setting, long limit)
    {
        if (tree.Count == 0) return null;
        var ordered = tree.OrderByDescending(process => process.Bytes).ThenBy(process => process.Pid).ToArray();
        var largest = ordered[0];
        if (setting.Protects(largest.Name)) return null;
        // An agent alone over the limit: no subprocess can bring the terminal back under, so it is asked to stop like any other.
        if (AgentOf(largest) is null || largest.Bytes > limit) return largest;
        var below = Descendants(tree, largest.Pid);
        var endable = ordered.Where(process => AgentOf(process) is null && !setting.Protects(process.Name)).ToArray();
        return endable.Where(process => below.Contains(process.Pid)).Concat(endable).Cast<GuardedProcess?>().FirstOrDefault()
            ?? (largest.Bytes > limit ? largest : null);
    }

    private static HashSet<int> Descendants(IReadOnlyList<GuardedProcess> tree, int pid)
    {
        var children = tree.ToLookup(process => process.Parent);
        var found = new HashSet<int>();
        var queue = new Queue<int>([pid]);
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (found.Add(child.Pid)) queue.Enqueue(child.Pid);
        return found;
    }

    private void Raise(GuardTarget target, GuardEventKind kind, string text)
    {
        var item = new GuardEvent(target.Session, kind, text, Wall());
        events.Enqueue(item);
        while (events.Count > MaxEvents) events.Dequeue();
        raised.Add(item);
    }
}