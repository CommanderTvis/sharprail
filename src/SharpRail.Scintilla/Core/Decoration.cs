namespace SharpRail.Scintilla.Core;

/// <summary>One indicator's values over the document (upstream src/Decoration.cxx Decoration).</summary>
internal sealed class Decoration(int indicator)
{
    internal RunStyles<int> Runs { get; } = new();

    internal int Indicator => indicator;

    internal bool Empty => Runs.Runs == 1 && Runs.AllSameAs(0);

    internal int Length => Runs.Length;

    internal int ValueAt(int position) => Runs.ValueAt(position);

    internal int StartRun(int position) => Runs.StartRun(position);

    internal int EndRun(int position) => Runs.EndRun(position);

    internal void SetValueAt(int position, int value) => Runs.SetValueAt(position, value);

    internal void InsertSpace(int position, int insertLength) => Runs.InsertSpace(position, insertLength);
}

/// <summary>Indicator values per position, one <see cref="Decoration"/> per non-empty indicator, sorted by
/// indicator number (upstream src/Decoration.cxx DecorationList).</summary>
internal sealed class DecorationList
{
    private const int IndicatorIme = 32;

    private readonly List<Decoration> decorations = [];
    private Decoration? current;
    private int currentIndicator;
    private int currentValue = 1;
    private int lengthDocument;

    /// <summary>Non-empty decorations in indicator order, for painting.</summary>
    internal IReadOnlyList<Decoration> View => decorations;

    internal int CurrentIndicator
    {
        get => currentIndicator;
        set
        {
            currentIndicator = value;
            current = DecorationFromIndicator(value);
            currentValue = 1;
        }
    }

    internal int CurrentValue
    {
        get => currentValue;
        set => currentValue = value != 0 ? value : 1;
    }

    private Decoration? DecorationFromIndicator(int indicator)
    {
        foreach (var deco in decorations)
            if (deco.Indicator == indicator) return deco;
        return null;
    }

    private Decoration Create(int indicator, int length)
    {
        currentIndicator = indicator;
        var deco = new Decoration(indicator);
        deco.InsertSpace(0, length);
        int index = decorations.FindIndex(d => d.Indicator >= indicator);
        decorations.Insert(index < 0 ? decorations.Count : index, deco);
        return deco;
    }

    private void Delete(int indicator)
    {
        decorations.RemoveAll(d => d.Indicator == indicator);
        current = null;
    }

    /// <summary>Fills the current indicator with <paramref name="value"/> (0 clears).</summary>
    internal FillResult FillRange(int position, int value, int fillLength)
    {
        current ??= DecorationFromIndicator(currentIndicator) ?? Create(currentIndicator, lengthDocument);
        var result = current.Runs.FillRange(position, value, fillLength);
        if (current.Empty) Delete(currentIndicator);
        return result;
    }

    internal void InsertSpace(int position, int insertLength)
    {
        bool atEnd = position == lengthDocument;
        lengthDocument += insertLength;
        foreach (var deco in decorations)
        {
            deco.Runs.InsertSpace(position, insertLength);
            if (atEnd) deco.Runs.FillRange(position, 0, insertLength);
        }
    }

    internal void DeleteRange(int position, int deleteLength)
    {
        lengthDocument -= deleteLength;
        foreach (var deco in decorations) deco.Runs.DeleteRange(position, deleteLength);
        int before = decorations.Count;
        if (lengthDocument == 0) decorations.Clear();
        else decorations.RemoveAll(d => d.Empty);
        if (decorations.Count != before) current = null;
    }

    /// <summary>Bit mask of indicators below 32 that are set at <paramref name="position"/>.</summary>
    internal int AllOnFor(int position)
    {
        int mask = 0;
        foreach (var deco in decorations)
            if (deco.ValueAt(position) != 0 && deco.Indicator < IndicatorIme) mask |= 1 << deco.Indicator;
        return mask;
    }

    internal int ValueAt(int indicator, int position) => DecorationFromIndicator(indicator)?.ValueAt(position) ?? 0;

    internal int Start(int indicator, int position) => DecorationFromIndicator(indicator)?.StartRun(position) ?? 0;

    internal int End(int indicator, int position) => DecorationFromIndicator(indicator)?.EndRun(position) ?? 0;
}