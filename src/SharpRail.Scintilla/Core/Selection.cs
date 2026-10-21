namespace SharpRail.Scintilla.Core;

/// <summary>A position plus virtual space (upstream SelectionPosition). <see cref="Invalid"/> is position -1.</summary>
internal record struct SelectionPosition(int Position, int VirtualSpace = 0) : IComparable<SelectionPosition>
{
    internal static readonly SelectionPosition Invalid = new(-1);

    internal readonly bool IsValid => VirtualSpace != -1 && Position != -1;

    internal void Reset() => (Position, VirtualSpace) = (0, 0);

    /// <summary>Sets the position and clears virtual space.</summary>
    internal void SetPosition(int position) => (Position, VirtualSpace) = (position, 0);

    internal void Add(int increment) => Position += increment;

    internal void AddVirtualSpace(int increment) => VirtualSpace += increment;

    internal void MoveForInsertDelete(bool insertion, int startChange, int length, bool moveForEqual)
    {
        if (insertion)
        {
            if (Position == startChange)
            {
                // Always consume virtual space.
                int virtualLengthRemove = Math.Min(length, VirtualSpace);
                VirtualSpace -= virtualLengthRemove;
                Position += virtualLengthRemove;
                if (moveForEqual) Position += length - virtualLengthRemove;
            }
            else if (Position > startChange)
                Position += length;
        }
        else
        {
            if (Position == startChange) VirtualSpace = 0;
            if (Position > startChange)
            {
                if (Position > startChange + length)
                    Position -= length;
                else
                {
                    Position = startChange;
                    VirtualSpace = 0;
                }
            }
        }
    }

    public readonly int CompareTo(SelectionPosition other) =>
        Position != other.Position ? Position.CompareTo(other.Position) : VirtualSpace.CompareTo(other.VirtualSpace);

    public static bool operator <(SelectionPosition a, SelectionPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(SelectionPosition a, SelectionPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(SelectionPosition a, SelectionPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SelectionPosition a, SelectionPosition b) => a.CompareTo(b) >= 0;

    internal static SelectionPosition Min(SelectionPosition a, SelectionPosition b) => a < b ? a : b;
    internal static SelectionPosition Max(SelectionPosition a, SelectionPosition b) => a > b ? a : b;
}

/// <summary>An ordered pair of positions (upstream SelectionSegment); the default is invalid at both ends.</summary>
internal readonly record struct SelectionSegment
{
    internal SelectionSegment(SelectionPosition a, SelectionPosition b) =>
        (Start, End) = a < b ? (a, b) : (b, a);

    internal SelectionSegment(int a, int b) : this(new SelectionPosition(a), new SelectionPosition(b)) { }

    public SelectionSegment() : this(SelectionPosition.Invalid, SelectionPosition.Invalid) { }

    internal SelectionPosition Start { get; }
    internal SelectionPosition End { get; }
    internal bool Empty => Start == End;
    internal int Length => End.Position - Start.Position;
}

/// <summary>Caret and anchor of a stream selection (upstream SelectionRange). The default is invalid.</summary>
internal record struct SelectionRange
{
    public SelectionRange() : this(SelectionPosition.Invalid, SelectionPosition.Invalid) { }

    internal SelectionRange(SelectionPosition caret, SelectionPosition anchor) => (Caret, Anchor) = (caret, anchor);

    internal SelectionRange(SelectionPosition single) : this(single, single) { }

    internal SelectionRange(int caret, int anchor) : this(new SelectionPosition(caret), new SelectionPosition(anchor)) { }

    internal SelectionRange(int single) : this(single, single) { }

    internal SelectionPosition Caret;
    internal SelectionPosition Anchor;

    internal readonly bool Empty => Anchor == Caret;

    internal readonly int Length => Math.Abs(Caret.Position - Anchor.Position);

    internal readonly SelectionPosition Start => Anchor < Caret ? Anchor : Caret;

    internal readonly SelectionPosition End => Anchor < Caret ? Caret : Anchor;

    internal readonly SelectionSegment AsSegment() => new(Caret, Anchor);

    internal void Reset()
    {
        Anchor.Reset();
        Caret.Reset();
    }

    internal readonly bool Contains(int position) => Anchor.Position > Caret.Position
        ? position >= Caret.Position && position <= Anchor.Position
        : position >= Anchor.Position && position <= Caret.Position;

    internal readonly bool ContainsCharacter(int position) => Anchor.Position > Caret.Position
        ? position >= Caret.Position && position < Anchor.Position
        : position >= Anchor.Position && position < Caret.Position;

    /// <summary>The part of <paramref name="check"/> inside this range; an invalid segment when they do not touch.</summary>
    internal readonly SelectionSegment Intersect(SelectionSegment check)
    {
        var inOrder = AsSegment();
        if (inOrder.Start > check.End || inOrder.End < check.Start) return new SelectionSegment();
        return new SelectionSegment(SelectionPosition.Max(check.Start, inOrder.Start),
            SelectionPosition.Min(check.End, inOrder.End));
    }

    internal void StartSet(SelectionPosition sp)
    {
        if (Anchor <= Caret)
        {
            Anchor = sp;
            if (Caret < Anchor) Caret = Anchor;
        }
        else
        {
            Caret = sp;
            if (Anchor < Caret) Anchor = Caret;
        }
    }

    internal void EndSet(SelectionPosition sp)
    {
        if (Caret >= Anchor)
        {
            Caret = sp;
            if (Anchor > Caret) Anchor = Caret;
        }
        else
        {
            Anchor = sp;
            if (Caret > Anchor) Caret = Anchor;
        }
    }

    /// <summary>Moves both ends for a document change; an insertion at the start of a non-empty selection moves
    /// both ends so the selected text is preserved.</summary>
    internal void MoveForInsertDelete(bool insertion, int startChange, int length)
    {
        bool caretStart = Caret.Position < Anchor.Position;
        bool anchorStart = Anchor.Position < Caret.Position;
        Caret.MoveForInsertDelete(insertion, startChange, length, caretStart);
        Anchor.MoveForInsertDelete(insertion, startChange, length, anchorStart);
    }
}

/// <summary>Single stream selection with anchor and caret (upstream src/Selection.h/.cxx, main range only).</summary>
/// <remarks>Multiple, rectangular and thin selections are not used by SharpRail and are not ported.</remarks>
internal sealed class Selection
{
    private SelectionRange range = new(0);

    internal SelectionRange RangeMain => range;

    internal int Count => 1;

    internal SelectionPosition MainCaret => range.Caret;

    internal SelectionPosition MainAnchor => range.Anchor;

    internal bool Empty => range.Empty;

    internal SelectionPosition Start => range.Start;

    internal SelectionPosition End => range.End;

    internal int Length => range.Length;

    internal void Clear() => range = new SelectionRange(range.Caret);

    internal void SetSelection(SelectionRange newRange) => range = newRange;

    internal void SetMain(int caret, int anchor) => range = new SelectionRange(caret, anchor);

    internal void MovePositions(bool insertion, int startChange, int length) =>
        range.MoveForInsertDelete(insertion, startChange, length);
}