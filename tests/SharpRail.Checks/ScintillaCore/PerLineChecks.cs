using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of the LineAnnotation cases of Scintilla test/unit/testPerLine.cxx. Markers, levels, state and
/// tabstops are not used by the managed editor; SetStyles (per-character annotation styles) is not ported.</summary>
internal static class PerLineChecks
{
    internal static void Run()
    {
        var la = new LineAnnotation();
        Expect.Equal(0, la.Length(0), "Initial length");
        Expect.Equal(0, la.Lines(0), "Initial lines");
        Expect.Equal(0, la.Style(0), "Initial style");

        la = new LineAnnotation();
        la.SetText(0, "Text");
        Expect.Equal(4, la.Length(0), "SetText length");
        Expect.Equal(1, la.Lines(0), "SetText lines");
        Expect.Equal("Text", la.Text(0), "SetText text");
        Expect.Equal(0, la.Style(0), "SetText style");
        la.SetStyle(0, 9);
        Expect.Equal(9, la.Style(0), "SetStyle");
        la.SetText(0, "Ant\nBird\nCat");
        Expect.Equal(3, la.Lines(0), "SetText multi-line");
        Expect.Equal(9, la.Style(0), "SetText keeps style");
        la.ClearAll();
        Expect.Equal(null, la.Text(0), "ClearAll");

        la = new LineAnnotation();
        la.SetText(0, "Ant");
        la.SetText(1, "Bird");
        Expect.Equal(3, la.Length(0), "InsertRemoveLine 0");
        Expect.Equal(4, la.Length(1), "InsertRemoveLine 1");
        Expect.Equal(1, la.Lines(0), "InsertRemoveLine lines");
        la.InsertLine(1);
        Expect.Equal(3, la.Length(0), "InsertLine 0");
        Expect.Equal(0, la.Length(1), "InsertLine 1");
        Expect.Equal(4, la.Length(2), "InsertLine 2");
        la.RemoveLine(2);
        Expect.Equal(3, la.Length(0), "RemoveLine 0");
        Expect.Equal(4, la.Length(1), "RemoveLine 1");
        la.InsertLines(1, 2);
        Expect.Equal(3, la.Length(0), "InsertLines 0");
        Expect.Equal(0, la.Length(1), "InsertLines 1");
        Expect.Equal(0, la.Length(2), "InsertLines 2");
        Expect.Equal(4, la.Length(3), "InsertLines 3");

        la.SetText(3, null);
        Expect.Equal(null, la.Text(3), "SetText null clears");
        la.SetText(0, "é");
        Expect.Equal(2, la.Length(0), "Length counts UTF-8 bytes");
    }
}