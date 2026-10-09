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
internal readonly record struct Unfinished(int Ordinal, Shard Shard, string Name)
{
    internal const string Setup = "(setup)";

    internal static Unfinished Parse(string line)
    {
        var parts = line.Split(' ', 3);
        return new(int.Parse(parts[0], CultureInfo.InvariantCulture), Shard.Parse(parts[1]), parts[2]);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Ordinal} {Shard} {Name}");
}

/// <summary>Which cases one process runs: its shard, narrowed by named cases or by what the last run left unfinished.</summary>
internal sealed record Selection(Shard Shard, IReadOnlyList<Unfinished>? Repair = null, IReadOnlySet<string>? Only = null)
{
    internal bool Selects(string name, int ordinal)
        => Shard.Owns(ordinal) && Only?.Contains(name) != false && Repair?.Any(entry => ordinal >= entry.Ordinal && entry.Shard.Owns(ordinal)) != false;

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

    internal static IReadOnlyList<string> Passed => passed;
    internal static IReadOnlyList<Unfinished> Remaining => selection.Remaining(running);

    internal static void Configure(Selection cases, bool list)
    {
        selection = cases; listing = list;
    }

    internal static void Case(string name, Action body)
    {
        if (!seen.Add(name)) throw new InvalidOperationException("Duplicate gate case: " + name);
        var index = ordinal++;
        if (selection.Repair?.FirstOrDefault(entry => entry.Ordinal == index) is { Name: { } expected } && expected != Unfinished.Setup && expected != name)
            throw new InvalidOperationException($"The case list changed since the failed run ({expected} is now {name}); run the whole gate.");
        if (!selection.Selects(name, index)) return;
        if (listing) { Console.WriteLine("CASE " + name); return; }
        running = (index, name);
        body();
        running = null;
        passed.Add(name);
    }
}