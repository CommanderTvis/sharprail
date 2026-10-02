using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;

using SkiaSharp;

namespace Ghostty.Avalonia;

/// <summary>A terminal grid size in cells.</summary>
public readonly record struct TerminalSize(int Columns, int Rows);

/// <summary>
/// A terminal drawn by Avalonia's Skia renderer over libghostty-vt. Ghostty parses output and keeps the
/// screen, scrollback, modes and selection; this control draws cells, shapes text with the system fonts and
/// encodes keyboard, mouse, focus and paste input. It runs no process: feed output with <see cref="Write"/>
/// and send <see cref="Input"/> to the program, telling it about <see cref="GridResized"/>.
/// Use it from the UI thread.
/// </summary>
public sealed partial class GhosttySkiaView : Control, IDisposable
{
    private readonly Vt vt;
    private readonly DispatcherTimer blink = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CellFonts? fonts;
    private (SKTypeface? Typeface, string Family, double Size, double Scale) fontKey;
    private TerminalColors colors = TerminalColors.Default;
    private readonly TerminalFramebuffer framebuffer = new();
    private TerminalRowPicture[] rows = [];
    private (CellFonts? Fonts, TerminalColors Colors, Size Size, Thickness Padding) rowLayout;
    internal int RowsRecorded { get; private set; }
    internal bool IsGpuBacked => framebuffer.IsGpuBacked;
    private bool stale = true, snapshotNeeded = true, cursorOn = true, disposed;
    private Frame last;

