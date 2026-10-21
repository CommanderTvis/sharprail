using System.Text;

using SharpRail.Scintilla;
using SharpRail.Scintilla.View;

using SkiaSharp;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Managed-only checks (no upstream suite): shaped screen-line layout replacing Native/Layout.cxx.</summary>
internal static class LineShapeChecks
{
    private static readonly FontSpec Font = new(13, 400, false);

    internal static void Run()
    {
        using var shaper = new LineShaper(SKTypeface.Default);
        Expect.Equal(1, Shape(shaper, "👨\u200D👩\u200D👧").Clusters.Count, "emoji ZWJ sequence clusters");
        var combining = Shape(shaper, "e\u0301x");
        Expect.Equal(2, combining.Clusters.Count, "combining mark clusters");
        Expect.Equal((0, 3), (combining.Clusters[0].Start, combining.Clusters[0].End), "combining mark cluster bytes");

        // A tab advances to the next stop past x.
        var tabbed = shaper.Shape("a\tb"u8, [new(0, 1, Font, 0), new(1, 2, Font, -1), new(2, 3, Font, 0)], 32);
        var tab = tabbed.Clusters[1];
        Expect.Equal(32f, tab.Left + tab.Width, "tab ends at the next stop");
        Expect.Equal(32f, tabbed.Clusters[2].Left, "text after the tab starts at the stop");
        var wide = shaper.Shape("\t"u8, [new(0, 1, Font, -1)], 32);
        Expect.Equal(32f, wide.Width, "tab at line start");
        var representation = shaper.Shape("a\u0001"u8, [new(0, 1, Font, 0), new(1, 2, Font, 20)], 32);
        Expect.Equal(20f, representation.Clusters[1].Width, "representation width");

        var ltr = Shape(shaper, "abc d");
        Expect.Equal(5, ltr.Clusters.Count, "LTR clusters");
        foreach (var c in ltr.Clusters)
        {
            Expect.Equal(c.Left, ltr.XFromPosition(c.Start), $"LTR x of {c.Start}");
            Expect.Equal(c.Start, ltr.PositionFromX(c.Left + 0.1f, false), $"LTR position near left of {c.Start}");
            Expect.Equal(c.End, ltr.PositionFromX(c.Left + c.Width - 0.1f, false), $"LTR position near right of {c.Start}");
            Expect.Equal(c.Start, ltr.PositionFromX(c.Left + c.Width - 0.1f, true), $"LTR character position of {c.Start}");
        }
        Expect.Equal(ltr.Width, ltr.XFromPosition(5), "LTR line end x");
        Expect.Equal(5, ltr.PositionFromX(ltr.Width + 50, false), "LTR past the end");
        var all = ltr.RangeIntervals(0, 5);
        Expect.Equal(1, all.Count, "contiguous clusters merge");
        Expect.Equal(new ShapeInterval(0, ltr.Width), all[0], "merged interval");
        Expect.Equal(new ShapeInterval(ltr.Clusters[1].Left, ltr.Clusters[2].Left + ltr.Clusters[2].Width), ltr.RangeIntervals(1, 3)[0], "partial interval");

        shaper.Direction = ScintillaTextDirection.Auto;
        var rtl = Shape(shaper, "אבג");
        Expect.Equal(3, rtl.Clusters.Count, "RTL clusters");
        Expect.Equal(4, rtl.Clusters[0].Start, "RTL visual order starts at the last logical cluster");
        foreach (var c in rtl.Clusters)
        {
            Expect.True(c.RightToLeft, "RTL cluster direction");
            Expect.Equal(c.Left + c.Width, rtl.XFromPosition(c.Start), $"RTL x of {c.Start}");
            Expect.Equal(c.Start, rtl.PositionFromX(c.Left + c.Width - 0.1f, false), $"RTL position near right of {c.Start}");
            Expect.Equal(c.End, rtl.PositionFromX(c.Left + 0.1f, false), $"RTL position near left of {c.Start}");
        }
        Expect.Equal(0f, rtl.XFromPosition(6), "RTL line end x");

        var mixed = Shape(shaper, "ab אב");
        Expect.Equal(1, mixed.RangeIntervals(0, 7).Count, "mixed full range is one interval");
        Expect.True(ReferenceEquals(mixed, Shape(shaper, "ab אב")), "shapes are cached");

        using var surface = SKSurface.Create(new SKImageInfo(64, 32));
        mixed.Draw(surface.Canvas, 0, 7, 0, 20, SKColors.Black);
        shaper.DrawText(surface.Canvas, Font, "12"u8, 0, 20, SKColors.Black);
        Expect.True(shaper.Measure(Font, "12"u8) > 0, "measure");
        var (ascent, descent, height) = shaper.Metrics(Font);
        Expect.True(ascent > 0 && descent > 0 && height >= ascent + descent - 1, "metrics");
    }

    private static LineShape Shape(LineShaper shaper, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return shaper.Shape(bytes, [new(0, bytes.Length, Font, 0)], 32);
    }
}