using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testRunStyles.cxx (copy/move compile checks have no C# counterpart).</summary>
internal static class RunStylesChecks
{
    internal static void Run()
    {
        var rs = new RunStyles<int>();
        Expect.Equal(0, rs.Length, "IsEmptyInitially length");
        Expect.Equal(1, rs.Runs, "IsEmptyInitially runs");

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 1);
        Expect.Equal(1, rs.Length, "SimpleInsert length");
        Expect.Equal(1, rs.Runs, "SimpleInsert runs");
        Expect.Equal(0, rs.ValueAt(0), "SimpleInsert value");
        Expect.Equal(1, rs.FindNextChange(0, rs.Length), "SimpleInsert change 0");
        Expect.Equal(2, rs.FindNextChange(1, rs.Length), "SimpleInsert change 1");

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 2);
        Expect.Equal(2, rs.Length, "TwoRuns length");
        Expect.Equal(1, rs.Runs, "TwoRuns runs");
        rs.SetValueAt(0, 2);
        Expect.Equal(2, rs.Runs, "TwoRuns runs after set");
        Expect.Equal(2, rs.ValueAt(0), "TwoRuns 0");
        Expect.Equal(0, rs.ValueAt(1), "TwoRuns 1");
        Expect.Equal(1, rs.FindNextChange(0, rs.Length), "TwoRuns change 0");
        Expect.Equal(2, rs.FindNextChange(1, rs.Length), "TwoRuns change 1");
        Expect.Equal(3, rs.FindNextChange(2, rs.Length), "TwoRuns change 2");

        LongerRuns();
        FillRanges();
        DeleteRange();
        Find();
        AllSame();
        FindWithReversion();
        FinalRunInversion();

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        rs.SetValueAt(0, 3);
        rs.SetValueAt(1, 3);
        rs.DeleteAll();
        Expect.Equal(0, rs.Length, "DeleteAll length");
        Expect.Equal(0, rs.ValueAt(0), "DeleteAll value");
        Expect.Equal(1, rs.Runs, "DeleteAll runs");

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 3);
        Expect.True(rs.FillRange(1, 99, 1).Changed, "DeleteSecond fill");
        Expect.Equal(3, rs.Length, "DeleteSecond length");
        Expect.Equal(3, rs.Runs, "DeleteSecond runs");
        rs.DeleteRange(1, 1);
        Expect.Equal(2, rs.Length, "DeleteSecond length after");
        Expect.Equal(1, rs.Runs, "DeleteSecond runs after");

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 2);
        Expect.True(rs.FillRange(1, 99, 1).Changed, "DeleteEndRun fill");
        Expect.Equal(2, rs.Length, "DeleteEndRun length");
        Expect.Equal(2, rs.Runs, "DeleteEndRun runs");
        Runs(rs, [(0, 0, 1), (1, 1, 2)], "DeleteEndRun");
        rs.DeleteRange(1, 1);
        Expect.Equal(1, rs.Length, "DeleteEndRun length after");
        Expect.Equal(1, rs.Runs, "DeleteEndRun runs after");
        Runs(rs, [(0, 0, 1), (1, 0, 1)], "DeleteEndRun after");
        rs.Check();

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 1);
        rs.FillRange(1, 99, 1);
        Expect.Equal(1, rs.Length, "OutsideBounds length");
        Expect.Equal(1, rs.Runs, "OutsideBounds runs");
        Runs(rs, [(0, 0, 1)], "OutsideBounds");
    }

    private static void Runs(RunStyles<int> rs, (int Position, int Start, int End)[] expected, string what)
    {
        foreach (var (position, start, end) in expected)
        {
            Expect.Equal(start, rs.StartRun(position), $"{what} StartRun({position})");
            Expect.Equal(end, rs.EndRun(position), $"{what} EndRun({position})");
        }
    }

    private static void LongerRuns()
    {
        var rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        rs.SetValueAt(0, 3);
        rs.SetValueAt(1, 3);
        Expect.Equal(3, rs.ValueAt(0), "LongerRuns 0");
        Expect.Equal(3, rs.ValueAt(1), "LongerRuns 1");
        Expect.Equal(0, rs.ValueAt(2), "LongerRuns 2");
        Expect.Equal(2, rs.Runs, "LongerRuns runs");
        Runs(rs, [(0, 0, 2), (1, 0, 2), (2, 2, 5), (3, 2, 5), (4, 2, 5), (5, 2, 5), (6, 2, 5)], "LongerRuns");
        Expect.Equal(2, rs.FindNextChange(0, rs.Length), "LongerRuns change 0");
        Expect.Equal(5, rs.FindNextChange(2, rs.Length), "LongerRuns change 2");
        Expect.Equal(6, rs.FindNextChange(5, rs.Length), "LongerRuns change 5");
    }

    private static void FillRanges()
    {
        var rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        Expect.Equal(new FillResult(true, 1, 3), rs.FillRange(1, 99, 3), "FillRange");
        for (int i = 0; i < 5; i++) Expect.Equal(i is >= 1 and <= 3 ? 99 : 0, rs.ValueAt(i), $"FillRange [{i}]");
        Runs(rs, [(0, 0, 1), (1, 1, 4)], "FillRange");

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        Expect.Equal(new FillResult(true, 1, 3), rs.FillRange(1, 99, 3), "FillRangeAlreadyFilled 1");
        Expect.Equal(new FillResult(false, 2, 1), rs.FillRange(2, 99, 1), "FillRangeAlreadyFilled 2");
        for (int i = 0; i < 5; i++) Expect.Equal(i is >= 1 and <= 3 ? 99 : 0, rs.ValueAt(i), $"FillRangeAlreadyFilled [{i}]");
        Expect.Equal(3, rs.Runs, "FillRangeAlreadyFilled runs");

        rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        Expect.Equal(new FillResult(true, 1, 2), rs.FillRange(1, 99, 2), "FillRangeAlreadyPartFilled 1");
        Expect.Equal(new FillResult(true, 3, 1), rs.FillRange(2, 99, 2), "FillRangeAlreadyPartFilled 2");
        Expect.Equal(3, rs.Runs, "FillRangeAlreadyPartFilled runs");
    }

    private static void DeleteRange()
    {
        var rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        rs.SetValueAt(0, 3);
        Expect.Equal(2, rs.Runs, "DeleteRange runs 1");
        rs.SetValueAt(1, 3);
        Expect.Equal(2, rs.Runs, "DeleteRange runs 2");
        rs.DeleteRange(1, 1);
        Expect.Equal(4, rs.Length, "DeleteRange length");
        Expect.Equal(2, rs.Runs, "DeleteRange runs 3");
        Expect.Equal(3, rs.ValueAt(0), "DeleteRange 0");
        Expect.Equal(0, rs.ValueAt(1), "DeleteRange 1");
        Runs(rs, [(0, 0, 1), (1, 1, 4), (2, 1, 4)], "DeleteRange");
    }

    private static void Find()
    {
        var rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        Expect.Equal(new FillResult(true, 1, 3), rs.FillRange(1, 99, 3), "Find fill");
        (int Value, int Start, int Expected)[] cases =
        [
            (0, 0, 0), (99, 0, 1), (3, 0, -1),
            (0, 1, 4), (99, 1, 1), (3, 1, -1),
            (0, 2, 4), (99, 2, 2), (3, 2, -1),
            (0, 4, 4), (99, 4, -1), (3, 4, -1),
            (0, 5, -1), (99, 5, -1), (3, 5, -1),
            (0, 6, -1), (99, 6, -1), (3, 6, -1),
        ];
        foreach (var (value, start, expected) in cases)
            Expect.Equal(expected, rs.Find(value, start), $"Find({value}, {start})");
    }

    private static void AllSame()
    {
        var rs = new RunStyles<int>();
        Expect.True(rs.AllSame(), "AllSame empty");
        rs.InsertSpace(0, 5);
        Expect.True(rs.AllSame(), "AllSame spaced");
        Expect.True(!rs.AllSameAs(88), "AllSameAs 88");
        Expect.True(rs.AllSameAs(0), "AllSameAs 0");
        Expect.True(rs.FillRange(1, 99, 3).Changed, "AllSame fill");
        Expect.True(!rs.AllSame(), "AllSame filled");
        Expect.True(!rs.AllSameAs(88), "AllSameAs 88 filled");
        Expect.True(!rs.AllSameAs(0), "AllSameAs 0 filled");
        Expect.True(rs.FillRange(1, 0, 3).Changed, "AllSame clear");
        Expect.True(rs.AllSame(), "AllSame cleared");
        Expect.True(!rs.AllSameAs(88), "AllSameAs 88 cleared");
        Expect.True(rs.AllSameAs(0), "AllSameAs 0 cleared");
    }

    private static void FindWithReversion()
    {
        var rs = new RunStyles<int>();
        rs.InsertSpace(0, 5);
        Expect.Equal(1, rs.Runs, "FindWithReversion runs 0");
        Expect.Equal(new FillResult(true, 1, 1), rs.FillRange(1, 99, 1), "FindWithReversion 1");
        Expect.Equal(3, rs.Runs, "FindWithReversion runs 1");
        Expect.Equal(new FillResult(true, 2, 1), rs.FillRange(2, 99, 1), "FindWithReversion 2");
        Expect.Equal(3, rs.Runs, "FindWithReversion runs 2");
        Expect.Equal(new FillResult(true, 1, 1), rs.FillRange(1, 0, 1), "FindWithReversion 3");
        Expect.Equal(3, rs.Runs, "FindWithReversion runs 3");
        Expect.Equal(new FillResult(true, 2, 1), rs.FillRange(2, 0, 1), "FindWithReversion 4");
        Expect.Equal(1, rs.Runs, "FindWithReversion runs 4");
        Expect.Equal(-1, rs.Find(0, 6), "FindWithReversion find");
    }

    private static void FinalRunInversion()
    {
        var rs = new RunStyles<int>();
        Expect.Equal(1, rs.Runs, "FinalRunInversion 0");
        rs.InsertSpace(0, 1);
        Expect.Equal(1, rs.Runs, "FinalRunInversion 1");
        rs.SetValueAt(0, 1);
        Expect.Equal(1, rs.Runs, "FinalRunInversion 2");
        rs.InsertSpace(1, 1);
        Expect.Equal(1, rs.Runs, "FinalRunInversion 3");
        rs.SetValueAt(1, 1);
        Expect.Equal(1, rs.Runs, "FinalRunInversion 4");
        rs.SetValueAt(1, 0);
        Expect.Equal(2, rs.Runs, "FinalRunInversion 5");
        rs.SetValueAt(1, 1);
        Expect.Equal(1, rs.Runs, "FinalRunInversion 6");
    }
}