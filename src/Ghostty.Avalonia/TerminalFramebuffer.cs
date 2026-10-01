using Avalonia;

using SkiaSharp;

namespace Ghostty.Avalonia;

// Retained rows/framebuffer inspired by Royal Apps' RoyalTerminal (MIT).
// Pinned sources and attribution: README.md, "Skia rendering design".
internal readonly record struct RowAppearance(uint Background, uint Foreground, int CursorX,
    uint CursorColor, byte CursorStyle, bool CursorOn, bool Focused, string Preedit);

internal sealed unsafe class TerminalRowPicture
{
    private static long sequence;
    private int references = 1;
    private readonly Cell[] cells;
    private readonly uint[] text;
    private readonly RowAppearance appearance;
    internal long Id { get; } = Interlocked.Increment(ref sequence);
    internal SKPicture Picture { get; }

    internal TerminalRowPicture(Frame frame, int row, RowAppearance appearance, SKPicture picture)
    {
        this.appearance = appearance;
        Picture = picture;
        cells = new ReadOnlySpan<Cell>(frame.Cells + row * frame.Columns, frame.Columns).ToArray();
        uint start = 0, end = 0;
        foreach (var cell in cells)
        {
            if (cell.Length == 0) continue;
            if (end == 0) start = cell.Text;
            end = cell.Text + cell.Length;
        }
        text = new ReadOnlySpan<uint>(frame.Text + start, (int)(end - start)).ToArray();
        for (var x = 0; x < cells.Length; x++) cells[x].Text = cells[x].Length == 0 ? 0 : cells[x].Text - start;
    }

    internal bool Matches(Frame frame, int row, RowAppearance next)
    {
        if (appearance != next || cells.Length != frame.Columns) return false;
        for (var x = 0; x < cells.Length; x++)
        {
            var a = cells[x]; var b = frame.Cells[row * frame.Columns + x];
            if (a.Length != b.Length || a.Wide != b.Wide || a.Underline != b.Underline || a.Flags != b.Flags ||
                a.Foreground != b.Foreground || a.Background != b.Background || a.UnderlineColor != b.UnderlineColor ||
                !text.AsSpan((int)a.Text, a.Length).SequenceEqual(new ReadOnlySpan<uint>(frame.Text + b.Text, b.Length))) return false;
        }
        return true;
    }

    internal void Retain() => Interlocked.Increment(ref references);
    internal void Release() { if (Interlocked.Decrement(ref references) == 0) Picture.Dispose(); }
}

internal sealed class TerminalFramebuffer : IDisposable
{
    private readonly object gate = new();
    private SKSurface? surface;
    private GRContext? context;
    private SKImageInfo info;
    private double scale;
    private long[] versions = [];
    private bool disposed;
    private bool gpuBacked;
    internal bool IsGpuBacked { get { lock (gate) return gpuBacked; } }

    internal void Draw(SKCanvas canvas, GRContext? gpu, Rect bounds, double nextScale, TerminalRowPicture[] rows)
    {
        lock (gate)
        {
            if (disposed || rows.Length == 0) return;
            var next = new SKImageInfo(Math.Max(1, (int)Math.Ceiling(bounds.Width * nextScale)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * nextScale)), SKColorType.Bgra8888, SKAlphaType.Premul);
            if (surface is null || next != info || context != gpu || scale != nextScale || versions.Length != rows.Length)
            {
                surface?.Dispose();
                surface = gpu is null ? SKSurface.Create(next) : SKSurface.Create(gpu, false, next);
                gpuBacked = gpu is not null && surface is not null;
                surface ??= SKSurface.Create(next);
                info = next; context = gpu; scale = nextScale; versions = new long[rows.Length];
            }
            if (surface is null) throw new InvalidOperationException("Could not allocate the terminal framebuffer.");
            var target = surface.Canvas;
            target.Save();
            target.Scale((float)scale);
            for (var y = 0; y < rows.Length; y++)
            {
                if (versions[y] == rows[y].Id) continue;
                target.DrawPicture(rows[y].Picture);
                versions[y] = rows[y].Id;
            }
            target.Restore();
            using var image = surface.Snapshot();
            canvas.DrawImage(image, new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height),
                new SKSamplingOptions(SKFilterMode.Nearest));
        }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; surface?.Dispose(); surface = null; }
    }
}