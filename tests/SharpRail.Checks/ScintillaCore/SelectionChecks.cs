using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testSelection.cxx (single selection). Not ported: serialization and
/// multiple/rectangular selection (not used).</summary>
internal static class SelectionChecks
{
    private static readonly SelectionPosition Invalid = SelectionPosition.Invalid;
    private static readonly SelectionPosition Zero = new(0);

    internal static void Run()
    {
        PositionBasics();
        Comparison();
        Add();
        MoveForInsertDelete();
        Segment();
        Range();
        Intersect();
        StartEndSet();
        SelectionMain();
    }

    private static void PositionBasics()
    {
        var sel = SelectionPosition.Invalid;
        Expect.Equal(-1, sel.Position, "Invalid position");
        Expect.Equal(0, sel.VirtualSpace, "Invalid virtual space");
        Expect.True(!sel.IsValid, "Invalid !IsValid");
        Expect.True(sel == Invalid && sel != Zero, "Invalid comparisons");
        sel.Reset();
        Expect.True(sel != Invalid && sel == Zero, "Reset to zero");
    }

    private static void Comparison()
    {
        var sel = new SelectionPosition(2, 3);
        Expect.True(sel > Invalid && sel > Zero && sel >= Zero && Zero < sel && Zero <= sel, "Comparison");
        var virtuous = new SelectionPosition(0, 4);
        Expect.True(virtuous > Zero && virtuous >= Zero && Zero < virtuous && Zero <= virtuous, "Virtual comparison");
        Expect.Equal(0, virtuous.Position, "Virtual position");
        Expect.Equal(4, virtuous.VirtualSpace, "Virtual space");
        virtuous.SetPosition(1); // Also resets virtual space.
        Expect.Equal(new SelectionPosition(1, 0), virtuous, "SetPosition");
        virtuous.VirtualSpace = 3; // Does not reset position.
        Expect.Equal(new SelectionPosition(1, 3), virtuous, "SetVirtualSpace");
    }

    private static void Add()
    {
        var sel = new SelectionPosition(2, 3);
        sel.Add(1);
        Expect.Equal(new SelectionPosition(3, 3), sel, "Add");
        sel.AddVirtualSpace(2);
        Expect.Equal(new SelectionPosition(3, 5), sel, "AddVirtualSpace");
    }

    private static void MoveForInsertDelete()
    {
        var sel = new SelectionPosition(2, 3);
        sel.MoveForInsertDelete(true, 0, 1, false);
        Expect.Equal(new SelectionPosition(3, 3), sel, "Insert before");
        // Converts a virtual space to real space.
        sel.MoveForInsertDelete(true, 3, 1, false);
        Expect.Equal(new SelectionPosition(4, 2), sel, "Insert consumes virtual space");
        // Deletion at position clears virtual space.
        sel.MoveForInsertDelete(false, 4, 1, false);
        Expect.Equal(new SelectionPosition(4, 0), sel, "Delete at position");
        sel.MoveForInsertDelete(false, 3, 1, false);
        Expect.Equal(new SelectionPosition(3, 0), sel, "Delete before");
        // Insert at position with and without move for equal.
        sel.MoveForInsertDelete(true, 3, 1, false);
        Expect.Equal(new SelectionPosition(3, 0), sel, "Insert at position");
        sel.MoveForInsertDelete(true, 3, 1, true);
        Expect.Equal(new SelectionPosition(4, 0), sel, "Insert at position moveForEqual");
        // Deletion over the position moves to start of deletion.
        sel.MoveForInsertDelete(false, 2, 5, false);
        Expect.Equal(new SelectionPosition(2, 0), sel, "Delete over");
    }

    private static void Segment()
    {
        var ss = new SelectionSegment();
        Expect.True(ss.Start == Invalid && ss.End == Invalid, "Default segment invalid");
    }

    private static void Range()
    {
        var sr = new SelectionRange();
        Expect.True(sr.Anchor == Invalid && sr.Caret == Invalid, "Default range invalid");

        // Not upstream: range-level MoveForInsertDelete keeps an insertion at the start outside the selection.
        var range = new SelectionRange(5, 2);
        range.MoveForInsertDelete(true, 2, 3);
        Expect.Equal(new SelectionRange(8, 5), range, "Insertion at start moves both ends");
        Expect.True(range.Contains(5) && range.Contains(8) && !range.ContainsCharacter(8), "Contains");
    }

