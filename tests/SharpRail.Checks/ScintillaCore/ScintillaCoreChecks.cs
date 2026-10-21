namespace SharpRail.Checks.ScintillaCore;

/// <summary>
/// Ports of Scintilla 5.6.7 test/unit/test*.cxx for the managed editor (--scintilla-core). Each component owns its
/// suite file; this runner and <see cref="Expect"/> are shared. No Avalonia setup: suites exercise pure managed code,
/// except layout/view suites, which use SkiaSharp and HarfBuzzSharp directly.
/// </summary>
internal static class ScintillaCoreChecks
{
    private static readonly (string Name, Action Run)[] Suites =
    [
        ("SplitVector", SplitVectorChecks.Run),
        ("Partitioning", PartitioningChecks.Run),
        ("RunStyles", RunStylesChecks.Run),
        ("ContractionState", ContractionStateChecks.Run),
        ("Decoration", DecorationChecks.Run),
        ("PerLine", PerLineChecks.Run),
        ("CharClassify", CharClassifyChecks.Run),
        ("CellBuffer", CellBufferChecks.Run),
        ("Document", DocumentChecks.Run),
        ("Selection", SelectionChecks.Run),
    ];

    internal static void Run()
    {
        foreach (var (name, run) in Suites)
        {
            run();
            Console.WriteLine($"PASS scintilla-core {name}");
        }
    }
}

/// <summary>Catch2-style assertions for the ported suites.</summary>
internal static class Expect
{
    internal static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual}.");
    }

    internal static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}.");
    }
}