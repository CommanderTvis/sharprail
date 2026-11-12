using System.Collections;
using System.Diagnostics;
using System.Globalization;

namespace SharpRail.Checks;

/// <summary>Slice <c>Index</c> of <c>Total</c>, one-based, over the gate's cases in the order they are reached.</summary>
internal readonly record struct Shard(int Index, int Total)
{
    internal static readonly Shard Whole = new(1, 1);

    internal static Shard Parse(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total) || index < 1 || index > total)
            throw new ArgumentException($"Shard must be k/N with 1 <= k <= N, not '{text}'.");
        return new(index, total);
    }

    /// <summary>Lane <c>this</c> of a machine that owns <paramref name="job"/>: every job's lanes together cover each case once.</summary>
    internal Shard Within(Shard job) => new((job.Index - 1) * Total + Index, job.Total * Total);

    internal bool Owns(int ordinal) => ordinal % Total == Index - 1;

    public override string ToString() => $"{Index}/{Total}";
}

/// <summary>A case the previous run did not finish: it failed at <c>Ordinal</c>, leaving the rest of its shard unrun.</summary>
internal readonly record struct Unfinished(int Ordinal, Shard Shard, string Name, bool Exact = false)
{
    internal const string Setup = "(setup)";

    internal static Unfinished Parse(string line)
    {
        var parts = line.Split(' ', 3);
        return new(int.Parse(parts[0].TrimStart('!'), CultureInfo.InvariantCulture), Shard.Parse(parts[1]), parts[2], parts[0].StartsWith('!'));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{(Exact ? "!" : "")}{Ordinal} {Shard} {Name}");
}

/// <summary>Which cases one process runs: its shard, narrowed by named cases or by what the last run left unfinished.</summary>
internal sealed record Selection(Shard Shard, IReadOnlyList<Unfinished>? Repair = null, IReadOnlySet<string>? Only = null)
{
    internal bool Selects(string name, int ordinal)
        => Shard.Owns(ordinal) && Only?.Contains(name) != false && Repair?.Any(entry => (entry.Exact ? ordinal == entry.Ordinal : ordinal >= entry.Ordinal) && entry.Shard.Owns(ordinal)) != false;

    /// <summary>
    /// What a later repair run must cover after this one stopped at <paramref name="failed"/>: that case and
    /// whatever this process still owed behind it. A failure in shared setup leaves everything it owned.
    /// </summary>
    internal IReadOnlyList<Unfinished> Remaining((int Ordinal, string Name)? failed)
    {
        if (failed is not { } at) return Repair ?? [new(0, Shard, Unfinished.Setup)];
        return Repair is null
            ? [new(at.Ordinal, Shard, at.Name)]
            : [.. Repair.Select(entry => entry.Ordinal >= at.Ordinal ? entry : entry with { Ordinal = at.Ordinal, Name = at.Name })];
    }
}

/// <summary>
/// Names the gate's cases and decides which of them this process runs. Code outside a case is shared
/// setup and runs in every lane; a case runs in exactly one.
/// </summary>
internal static class Gate
{
    private static readonly HashSet<string> seen = [];
    private static readonly List<string> passed = [];
    private static Selection selection = new(Shard.Whole);
    private static bool listing;
    private static int ordinal;
    private static (int Ordinal, string Name)? running;
    private static int skipped;
    private static bool keepGoing;
    private static readonly List<CaseFailure> failures = [];
    private static readonly List<CaseTiming> timings = [];

    internal static IReadOnlyList<string> Passed => passed;
    internal static int Skipped => skipped;
    internal static string? Running => running?.Name;
    internal static IReadOnlyList<CaseFailure> Failures => failures;
    internal static IReadOnlyList<CaseTiming> Timings => timings;
    internal static bool KeepGoing => keepGoing;
    internal static Shard Shard => selection.Shard;

    /// <summary>What a repair run owes after an abort: the failing case and the rest of its shard, plus every case keep-going saw fail.</summary>
    internal static IReadOnlyList<Unfinished> Remaining => [.. selection.Remaining(running), .. Failed];

    /// <summary>The cases keep-going saw fail, each owed alone: the cases behind them ran.</summary>
    internal static IReadOnlyList<Unfinished> Failed => [.. failures.Select(failure => new Unfinished(failure.Ordinal, selection.Shard, failure.Name, Exact: true))];

    internal static void Configure(Selection cases, bool list, bool continueAfterFailure = false)
    {
        selection = cases; listing = list; keepGoing = continueAfterFailure;
    }

    internal static void Case(string name, Action body)
    {
        if (!seen.Add(name)) throw new InvalidOperationException("Duplicate gate case: " + name);
        var index = ordinal++;
        if (selection.Repair?.FirstOrDefault(entry => entry.Ordinal == index) is { Name: { } expected } && expected != Unfinished.Setup && expected != name)
            throw new InvalidOperationException($"The case list changed since the failed run ({expected} is now {name}); run the whole gate.");
        if (!selection.Selects(name, index)) { skipped++; return; }
        if (listing) { Console.WriteLine("CASE " + name); return; }
        running = (index, name);
        CiReport.BeginCase(name);
        var started = Stopwatch.GetTimestamp();
        if (!keepGoing)
        {
            body();
            CiReport.EndCase();
            timings.Add(new(name, Stopwatch.GetElapsedTime(started), false));
            running = null;
            passed.Add(name);
            return;
        }
        // Cases share the process, so a failed one must not leak its directory or environment into the next.
        var directory = Directory.GetCurrentDirectory();
        var environment = Environment.GetEnvironmentVariables();
        try
        {
            body();
            CiReport.EndCase();
            timings.Add(new(name, Stopwatch.GetElapsedTime(started), false));
            passed.Add(name);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            Console.Error.WriteLine(error);
            failures.Add(CiReport.Describe(name, index, error, elapsed));
            timings.Add(new(name, elapsed, true));
            CiReport.EndCase();
            Console.WriteLine($"FAILED {name}; continuing with the next case (keep-going)");
        }
        finally
        {
            Restore(directory, environment);
            running = null;
        }
    }

    private static void Restore(string directory, IDictionary environment)
    {
        try { if (Directory.GetCurrentDirectory() != directory) Directory.SetCurrentDirectory(directory); }
        catch (IOException) { }
        var now = Environment.GetEnvironmentVariables();
        foreach (var key in now.Keys.Cast<string>().Where(key => !environment.Contains(key)).ToArray()) Environment.SetEnvironmentVariable(key, null);
        foreach (var key in environment.Keys.Cast<string>())
            if (!Equals(now[key], environment[key])) Environment.SetEnvironmentVariable(key, (string?)environment[key]);
    }
}