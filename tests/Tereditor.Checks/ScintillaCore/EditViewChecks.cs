using SharpRail.Scintilla;
using SharpRail.Scintilla.Core;
using SharpRail.Scintilla.View;

using SkiaSharp;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Managed-only checks (no upstream suite): line layout, wrapping, hit testing and a paint smoke test.</summary>
internal static class EditViewChecks
{
    internal static void Run()
    {
        using var shaper = new LineShaper(SKTypeface.Default);
        shaper.Direction = ScintillaTextDirection.LeftToRight;
        var style = new ViewStyle();
        var view = new EditView(shaper);
        var document = new Document();
        document.InsertString(0, "alpha beta gamma delta epsilon zeta eta theta\nshort\n\ta\tb\nx\u0001y\n");

        Expect.Equal(1, view.WrapLine(document, style, 0, float.PositiveInfinity), "unbounded line is one row");
        var full = view.LineWidth(document, style, 0);
        Expect.True(full > view.LineWidth(document, style, 1), "line width grows with text");

        var narrow = view.WrapLine(document, style, 0, full / 3);
        Expect.True(narrow >= 3, $"narrow wrap rows ({narrow})");
        Expect.True(view.WrapLine(document, style, 0, full / 6) > narrow, "narrower wrap has more rows");
        Expect.Equal(1, view.WrapLine(document, style, 1, full / 3), "short line does not wrap");
        Expect.True(view.LineWidth(document, style, 0) <= full / 3 + 0.5f, "wrapped rows fit the width");

        // Word-boundary breaking: every row after the first starts at a word start.
        var width = full / 3;
        var lineStart = document.LineStart(0);
        for (var sub = 1; sub < narrow; sub++)
        {
            var position = view.PositionInLine(document, style, 0, sub, 0, true, width);
            Expect.True(position > lineStart && document.CharAt(position - 1) == (byte)' ', $"row {sub} starts after a space");
            var location = view.LocationInLine(document, style, position, width);
            Expect.Equal(0f, location.X, $"row {sub} start x");
            Expect.True(location.Y > 0, $"row {sub} start y");
        }

        // Round trips across sub-lines.
        var lineHeight = style.LineHeight;
        Expect.True(lineHeight > 0, "metrics derived");
        for (var position = lineStart; position < document.LineEnd(0); position++)
        {
            var location = view.LocationInLine(document, style, position, width);
            var sub = (int)MathF.Round(location.Y / lineHeight);
            Expect.Equal(position, view.PositionInLine(document, style, 0, sub, location.X + 0.01f, false, width), $"round trip {position}");
        }
        var end = view.LocationInLine(document, style, document.LineEnd(0), width);
        Expect.Equal(narrow - 1, (int)MathF.Round(end.Y / lineHeight), "line end on the last row");

        // A character that cannot fit anywhere still makes progress, one cluster per row at least.
        Expect.True(view.WrapLine(document, style, 1, 1) >= 5, "tiny width wraps per cluster");

        // Tab stops at multiples of TabWidth spaces.
        var tabLine = document.LineStart(2);
        var tabPixels = style.SpaceWidth * style.TabWidth;
        Expect.Equal(tabPixels, view.LocationInLine(document, style, tabLine + 1, float.PositiveInfinity).X, "first tab stop");
        Expect.Equal(2 * tabPixels, view.LocationInLine(document, style, tabLine + 3, float.PositiveInfinity).X, "second tab stop");

        // Control characters get a representation box wider than nothing.
        var controlLine = document.LineStart(3);
        float before = view.LocationInLine(document, style, controlLine + 1, float.PositiveInfinity).X,
            after = view.LocationInLine(document, style, controlLine + 2, float.PositiveInfinity).X;
        Expect.True(after - before > style.SpaceWidth, "control representation width");
        Expect.Equal("SOH", EditView.Representation([1], out _), "C0 mnemonic");
        Expect.Equal("xFF", EditView.Representation([0xff], out var consumed), "invalid byte");
        Expect.Equal(1, consumed, "invalid byte length");

        // Invalidation re-lays out edited lines.
        document.InsertString(document.LineStart(1), "much longer than before ");
        view.Invalidate(1);
        Expect.True(view.LineWidth(document, style, 1) > view.LineWidth(document, style, 3), "invalidated line re-measured");

        PaintSmoke(view, document, style);
    }

    private static void PaintSmoke(EditView view, Document document, ViewStyle style)
    {
        style.Styles[ViewStyle.Default].Back = SKColors.White;
        style.Styles[ViewStyle.Default].Fore = SKColors.Black;
        style.ClearAll();
        style.Styles[ViewStyle.LineNumber].Back = SKColors.LightGray;
        style.Margins[0].Width = 30;
        style.Margins[1].Width = 0;
        style.Elements[Element.SelectionBack] = SKColors.Blue;
        style.Indicators[0].Style = IndicatorStyle.Squiggle;
        style.Indicators[0].Fore = SKColors.Red;
        view.Invalidate();
        document.SetIndicatorCurrent(0);
        document.DecorationFillRange(0, 1, 5);
        var selection = new SharpRail.Scintilla.Core.Selection();
        selection.SetMain(document.LineStart(1) + 4, document.LineStart(1));
        var display = new ContractionState();
        display.InsertLines(0, document.LinesTotal - 1);
        using var surface = SKSurface.Create(new SKImageInfo(400, 120));
        var state = new PaintState(document, style, display, selection, new SKRect(0, 0, 400, 120), 0, 0, float.PositiveInfinity,
            true, true, false);
        view.Paint(surface.Canvas, state);
        using var image = surface.Snapshot();
        using var pixels = image.PeekPixels();
        var textLeft = style.FixedColumnWidth;
        var row = (int)style.LineHeight;

        bool Any(int x0, int x1, int y0, int y1, Func<SKColor, bool> match)
        {
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    if (match(pixels.GetPixelColor(x, y))) return true;
            return false;
        }
        Expect.True(Any(textLeft, textLeft + 40, 0, row, c => c.Red < 128 && c.Green < 128 && c.Blue < 128), "text drawn");
        Expect.True(Any(textLeft, textLeft + 20, row, 2 * row, c => c.Blue > 200 && c.Red < 60), "selection drawn");
        Expect.True(Any(textLeft, textLeft + 30, row / 2, row + 2, c => c.Red > 200 && c.Red - c.Green > 60 && c.Red - c.Blue > 60), "squiggle drawn");
        Expect.True(Any(0, 30, 0, row, c => c.Red < 128 && c.Green < 128), "line number drawn");
        Expect.Equal(SKColors.LightGray, pixels.GetPixelColor(1, 1), "margin background");
    }
}