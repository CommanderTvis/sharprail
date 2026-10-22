using System.Globalization;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// Limits on running language servers (see SPEC "Server limits"): an idle timeout, a memory budget across every server, a count cap per
/// server kind and memory-pressure shrinking. A stopped server restarts on its next request, so the limits never change an answer, only
/// how long the next call waits. Servers with an open editor buffer or a request in flight are never stopped by the limits.
/// </summary>
public sealed partial class LanguageService
{
    private readonly object limitGate = new();
    private TaskCompletionSource slotFreed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int enforcing;

    /// <summary>The current limits, read from host settings on every decision.</summary>
    internal Func<LanguageServerLimits> Limits { get; init; } = () => LanguageServerLimits.Default;

    /// <summary>The clock of last use; settable so checks can age servers without waiting.</summary>
    internal Func<long> Ticks { get; set; } = () => Environment.TickCount64;

    /// <summary>Whether the system is short of memory; settable for checks.</summary>
    internal Func<bool> Pressure { get; set; } = LanguageMemory.UnderPressure;

    /// <summary>A server's memory as the limits see it; settable so checks can give fake servers a size.</summary>
    internal Func<string, string, long, long> MemoryOf { get; set; } = (_, _, sampled) => sampled;

    internal const string WaitingForSlot = "Waiting for a free language server slot…";

    private static long Memory(Entry entry) => entry.Session.IsValueCreated && entry.Session.Value is { IsCompletedSuccessfully: true } done && !done.Result.Exited ? done.Result.MemoryBytes : 0;

    private long MemoryFor(Entry entry) => MemoryOf(entry.Workspace, entry.ServerId, Memory(entry));

    /// <summary>Whether the entry holds a slot: admitted, not stopped and its process not dead.</summary>
    private static bool Holding(Entry entry) => Volatile.Read(ref entry.HoldsSlot) && Volatile.Read(ref entry.Stopped) == 0 &&
        !(entry.Session.IsValueCreated && entry.Session.Value is { IsCompleted: true } task && (!task.IsCompletedSuccessfully || task.Result.Exited));

    /// <summary>Whether the limits may stop the entry: started, no request in flight and no editor buffer open.</summary>
    private static bool Evictable(Entry entry) => Volatile.Read(ref entry.InFlight) == 0 && Volatile.Read(ref entry.Stopped) == 0 &&
        entry.Session.IsValueCreated && entry.Session.Value is { IsCompletedSuccessfully: true } task && !task.Result.HasEditors;

    private void Touch(Entry entry) => Volatile.Write(ref entry.LastUsed, Ticks());

