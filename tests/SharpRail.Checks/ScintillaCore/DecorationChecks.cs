using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testDecoration.cxx.</summary>
internal static class DecorationChecks
{
    private const int Indicator = 4;

    internal static void Run()
    {
        Expect.Equal(Indicator, new Decoration(Indicator).Indicator, "Decoration HasCorrectIndicator");

        var deco = new Decoration(Indicator);
        Expect.Equal(0, deco.Length, "Decoration IsEmptyInitially length");
        Expect.Equal(1, deco.Runs.Runs, "Decoration IsEmptyInitially runs");
        Expect.True(deco.Empty, "Decoration IsEmptyInitially empty");

        deco = new Decoration(Indicator);
        deco.InsertSpace(0, 1);
        Expect.True(deco.Empty, "Decoration SimpleSpace");

        deco = new Decoration(Indicator);
        deco.InsertSpace(0, 1);
        deco.SetValueAt(0, 2);
        Expect.True(!deco.Empty, "Decoration SimpleRun");

        var decol = new DecorationList { CurrentIndicator = Indicator };
        Expect.Equal(Indicator, decol.CurrentIndicator, "DecorationList HasCorrectIndicator");

        decol = new DecorationList { CurrentValue = 55 };
        Expect.Equal(55, decol.CurrentValue, "DecorationList HasCorrectCurrentValue");

        decol = new DecorationList { CurrentIndicator = Indicator };
        decol.InsertSpace(0, 9);
        const int value = 59;
        var fr = decol.FillRange(4, value, 3);
        Expect.Equal(new FillResult(true, 4, 3), fr, "ExpandSetValues fill");
        Expect.Equal(value, decol.ValueAt(Indicator, 5), "ExpandSetValues ValueAt");
        Expect.Equal(1 << Indicator, decol.AllOnFor(5), "ExpandSetValues AllOnFor");
        Expect.Equal(4, decol.Start(Indicator, 5), "ExpandSetValues Start");
        Expect.Equal(7, decol.End(Indicator, 5), "ExpandSetValues End");
        const int indicatorB = 6;
        decol.CurrentIndicator = indicatorB;
        Expect.True(decol.FillRange(4, value, 3).Changed, "ExpandSetValues fill B");
        Expect.Equal((1 << Indicator) | (1 << indicatorB), decol.AllOnFor(5), "ExpandSetValues AllOnFor B");
        decol.DeleteRange(5, 1);
        Expect.Equal(4, decol.Start(indicatorB, 5), "ExpandSetValues Start B");
        Expect.Equal(6, decol.End(indicatorB, 5), "ExpandSetValues End B");
        Expect.Equal(2, decol.View.Count, "ExpandSetValues view");

        decol.FillRange(0, 0, decol.View[1].Length);
        Expect.Equal(1, decol.View.Count, "Clearing an indicator removes it");
        Expect.Equal(0, decol.ValueAt(indicatorB, 5), "Cleared indicator value");
    }
}