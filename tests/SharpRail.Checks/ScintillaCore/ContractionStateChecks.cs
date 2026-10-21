using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testContractionState.cxx. ShowHide, Hidden, Hide All, Contracting, ExpandAll
/// and SetFoldDisplayText test folding, which the managed editor does not have; height cases are extended to check
/// the display mapping that wrapping relies on.</summary>
internal static class ContractionStateChecks
{
    internal static void Run()
    {
        var cs = new ContractionState();
        Expect.Equal(1, cs.LinesInDoc, "IsEmptyInitially lines");
        Expect.Equal(1, cs.LinesDisplayed, "IsEmptyInitially displayed");
        Expect.Equal(0, cs.DisplayFromDoc(0), "IsEmptyInitially DisplayFromDoc");
        Expect.Equal(0, cs.DocFromDisplay(0), "IsEmptyInitially DocFromDisplay");

        cs = new ContractionState();
        cs.InsertLines(0, 1);
        Expect.Equal(2, cs.LinesInDoc, "OneLine lines");
        Expect.Equal(2, cs.LinesDisplayed, "OneLine displayed");
        Expect.Equal(0, cs.DisplayFromDoc(0), "OneLine DisplayFromDoc(0)");
        Expect.Equal(0, cs.DocFromDisplay(0), "OneLine DocFromDisplay(0)");
        Expect.Equal(1, cs.DisplayFromDoc(1), "OneLine DisplayFromDoc(1)");
        Expect.Equal(1, cs.DocFromDisplay(1), "OneLine DocFromDisplay(1)");

        cs = new ContractionState();
        cs.InsertLines(0, 4);
        Expect.Equal(5, cs.LinesInDoc, "InsertionThenDeletions lines");
        Expect.Equal(5, cs.LinesDisplayed, "InsertionThenDeletions displayed");
        for (int l = 0; l < 4; l++)
        {
            Expect.Equal(l, cs.DisplayFromDoc(l), $"InsertionThenDeletions DisplayFromDoc({l})");
            Expect.Equal(l, cs.DocFromDisplay(l), $"InsertionThenDeletions DocFromDisplay({l})");
        }
        cs.DeleteLines(0, 2);
        Expect.Equal(3, cs.LinesInDoc, "InsertionThenDeletions lines after");
        Expect.Equal(3, cs.LinesDisplayed, "InsertionThenDeletions displayed after");
        for (int l = 0; l < 2; l++)
        {
            Expect.Equal(l, cs.DisplayFromDoc(l), $"InsertionThenDeletions after DisplayFromDoc({l})");
            Expect.Equal(l, cs.DocFromDisplay(l), $"InsertionThenDeletions after DocFromDisplay({l})");
        }

        cs = new ContractionState();
        cs.InsertLines(0, 4);
        for (int l = 0; l < 4; l++) Expect.Equal(1, cs.GetHeight(l), $"ChangeHeight initial {l}");
        Expect.True(cs.SetHeight(1, 2), "ChangeHeight changed");
        Expect.True(!cs.SetHeight(1, 2), "ChangeHeight unchanged");
        Expect.Equal(1, cs.GetHeight(0), "ChangeHeight 0");
        Expect.Equal(2, cs.GetHeight(1), "ChangeHeight 1");
        Expect.Equal(1, cs.GetHeight(2), "ChangeHeight 2");
        cs.Check();

        HeightMapping();
    }

    private static void HeightMapping()
    {
        var cs = new ContractionState();
        cs.InsertLines(0, 3);
        cs.SetHeight(1, 3);
        Expect.Equal(6, cs.LinesDisplayed, "HeightMapping displayed");
        Expect.Equal(1, cs.DisplayFromDoc(1), "HeightMapping DisplayFromDoc(1)");
        Expect.Equal(3, cs.DisplayLastFromDoc(1), "HeightMapping DisplayLastFromDoc(1)");
        Expect.Equal(4, cs.DisplayFromDoc(2), "HeightMapping DisplayFromDoc(2)");
        Expect.Equal(2, cs.DisplayFromDocSub(1, 1), "HeightMapping DisplayFromDocSub(1, 1)");
        Expect.Equal(3, cs.DisplayFromDocSub(1, 7), "HeightMapping DisplayFromDocSub(1, 7)");
        int[] docs = [0, 1, 1, 1, 2, 3];
        for (int v = 0; v < docs.Length; v++) Expect.Equal(docs[v], cs.DocFromDisplay(v), $"HeightMapping DocFromDisplay({v})");
        Expect.Equal(cs.LinesInDoc, cs.DocFromDisplay(99), "HeightMapping DocFromDisplay past end");
        cs.InsertLines(1, 1);
        Expect.Equal(7, cs.LinesDisplayed, "HeightMapping displayed after insert");
        Expect.Equal(3, cs.GetHeight(2), "HeightMapping moved height");
        cs.DeleteLines(2, 1);
        Expect.Equal(4, cs.LinesDisplayed, "HeightMapping displayed after delete");
        cs.Check();
        cs.Clear();
        Expect.Equal(1, cs.LinesInDoc, "HeightMapping cleared");
    }
}