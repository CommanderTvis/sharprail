using System.Diagnostics;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Processes.Host;

/// <summary>Where the sampler reads: the real host in the plugin, fakes in the checks. Every member may read the process table or wait.</summary>
internal interface IProcessSource
{
    ProcessInputs Read();
    (TimeSpan Cpu, long MemoryBytes)? Usage(int pid);
    string WorkspaceLabel(string workspace);
    ValueTask StopLanguageServerAsync(string workspace, string serverId, CancellationToken cancellationToken);
    ValueTask StopProcessAsync(int pid, string command, CancellationToken cancellationToken);
}

/// <summary>
/// Turns the sources into snapshots. It samples only when asked (the visible pane asks), at most once per <see cref="MinimumInterval"/>;
/// a closer call returns the last sample, so several windows cost one sample. CPU percent is the processor-time delta over the wall-clock delta.
/// </summary>
internal sealed class ProcessSampler(IProcessSource source)
{
    private readonly Lock gate = new();
    private readonly ProcessHistory history = new();
    private readonly Dictionary<int, (TimeSpan Cpu, long At)> previous = [];
    private ProcessSnapshot? last;
    private IReadOnlyDictionary<string, StopTarget> stops = new Dictionary<string, StopTarget>();
    private long lastAt, sequence;

    /// <summary>How many times the sources were read; checks use it to prove nothing samples unasked.</summary>
    internal int Samples { get; private set; }

    internal TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal Func<long> Clock { get; init; } = Stopwatch.GetTimestamp;
    internal long Frequency { get; init; } = Stopwatch.Frequency;

    internal ProcessSnapshot Sample()
    {
        lock (gate)
        {
            var now = Clock();
            if (last is not null && (now - lastAt) < MinimumInterval.TotalSeconds * Frequency) return last;
            var inputs = source.Read();
            var seen = new HashSet<int>();
            var cache = new Dictionary<int, (double, long)?>();
            (double Cpu, long Memory)? Usage(int pid)
            {
                if (cache.TryGetValue(pid, out var known)) return known;
                var usage = source.Usage(pid);
                (double, long)? result = null;
                if (usage is { } value)
                {
                    seen.Add(pid);
                    var percent = 0.0;
                    if (previous.TryGetValue(pid, out var before) && now > before.At)
                        percent = Math.Max(0, (value.Cpu - before.Cpu).TotalSeconds / ((now - before.At) / (double)Frequency) * 100);
                    previous[pid] = (value.Cpu, now);
                    result = (percent, value.MemoryBytes);
                }
                cache[pid] = result;
                return result;
            }
            var (snapshot, targets) = ProcessModel.Build(inputs, Usage, source.WorkspaceLabel, history, ++sequence);
            foreach (var pid in previous.Keys.Where(pid => !seen.Contains(pid)).ToArray()) previous.Remove(pid);
            stops = targets;
            last = snapshot;
            lastAt = now;
            Samples++;
            return snapshot;
        }
    }

    /// <summary>
    /// Stops the processes and language servers at or below a node of the last snapshot; an id the snapshot does not hold stops nothing,
    /// so a stale or forged id can never name an arbitrary pid. Language servers go through the language service, never a signal.
    /// </summary>
    /// <summary>Whether stopping an agent row also stops what it forked (the plugin setting); read per stop.</summary>
    internal Func<bool> StopAgentChildren { get; init; } = () => true;

    internal async Task<int> StopAsync(string id, CancellationToken cancellationToken)
    {
        (string Id, StopTarget Target)[] targets;
        lock (gate) targets = last is null ? [] : [.. ProcessModel.StopsUnder(last, stops, id, StopAgentChildren())];
        await Task.WhenAll(targets.Select(item => item.Target switch
        {
            { Workspace: { } workspace, ServerId: { } server } => source.StopLanguageServerAsync(workspace, server, cancellationToken).AsTask(),
            { Pid: { } pid } => source.StopProcessAsync(pid, item.Target.Command, cancellationToken).AsTask(),
            _ => Task.CompletedTask
        }));
        lock (gate) lastAt = 0; // the next ask samples fresh: the tree changed
        return targets.Length;
    }
}