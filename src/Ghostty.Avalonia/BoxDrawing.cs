using SkiaSharp;

namespace Ghostty.Avalonia;

/// <summary>
/// Draws common box-drawing and block elements to fill their cells exactly, as Ghostty does,
/// so lines and blocks join across cells whatever the font's own glyphs and line height.
/// </summary>
internal static class BoxDrawing
{
    // Arm weights up, right, down, left: 1 light, 2 heavy, 3 double.
    private static readonly Dictionary<int, (byte Up, byte Right, byte Down, byte Left)> Lines = new()
    {
        [0x2500] = (0, 1, 0, 1),
        [0x2501] = (0, 2, 0, 2),
        [0x2502] = (1, 0, 1, 0),
        [0x2503] = (2, 0, 2, 0),
        [0x250C] = (0, 1, 1, 0),
        [0x250F] = (0, 2, 2, 0),
        [0x2510] = (0, 0, 1, 1),
        [0x2513] = (0, 0, 2, 2),
        [0x2514] = (1, 1, 0, 0),
        [0x2517] = (2, 2, 0, 0),
        [0x2518] = (1, 0, 0, 1),
        [0x251B] = (2, 0, 0, 2),
        [0x251C] = (1, 1, 1, 0),
        [0x2523] = (2, 2, 2, 0),
        [0x2524] = (1, 0, 1, 1),
        [0x252B] = (2, 0, 2, 2),
        [0x252C] = (0, 1, 1, 1),
        [0x2533] = (0, 2, 2, 2),
        [0x2534] = (1, 1, 0, 1),
        [0x253B] = (2, 2, 0, 2),
        [0x253C] = (1, 1, 1, 1),
        [0x254B] = (2, 2, 2, 2),
        [0x2550] = (0, 3, 0, 3),
        [0x2551] = (3, 0, 3, 0),
        [0x2554] = (0, 3, 3, 0),
        [0x2557] = (0, 0, 3, 3),
        [0x255A] = (3, 3, 0, 0),
        [0x255D] = (3, 0, 0, 3),
        [0x2560] = (3, 3, 3, 0),
        [0x2563] = (3, 0, 3, 3),
        [0x2566] = (0, 3, 3, 3),
        [0x2569] = (3, 3, 0, 3),
        [0x256C] = (3, 3, 3, 3),
        [0x2574] = (0, 0, 0, 1),
        [0x2575] = (1, 0, 0, 0),
        [0x2576] = (0, 1, 0, 0),
        [0x2577] = (0, 0, 1, 0),
        [0x2578] = (0, 0, 0, 2),
        [0x2579] = (2, 0, 0, 0),
        [0x257A] = (0, 2, 0, 0),
        [0x257B] = (0, 0, 2, 0),
    };

    // Quadrants: 1 upper left, 2 upper right, 4 lower left, 8 lower right.
    private static readonly byte[] Quadrants = [4, 8, 1, 1 | 4 | 8, 1 | 8, 1 | 2 | 4, 1 | 2 | 8, 2, 2 | 4, 2 | 4 | 8];

    internal static bool Draw(SKCanvas canvas, int codepoint, SKRect cell, SKColor color, float thin)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = false, Style = SKPaintStyle.Fill };
        float w = cell.Width, h = cell.Height;
        switch (codepoint)
        {
            case 0x2580: canvas.DrawRect(cell.Left, cell.Top, w, h / 2, paint); return true;
            case >= 0x2581 and <= 0x2588:
                var lower = h * (codepoint - 0x2580) / 8;
                canvas.DrawRect(cell.Left, cell.Bottom - lower, w, lower, paint); return true;
            case >= 0x2589 and <= 0x258F:
                canvas.DrawRect(cell.Left, cell.Top, w * (0x2590 - codepoint) / 8, h, paint); return true;
            case 0x2590: canvas.DrawRect(cell.MidX, cell.Top, w / 2, h, paint); return true;
            case >= 0x2591 and <= 0x2593:
                paint.Color = color.WithAlpha((byte)(color.Alpha * (codepoint - 0x2590) / 4)); canvas.DrawRect(cell, paint); return true;
            case 0x2594: canvas.DrawRect(cell.Left, cell.Top, w, h / 8, paint); return true;
            case 0x2595: canvas.DrawRect(cell.Right - w / 8, cell.Top, w / 8, h, paint); return true;
            case >= 0x2596 and <= 0x259F:
                var bits = Quadrants[codepoint - 0x2596];
                if ((bits & 1) != 0) canvas.DrawRect(cell.Left, cell.Top, w / 2, h / 2, paint);
                if ((bits & 2) != 0) canvas.DrawRect(cell.MidX, cell.Top, w / 2, h / 2, paint);
                if ((bits & 4) != 0) canvas.DrawRect(cell.Left, cell.MidY, w / 2, h / 2, paint);
                if ((bits & 8) != 0) canvas.DrawRect(cell.MidX, cell.MidY, w / 2, h / 2, paint);
                return true;
            case >= 0x256D and <= 0x2570:
                Rounded(canvas, codepoint, cell, color, thin); return true;
        }
        if (!Lines.TryGetValue(codepoint, out var arms)) return false;
        var heavy = thin * 2;
        // Pixel-aligned centre lines so arms of neighbouring cells meet.
        float cx = MathF.Floor(cell.MidX - thin / 2), cy = MathF.Floor(cell.MidY - thin / 2);
        Arm(arms.Up, vertical: true, cell.Top, cy + Width(arms.Up));
        Arm(arms.Down, vertical: true, cy, cell.Bottom);
        Arm(arms.Left, vertical: false, cell.Left, cx + Width(arms.Left));
        Arm(arms.Right, vertical: false, cx, cell.Right);
        return true;

        float Width(byte weight) => weight == 2 ? heavy : thin;
        void Arm(byte weight, bool vertical, float from, float to)
        {
            if (weight == 0) return;
            var size = Width(weight);
            var offsets = weight == 3 ? new[] { -thin, thin } : [0f];
            foreach (var offset in offsets)
            {
                if (vertical) canvas.DrawRect(cx + offset - (size - thin) / 2, from, size, to - from, paint);
                else canvas.DrawRect(from, cy + offset - (size - thin) / 2, to - from, size, paint);
            }
        }
    }

    private static void Rounded(SKCanvas canvas, int codepoint, SKRect cell, SKColor color, float thin)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = thin };
        float cx = MathF.Floor(cell.MidX - thin / 2) + thin / 2, cy = MathF.Floor(cell.MidY - thin / 2) + thin / 2;
        var radius = Math.Min(cell.Width, cell.Height) / 2;
        using var builder = new SKPathBuilder();
        // ╭ ╮ ╯ ╰: arcs joining the arm towards each cell edge.
        var (horizontal, vertical) = codepoint switch
        {
            0x256D => (cell.Right, cell.Bottom),
            0x256E => (cell.Left, cell.Bottom),
            0x256F => (cell.Left, cell.Top),
            _ => (cell.Right, cell.Top)
        };
        builder.MoveTo(horizontal, cy);
        builder.LineTo(cx + Math.Sign(horizontal - cx) * radius, cy);
        builder.QuadTo(cx, cy, cx, cy + Math.Sign(vertical - cy) * radius);
        builder.LineTo(cx, vertical);
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);
    }
}