    private static void Intersect()
    {
        var segmentEmpty = new SelectionSegment();
        // Range from 1 to 2 with 3 virtual spaces.
        var range123 = new SelectionRange(new SelectionPosition(2, 3), new SelectionPosition(1));
        var segment12 = new SelectionSegment(1, 2);
        Expect.Equal(segment12, range123.Intersect(segment12), "Inside");
        var segment121 = new SelectionSegment(new SelectionPosition(1), new SelectionPosition(2, 1));
        Expect.Equal(segment121, range123.Intersect(segment121), "With virtual");
        var segment052 = new SelectionSegment(new SelectionPosition(0), new SelectionPosition(5, 2));
        Expect.Equal(range123.AsSegment(), range123.Intersect(segment052), "All inside");
        var wayOut = new SelectionSegment(new SelectionPosition(100), new SelectionPosition(105, 2));
        Expect.Equal(segmentEmpty, range123.Intersect(wayOut), "Nothing");
        var edge = new SelectionSegment(1, 1);
        Expect.Equal(edge, range123.Intersect(edge), "Edge");
        // (0, 1) and (1, 2v3) touch so the intersection is a single position.
        Expect.Equal(new SelectionSegment(1, 1), range123.Intersect(new SelectionSegment(0, 1)), "Touching");
    }

    private static void ExpectRange(SelectionRange range, int start, int end, SelectionRange expected, string what)
    {
        Expect.Equal(new SelectionPosition(start), range.Start, what + " start");
        Expect.Equal(new SelectionPosition(end), range.End, what + " end");
        Expect.Equal(expected, range, what);
    }

    private static void StartEndSet()
    {
        var range = new SelectionRange();
        range.StartSet(new SelectionPosition(2));
        range.EndSet(new SelectionPosition(3));
        ExpectRange(range, 2, 3, new SelectionRange(3, 2), "Set from invalid");
        range.StartSet(new SelectionPosition(1));
        ExpectRange(range, 1, 3, new SelectionRange(3, 1), "StartSet");

        range = new SelectionRange(2, 1);
        range.StartSet(new SelectionPosition(3));
        ExpectRange(range, 3, 3, new SelectionRange(3, 3), "StartSet outside after");

        range = new SelectionRange(2, 1);
        range.EndSet(new SelectionPosition(3));
        ExpectRange(range, 1, 3, new SelectionRange(3, 1), "EndSet outside after");

        range = new SelectionRange(2, 1);
        range.StartSet(new SelectionPosition(0));
        ExpectRange(range, 0, 2, new SelectionRange(2, 0), "StartSet outside before");

        range = new SelectionRange(2, 1);
        range.EndSet(new SelectionPosition(0));
        ExpectRange(range, 0, 0, new SelectionRange(0, 0), "EndSet outside before");

        range = new SelectionRange(3, 1);
        range.EndSet(new SelectionPosition(2));
        ExpectRange(range, 1, 2, new SelectionRange(2, 1), "EndSet inside");

        range = new SelectionRange(3, 1);
        range.StartSet(new SelectionPosition(2));
        ExpectRange(range, 2, 3, new SelectionRange(3, 2), "StartSet inside");

        range = new SelectionRange(2);
        range.StartSet(new SelectionPosition(9));
        ExpectRange(range, 9, 9, new SelectionRange(9, 9), "Empty StartSet after");

        range = new SelectionRange(2);
        range.StartSet(new SelectionPosition(0));
        ExpectRange(range, 0, 2, new SelectionRange(2, 0), "Empty StartSet before");
    }

    private static void SelectionMain()
    {
        var sel = new SharpRail.Scintilla.Core.Selection();
        var rangeZero = new SelectionRange(0);
        Expect.Equal(1, sel.Count, "Selection count");
        Expect.Equal(rangeZero, sel.RangeMain, "Selection main range");
        Expect.True(sel.Empty, "Selection empty");

        // Not upstream: single-range operations used by the editor.
        sel.SetMain(7, 3);
        Expect.True(!sel.Empty && sel.Start.Position == 3 && sel.End.Position == 7 && sel.Length == 4, "SetMain");
        sel.MovePositions(false, 0, 2);
        Expect.Equal(new SelectionRange(5, 1), sel.RangeMain, "MovePositions deletion");
        sel.Clear();
        Expect.Equal(new SelectionRange(5), sel.RangeMain, "Clear keeps caret");
    }
}