    public GhosttySkiaView(int scrollbackLines = 10_000)
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        vt = new Vt(Size.Columns, Size.Rows, scrollbackLines, Reply);
        vt.SetColors(colors);
        vt.OptionAsAlt = OperatingSystem.IsMacOS();
        blink.Tick += (_, _) => { cursorOn = !cursorOn; Redraw(snapshot: false); };
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = InputMethod, RoutingStrategies.Bubble);
    }

    /// <summary>Bytes for the program: typed keys, encoded mouse and paste input, and replies to terminal queries.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Input;
    /// <summary>The grid changed size to fit the control; resize the program's terminal to match.</summary>
    public event EventHandler<TerminalSize>? GridResized;

    public TerminalSize Size { get; private set; } = new(80, 24);

    /// <summary>A caller-owned grid face used for every style; null uses <see cref="FontFamily"/>. Characters it lacks fall back to system fonts.</summary>
    public SKTypeface? Typeface
    {
        get;
        set { field = value; fonts?.Dispose(); fonts = null; Relayout(); }
    }

    /// <summary>The system font family of the grid, and the family fallback matching prefers.</summary>
    public string FontFamily
    {
        get;
        set { field = value; fonts?.Dispose(); fonts = null; Relayout(); }
    } = OperatingSystem.IsMacOS() ? "Menlo" : OperatingSystem.IsWindows() ? "Cascadia Mono" : "DejaVu Sans Mono";

    public double FontSize
    {
        get;
        set { field = value; fonts?.Dispose(); fonts = null; Relayout(); }
    } = 13;

    /// <summary>Space between the control's edge and the grid.</summary>
    public Thickness Padding { get; set { field = value; Relayout(); } } = new(4);

    public TerminalColors Colors
    {
        get => colors;
        set { colors = value; vt.SetColors(value); Redraw(); }
    }

    /// <summary>Whether Option sends Alt-modified keys on macOS rather than composing characters. On by default.</summary>
    public bool OptionAsAlt { get; set { field = value; vt.OptionAsAlt = value; } } = OperatingSystem.IsMacOS();

    /// <summary>When set, pasting an image saves it there as PNG and pastes its shell-quoted path.</summary>
    public string? ClipboardImageDirectory { get; set; }

    /// <summary>Raised when clipboard access fails, for example while saving a pasted image.</summary>
    public event EventHandler<Exception>? OperationFailed;

    /// <summary>The number of frames recorded since creation.</summary>
    public int Frames { get; private set; }

    /// <summary>Whether the viewport shows the newest output rather than scrollback.</summary>
    public bool IsAtBottom => last.ScrollOffset + last.ScrollLength >= last.ScrollTotal;

    /// <summary>Feeds program output to the terminal.</summary>
    public void Write(ReadOnlySpan<byte> output)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (output.IsEmpty) return;
        vt.Write(output);
        Redraw();
    }

    /// <summary>The screen and scrollback as plain text.</summary>
    public string ReadScreen() => vt.ScreenText();
    public string SelectedText => vt.SelectionText();
    public void SelectAll() { vt.SelectAll(); Redraw(); }

    public void FocusTerminal() => Focus();

    private void Reply(ReadOnlySpan<byte> data) => Input?.Invoke(this, data.ToArray());
    private void Send(ReadOnlySpan<byte> data) { if (!data.IsEmpty) Input?.Invoke(this, data.ToArray()); }

    private void Redraw(bool snapshot = true)
    {
        stale = true;
        snapshotNeeded |= snapshot;
        InvalidateVisual();
    }

    private void Relayout()
    {
        stale = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private CellFonts Fonts()
    {
        var scale = PixelScale.Of(this);
        if (fonts is not null && fontKey == (Typeface, FontFamily, FontSize, scale)) return fonts;
        fonts?.Dispose();
        fontKey = (Typeface, FontFamily, FontSize, scale);
        return fonts = new CellFonts(Typeface, FontFamily, FontSize, scale);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cells = Fonts();
        double width = double.IsInfinity(availableSize.Width) ? 80 * cells.CellWidth + Padding.Left + Padding.Right : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 24 * cells.CellHeight + Padding.Top + Padding.Bottom : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cells = Fonts();
        var scale = fontKey.Scale;
        var size = new TerminalSize(
            Math.Max(1, (int)((finalSize.Width - Padding.Left - Padding.Right) / cells.CellWidth)),
            Math.Max(1, (int)((finalSize.Height - Padding.Top - Padding.Bottom) / cells.CellHeight)));
        var pixels = (Width: (int)Math.Round(cells.CellWidth * scale), Height: (int)Math.Round(cells.CellHeight * scale));
        if (size != Size || pixels != cellPixels)
        {
            var resized = size != Size;
            Size = size; cellPixels = pixels;
            vt.Resize(size.Columns, size.Rows, pixels.Width, pixels.Height);
            Redraw();
            if (resized) GridResized?.Invoke(this, size);
        }
        return finalSize;
    }

    private (int Width, int Height) cellPixels;

    public override void Render(DrawingContext context)
    {
        if (disposed || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var cells = Fonts();
        if (stale || rowLayout != (cells, colors, Bounds.Size, Padding))
        {
            var frame = last;
            if (snapshotNeeded && !vt.Snapshot(out frame)) return;
            UpdateBlink(frame);
            UpdateLinks(frame);
            var invalidate = rowLayout != (cells, colors, Bounds.Size, Padding);
            rowLayout = (cells, colors, Bounds.Size, Padding);
            if (rows.Length != frame.Rows)
            {
                foreach (var row in rows) row.Release();
                rows = new TerminalRowPicture[frame.Rows];
                invalidate = true;
            }
            for (var y = 0; y < rows.Length; y++)
            {
                var cursor = frame.CursorVisible != 0 && frame.CursorY == y;
                var appearance = new RowAppearance(frame.Background, frame.Foreground,
                    cursor ? frame.CursorX : -1, cursor ? frame.CursorColor : 0,
                    cursor ? frame.CursorStyle : (byte)0, cursor && cursorOn, cursor && IsKeyboardFocusWithin,
                    cursor ? preedit : "");
                if (!invalidate && rows[y].Matches(frame, y, appearance)) continue;
                var next = new TerminalRowPicture(frame, y, appearance, Record(frame, cells,
                    (float)Bounds.Width, (float)Bounds.Height, y));
                rows[y]?.Release();
                rows[y] = next;
                RowsRecorded++;
            }
            last = frame;
            snapshotNeeded = false;
            stale = false;
            Frames++;
        }
        context.Custom(new RowOperation(new Rect(Bounds.Size), rows, framebuffer, fontKey.Scale));
    }

    private void UpdateBlink(Frame frame)
    {
        var blinking = IsKeyboardFocusWithin && frame.CursorVisible != 0 && frame.CursorBlinking != 0;
        if (blinking == blink.IsEnabled) return;
        if (blinking) blink.Start(); else { blink.Stop(); cursorOn = true; }
    }

    private unsafe SKPicture Record(Frame frame, CellFonts cells, float width, float height, int row)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, width, height));
        var background = Background(frame);

        float left = (float)Padding.Left, top = (float)Padding.Top, cw = cells.CellWidth, ch = cells.CellHeight;
        using var fill = new SKPaint { Style = SKPaintStyle.Fill };
        using var text = new SKPaint { IsAntialias = true };
        var cursorCell = frame.CursorVisible != 0 && (cursorOn || !IsKeyboardFocusWithin)
            ? frame.CursorY * frame.Columns + frame.CursorX - (frame.CursorWideTail != 0 ? 1 : 0) : -1;
        var focused = IsKeyboardFocusWithin;
        var blockCursor = cursorCell >= 0 && focused && frame.CursorStyle == 1;
        var cursorColor = frame.CursorColor >> 24 != 0 ? new SKColor(frame.CursorColor) : colors.Cursor is { } c ? c.ToSKColor() : new SKColor(frame.Foreground);
        var clip = new SKRect(0, row == 0 ? 0 : top + row * ch, width,
            row == frame.Rows - 1 ? height : top + (row + 1) * ch);
        canvas.ClipRect(clip);
        fill.Color = background;
        fill.BlendMode = SKBlendMode.Src;
        canvas.DrawRect(clip, fill);
        fill.BlendMode = SKBlendMode.SrcOver;
        var y = row;
        var rowTop = top + y * ch;
        // Backgrounds first, merged into runs, so wide glyphs and overhangs are not clipped by later cells.
        for (var x = 0; x < frame.Columns;)
        {
            var (_, bg) = Resolve(frame, frame.Cells[y * frame.Columns + x]);
            var end = x + 1;
            while (end < frame.Columns && Resolve(frame, frame.Cells[y * frame.Columns + end]).Background == bg) end++;
            if (bg != background)
            {
                fill.Color = bg;
                // Cells on the grid's edges extend their background into the padding, as Ghostty does.
                canvas.DrawRect(new SKRect(x == 0 ? 0 : left + x * cw, y == 0 ? 0 : rowTop,
                    end == frame.Columns ? width : left + end * cw, y == frame.Rows - 1 ? height : rowTop + ch), fill);
            }
            x = end;
        }
        var drawnThrough = -1;
        for (var x = 0; x < frame.Columns; x++)
        {
            var index = y * frame.Columns + x;
            var cell = frame.Cells[index];
            if (cell.Wide == 2 || cell.Wide == 3) continue;
            var span = cell.Wide == 1 ? 2 : 1;
            var (fg, bg) = Resolve(frame, cell);
            var bounds = new SKRect(left + x * cw, rowTop, left + (x + span) * cw, rowTop + ch);
            if (index == cursorCell && blockCursor)
            {
                fill.Color = cursorColor;
                canvas.DrawRect(bounds, fill);
                fg = colors.Cursor is null && frame.CursorColor >> 24 == 0 ? bg : Contrast(background, cursorColor);
            }
            if (x > drawnThrough && cell.Length > 0 && !cell.Flags.HasFlag(CellFlags.Invisible))
            {
                var grapheme = new ReadOnlySpan<uint>(frame.Text + cell.Text, cell.Length);
                if (cell.Length != 1 || !BoxDrawing.Draw(canvas, (int)grapheme[0], bounds, fg, cells.LineThickness))
                {
                    var bold = cell.Flags.HasFlag(CellFlags.Bold);
                    var italic = cell.Flags.HasFlag(CellFlags.Italic);
                    SKTextBlob? blob;
                    if (span == 1 && cell.Length == 1 && grapheme[0] is >= 32 and <= 126 && index != cursorCell)
                    {
                        var end = x + 1;
                        while (end < frame.Columns)
                        {
                            var next = frame.Cells[y * frame.Columns + end];
                            if (next.Length != 1 || next.Wide != 0 || next.Flags != cell.Flags ||
                                frame.Text[next.Text] is < 32 or > 126 || Resolve(frame, next).Foreground != fg ||
                                y * frame.Columns + end == cursorCell) break;
                            end++;
                        }
                        var chars = new char[end - x];
                        for (var i = x; i < end; i++) chars[i - x] = (char)frame.Text[frame.Cells[y * frame.Columns + i].Text];
                        blob = cells.Blob(new string(chars), bold, italic, 1, asciiRun: true);
                        drawnThrough = end - 1;
                    }
                    else blob = cells.Blob(CellFonts.Grapheme(grapheme), bold, italic, span);
                    text.Color = fg;
                    if (blob is not null) canvas.DrawText(blob, bounds.Left, rowTop + cells.Baseline, text);
                }
            }
            if (linkCells.Contains(index) && cell.Underline == 0) cell.Underline = 1;
            Decorate(canvas, cell, fg, bounds, cells);
            if (index == cursorCell && !blockCursor) DrawCursor(canvas, frame.CursorStyle, focused, bounds, cursorColor, cells);
        }
        if (preedit.Length > 0 && frame.CursorVisible != 0 && frame.CursorY == row) DrawPreedit(canvas, frame, cells, background);
        return recorder.EndRecording();
    }

    // Composition text from an input method, underlined at the cursor until it is committed.
    private void DrawPreedit(SKCanvas canvas, Frame frame, CellFonts cells, SKColor background)
    {
        var font = cells.Regular;
        var width = Math.Max(cells.CellWidth, font.MeasureText(preedit));
        var x = (float)Padding.Left + frame.CursorX * cells.CellWidth;
        var y = (float)Padding.Top + frame.CursorY * cells.CellHeight;
        using var paint = new SKPaint { Color = background, IsAntialias = true };
        canvas.DrawRect(x, y, width, cells.CellHeight, paint);
        paint.Color = new SKColor(frame.Foreground);
        canvas.DrawText(preedit, x, y + cells.Baseline, SKTextAlign.Left, font, paint);
        canvas.DrawRect(x, y + cells.UnderlinePosition, width, cells.LineThickness, paint);
    }

    private static void Decorate(SKCanvas canvas, Cell cell, SKColor foreground, SKRect bounds, CellFonts cells)
    {
        if (cell.Underline == 0 && (cell.Flags & (CellFlags.Strikethrough | CellFlags.Overline)) == 0) return;
        var thickness = cells.LineThickness;
        using var paint = new SKPaint { Color = cell.UnderlineColor >> 24 != 0 ? new SKColor(cell.UnderlineColor) : foreground, StrokeWidth = thickness, IsAntialias = true, Style = SKPaintStyle.Stroke };
        var y = bounds.Top + cells.UnderlinePosition;
        switch (cell.Underline)
        {
            case 1: canvas.DrawLine(bounds.Left, y, bounds.Right, y, paint); break;
            case 2:
                canvas.DrawLine(bounds.Left, y - thickness, bounds.Right, y - thickness, paint);
                canvas.DrawLine(bounds.Left, y + thickness, bounds.Right, y + thickness, paint);
                break;
            case 3:
                using (var builder = new SKPathBuilder())
                {
                    var amplitude = thickness * 1.5f; var step = bounds.Width / 2;
                    builder.MoveTo(bounds.Left, y);
                    for (var i = 0; i < 2; i++)
                        builder.QuadTo(bounds.Left + step * (i + 0.5f), y + (i == 0 ? -amplitude : amplitude), bounds.Left + step * (i + 1), y);
                    using var wave = builder.Detach();
                    canvas.DrawPath(wave, paint);
                }
                break;
            case 4 or 5:
                using (var effect = SKPathEffect.CreateDash(cell.Underline == 4 ? [thickness, thickness * 2] : [thickness * 3, thickness * 2], 0))
                {
                    paint.PathEffect = effect;
                    canvas.DrawLine(bounds.Left, y, bounds.Right, y, paint);
                }
                break;
        }
        paint.Color = foreground; paint.PathEffect = null;
        if (cell.Flags.HasFlag(CellFlags.Strikethrough)) canvas.DrawLine(bounds.Left, bounds.Top + cells.StrikePosition, bounds.Right, bounds.Top + cells.StrikePosition, paint);
        if (cell.Flags.HasFlag(CellFlags.Overline)) canvas.DrawLine(bounds.Left, bounds.Top + thickness / 2, bounds.Right, bounds.Top + thickness / 2, paint);
    }

    private static void DrawCursor(SKCanvas canvas, byte style, bool focused, SKRect bounds, SKColor color, CellFonts cells)
    {
        var thickness = Math.Max(cells.LineThickness, 1);
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        // Without focus every cursor shows as a hollow block, as in Ghostty.
        if (!focused || style == 3)
        {
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = thickness;
            canvas.DrawRect(SKRect.Inflate(bounds, -thickness / 2, -thickness / 2), paint);
        }
        else if (style == 0) canvas.DrawRect(bounds.Left, bounds.Top, thickness * 2, bounds.Height, paint);
        else if (style == 2) canvas.DrawRect(bounds.Left, bounds.Bottom - thickness * 2, bounds.Width, thickness * 2, paint);
    }

    private SKColor Background(Frame frame) => new(frame.Background);

    // A cell's drawn colours after defaults, inverse video, selection, faint and the contrast floor.
    private (SKColor Foreground, SKColor Background) Resolve(Frame frame, Cell cell)
    {
        var fg = cell.Foreground >> 24 != 0 ? cell.Foreground : frame.Foreground;
        var bg = cell.Background >> 24 != 0 ? cell.Background : frame.Background;
        if (cell.Flags.HasFlag(CellFlags.Inverse)) (fg, bg) = (bg, fg);
        if (cell.Flags.HasFlag(CellFlags.Selected))
        {
            var selection = colors.SelectionBackground?.ToUInt32() ?? fg;
            fg = colors.SelectionForeground?.ToUInt32() ?? (colors.SelectionBackground is null ? bg : fg);
            bg = selection;
        }
        var foreground = new SKColor(fg);
        var background = new SKColor(bg);
        if (colors.MinimumContrast > 1 && Ratio(foreground, background) < colors.MinimumContrast) foreground = Contrast(background, foreground);
        if (cell.Flags.HasFlag(CellFlags.Faint)) foreground = foreground.WithAlpha(0x80);
        return (foreground, background);
    }

    // The better of black and white against the background, keeping the colour when it already reads.
    private static SKColor Contrast(SKColor background, SKColor preferred) =>
        Ratio(preferred, background) >= 3 ? preferred : Ratio(SKColors.White, background) >= Ratio(SKColors.Black, background) ? SKColors.White : SKColors.Black;

    private static double Ratio(SKColor a, SKColor b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        static double Luminance(SKColor c) => 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
        static double Channel(byte value) => value / 255.0 is var v && v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty) Redraw(snapshot: false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        blink.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        blink.Stop();
        vt.Dispose();
        fonts?.Dispose();
        foreach (var row in rows) row.Release();
        rows = [];
        framebuffer.Dispose();
    }

    private sealed class RowOperation : ICustomDrawOperation
    {
        private TerminalRowPicture[] rows;
        private readonly TerminalFramebuffer framebuffer;
        private readonly double scale;
        internal RowOperation(Rect bounds, TerminalRowPicture[] rows, TerminalFramebuffer framebuffer, double scale)
        {
            Bounds = bounds; this.rows = [.. rows]; this.framebuffer = framebuffer; this.scale = scale;
            foreach (var row in this.rows) row.Retain();
        }
        public Rect Bounds { get; }
        public bool HitTest(Point point) => Bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose()
        {
            foreach (var row in rows) row.Release();
            rows = [];
        }
        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            framebuffer.Draw(lease.SkCanvas, lease.GrContext, Bounds, scale, rows);
        }
    }
}