    private void Pulse()
    {
        TaskCompletionSource freed;
        lock (limitGate) { freed = slotFreed; slotFreed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        freed.TrySetResult();
    }

    /// <summary>
    /// Admits a starting server under the count cap and memory budget, first stopping the least recently used idle server that is in the
    /// way; when every server in the way is busy, waits (the entry reports <see cref="WaitingForSlot"/>) instead of exceeding a limit.
    /// </summary>
    private async Task AcquireSlotAsync(Entry entry, CancellationToken token)
    {
        while (true)
        {
            Entry? victim;
            Task freed;
            lock (limitGate)
            {
                var limits = Limits();
                var holders = entries.Values.Where(Holding).ToArray();
                var overCap = holders.Count(other => other.ServerId == entry.ServerId) >= limits.Cap(entry.ServerId);
                var overBudget = holders.Sum(MemoryFor) >= limits.BudgetBytes;
                if (!overCap && !overBudget)
                {
                    Volatile.Write(ref entry.HoldsSlot, true);
                    return;
                }
                victim = holders.Where(Evictable).Where(other => !overCap || other.ServerId == entry.ServerId).OrderBy(other => Volatile.Read(ref other.LastUsed)).FirstOrDefault();
                freed = slotFreed.Task;
            }
            if (victim is not null)
            {
                await EvictAsync(victim, $"the {(victim.ServerId == entry.ServerId ? "running " + entry.ServerId + " server cap" : "language server memory budget")} needed room for {entry.ServerId} in {entry.Workspace}");
                continue;
            }
            entry.State = "initializing"; entry.Detail = WaitingForSlot;
            logs.GetOrAdd(entry.Workspace + "\0" + entry.ServerId, _ => new()).Add("lifecycle", WaitingForSlot);
            // A slot frees when a server stops or goes idle; the periodic enforcement also pulses, so a missed pulse costs one period.
            await Task.WhenAny(freed, Task.Delay(TimeSpan.FromSeconds(5), token));
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Stops a server for a limit; its next request starts it again. Returns false when another stop got there first.</summary>
    private async Task<bool> EvictAsync(Entry entry, string reason)
    {
        var key = entry.Workspace + "\0" + entry.ServerId;
        if (!entries.TryRemove(new KeyValuePair<string, Entry>(key, entry))) return false;
        logs.GetOrAdd(key, _ => new()).Add("lifecycle", "Stopped by the language server limits: " + reason + ". The next request starts it again.");
        try { await PreserveDraftsAsync(key, entry, CancellationToken.None); }
        catch (Exception) { }
        finally { await StopAsync(entry, "stopped by the language server limits: " + reason); }
        return true;
    }

    /// <summary>
    /// One enforcement pass, run every <see cref="EnforcementPeriod"/> off any UI thread: samples memory, stops servers idle past the
    /// timeout, shrinks to recently used servers under memory pressure and stops least recently used idle servers while over budget.
    /// </summary>
    internal async Task EnforceAsync()
    {
        if (Interlocked.Exchange(ref enforcing, 1) != 0) return;
        try
        {
            SampleMemory();
            var limits = Limits();
            var now = Ticks();
            if (limits.Idle is { } idle)
                foreach (var entry in entries.Values.Where(Holding).Where(Evictable).ToArray())
                    if (now - Volatile.Read(ref entry.LastUsed) >= (long)idle.TotalMilliseconds)
                        await EvictAsync(entry, $"unused for {limits.IdleMinutes} min");
            if (Pressure())
                foreach (var entry in entries.Values.Where(Holding).Where(Evictable).ToArray())
                    if (now - Volatile.Read(ref entry.LastUsed) >= (long)PressureGrace.TotalMilliseconds)
                        await EvictAsync(entry, "the system is short of memory");
            while (true)
            {
                var holders = entries.Values.Where(Holding).ToArray();
                if (holders.Sum(MemoryFor) <= limits.BudgetBytes) break;
                var victim = holders.Where(Evictable).OrderBy(entry => Volatile.Read(ref entry.LastUsed)).FirstOrDefault();
                if (victim is null) break;
                await EvictAsync(victim, $"all language servers together exceeded the {Mib(limits.BudgetBytes)} memory budget");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            Volatile.Write(ref enforcing, 0);
            Pulse();
        }
    }

    /// <summary>How often memory is sampled and the limits enforced.</summary>
    internal static readonly TimeSpan EnforcementPeriod = TimeSpan.FromSeconds(15);

    /// <summary>Under memory pressure, servers used within this window are kept as the active set.</summary>
    internal static readonly TimeSpan PressureGrace = TimeSpan.FromMinutes(2);

    private static string Mib(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB";

    /// <summary>Every running server on this host with its workspace, memory and last use, plus the limits, for Settings › Code Intelligence.</summary>
    private LanguageResult Running()
    {
        var limits = Limits();
        var now = Ticks();
        var rows = new List<LanguageServerState>();
        long total = 0;
        foreach (var (key, entry) in entries.ToArray().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (Volatile.Read(ref entry.Stopped) != 0) continue;
            var memory = MemoryFor(entry);
            if (Holding(entry)) total += memory;
            var task = entry.Session.IsValueCreated ? entry.Session.Value : null;
            var state = task is { IsCompletedSuccessfully: true } done ? done.Result.Exited ? "error" : done.Result.IsIndexing ? "initializing" : "ready"
                : task is { IsFaulted: true } ? "error" : entry.State;
            var busy = Volatile.Read(ref entry.InFlight) > 0 ? "busy" : task is { IsCompletedSuccessfully: true } open && open.Result.HasEditors ? "editor open" : "idle";
            rows.Add(new(entry.ServerId, "", state, state == "ready" ? busy : entry.Detail, "")
            {
                Workspace = entry.Workspace,
                MemoryBytes = memory,
                IdleSeconds = Math.Max(0, (now - Volatile.Read(ref entry.LastUsed)) / 1000),
                StartedBy = entry.StartedBy
            });
        }
        var summary = new JsonObject
        {
            ["budgetBytes"] = limits.BudgetBytes,
            ["automaticBudget"] = limits.MemoryBudgetMb == 0,
            ["totalBytes"] = total,
            ["physicalBytes"] = LanguageMemory.PhysicalBytes,
            ["pressure"] = Pressure(),
            ["limits"] = JsonNode.Parse(limits.Serialize()),
            ["defaultCaps"] = new JsonObject(LanguageServerLimits.DefaultCaps.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
            ["defaultCap"] = LanguageServerLimits.DefaultCap
        };
        return new("ready", $"{rows.Count} running language servers using {Mib(total)} of the {Mib(limits.BudgetBytes)} budget.") { Servers = rows, Configuration = summary.ToJsonString() };
    }

    /// <summary>Stops one server the way the limits do: no stop is remembered and its next request starts it again.</summary>
    private async Task<LanguageResult> ReleaseAsync(string workspace, string server)
    {
        if (!entries.TryGetValue(workspace + "\0" + server, out var entry)) return new("stopped", "That language server is not running.", server);
        if (Volatile.Read(ref entry.InFlight) > 0) return new("busy", "That language server is answering a request; try again when it finishes.", server);
        await EvictAsync(entry, "a stop was requested in Settings");
        return new("stopped", "Stopped; the next request in that workspace starts it again.", server);
    }

    /// <summary>Stops a workspace's servers now, keeping its settings, drafts and exit records; used when its project closes.</summary>
    internal async Task StopWorkspaceAsync(string workspace, string reason)
    {
        workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        foreach (var (key, entry) in entries.ToArray())
            if (key.StartsWith(workspace + "\0", StringComparison.Ordinal) && entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
            {
                try { await PreserveDraftsAsync(key, entry, CancellationToken.None); }
                catch (Exception) { }
                finally { await StopAsync(entry, reason); }
            }
    }
}
