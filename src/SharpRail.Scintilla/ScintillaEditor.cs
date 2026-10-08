using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;

using SkiaSharp;

namespace SharpRail.Scintilla;

/// <summary>Base paragraph direction for bidirectional text; automatic uses each line's first strong character.</summary>
public enum ScintillaTextDirection { LeftToRight, RightToLeft, Auto }

/// <summary>
/// Editor colours; the selection foreground keeps each style's colour when null, the caret takes the
/// foreground when null, and the caret's line is highlighted only when <see cref="CurrentLine"/> is set.
/// </summary>
public sealed record ScintillaColors(Color Foreground, Color Background, Color LineNumbers, Color Selection, Color? SelectionForeground = null)
{
    public Color? Caret { get; init; }
    public Color? CurrentLine { get; init; }
    public static ScintillaColors Light { get; } = new(Colors.Black, Colors.White, Colors.Gray, Color.FromRgb(0xb4, 0xd5, 0xfe));
}

public sealed partial class ScintillaEditor : Control, IDisposable
{
    // System fonts are shared process-wide and never disposed.
    private static readonly SKTypeface DefaultTypeface = SKTypeface.FromFamilyName("Menlo");
    private readonly ScintillaDocument document;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer idle;
    private readonly InputClient inputClient;
    private bool disposed;
    private ScintillaColors colors = ScintillaColors.Light;
    private ScintillaColors? appliedColors;
    private ScintillaTextDirection direction;
    private nint revision;
    private readonly nint defaultRightMargin;
    // Replaced recordings are left to the finalizer: the compositor may still be replaying them.
    private SKPicture? picture;
    private Size pictureSize;
    internal int Recordings { get; private set; }
    private double wrapWidth = double.PositiveInfinity;
    public event EventHandler? TextChanged;
    public event EventHandler<Exception>? OperationFailed;

    /// <summary>Creates an editor; the caller keeps ownership of <paramref name="typeface"/>, which defaults to Menlo.</summary>
    public ScintillaEditor(string text = "", SKTypeface? typeface = null)
    {
        Focusable = true; ClipToBounds = true; Cursor = new Cursor(StandardCursorType.Ibeam);
        document = new(text, typeface ?? DefaultTypeface);
        revision = document.Revision;
        defaultRightMargin = document.Send(ScintillaMessage.GetMarginRight);
        inputClient = new(this);
        TextInputMethodClientRequested += (_, e) => { e.Client = inputClient; e.Handled = true; };
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        { if (IsFocused && !disposed) { document.Tick(); InvalidateVisual(); } });
        timer.Stop();
        idle = new DispatcherTimer(TimeSpan.FromMilliseconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (disposed || !document.Idle()) idle!.Stop();
            UpdateScroll(); InvalidateVisual();
        });
    }

    public string Text
    {
        get => document.Text();
        set { document.SetText(value); Changed(); }
    }
    public bool IsModified => document.Send(ScintillaMessage.GetModify) != 0;
    public int FirstVisibleLine => checked((int)document.Send(ScintillaMessage.GetFirstVisibleLine));
    public bool IsReadOnly
    {
        get => document.Send(ScintillaMessage.GetReadOnly) != 0;
        set { document.Send(ScintillaMessage.SetReadOnly, value ? 1 : 0); InvalidateVisual(); }
    }
    /// <summary>Text column width before lines wrap, like the reference's bounded file width; infinity disables wrapping.</summary>
    public double WrapWidth
    {
        get => wrapWidth;
        set { wrapWidth = value; ApplyWrap(Bounds.Width); InvalidateVisual(); }
    }

    public ScintillaColors Colors
    {
        get => colors;
        set { colors = value; InvalidateVisual(); }
    }
    public ScintillaTextDirection Direction
    {
        get => direction;
        set { direction = value; document.SetDirection(value); InvalidateVisual(); inputClient.NotifyScrolled(); }
    }
    internal ScintillaDocument Document => document;

    /// <summary>Display lines that one document line occupies after wrapping.</summary>
    public int WrapCount(int line) => checked((int)document.Send(ScintillaMessage.WrapCount, line));
    public void MarkSaved() { document.Send(ScintillaMessage.SetSavePoint); InvalidateVisual(); }
    public void Undo() { document.Send(ScintillaMessage.Undo); Changed(); }
    public void Redo() { document.Send(ScintillaMessage.Redo); Changed(); }
    public void SelectAll() { document.Send(ScintillaMessage.SelectAll); InvalidateVisual(); }

    private void Changed()
    {
        InvalidateVisual(); inputClient.Notify();
        if (revision == document.Revision) return;
        revision = document.Revision;
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!disposed) { ApplyWrap(finalSize.Width); document.Resize(finalSize.Width, finalSize.Height); }
        return finalSize;
    }

    // Scintilla wraps at its text area, so a right margin narrows that area to the wrap width
    // while the editor still fills its pane.
    private void ApplyWrap(double width)
    {
        if (disposed) return;
        var wrap = double.IsFinite(wrapWidth);
        document.Send(ScintillaMessage.SetWrapMode, wrap ? 1 : 0);
        double gutter = document.Send(ScintillaMessage.GetMarginLeft);
        for (var margin = 0; margin < document.Send(ScintillaMessage.GetMargins); margin++)
            gutter += document.Send(ScintillaMessage.GetMarginWidthN, margin);
        document.Send(ScintillaMessage.SetMarginRight, 0, wrap ? (nint)Math.Max(defaultRightMargin, width - gutter - wrapWidth) : defaultRightMargin);
    }

    public override void Render(DrawingContext context)
    {
        if (disposed || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        ApplyColors();
        // A partial line scrolled past the top needs one more row painted below the viewport.
        var offset = SubLine;
        var extra = offset > 0 ? LineHeight : 0;
        var size = new Size(Bounds.Width, Bounds.Height + extra);
        // Scrolling within the first line only moves the transform, so the last recording stays valid until
        // Scintilla invalidates; the temporary resize for the partial row is not a change.
        if (picture is null || document.Dirty || pictureSize != size)
        {
            if (extra > 0) document.Resize(Bounds.Width, Bounds.Height + extra);
            document.Dirty = false;
            picture = document.Record((float)size.Width, (float)size.Height);
            Recordings++;
            pictureSize = size;
            var dirty = document.Dirty;
            if (extra > 0) document.Resize(Bounds.Width, Bounds.Height);
            document.Dirty = dirty;
        }
        using (context.PushTransform(Matrix.CreateTranslation(0, -offset)))
            context.Custom(new PictureOperation(new Rect(0, 0, Bounds.Width, Bounds.Height + extra), picture));
        // Scintilla settles scroll extents while painting; publish them after the render pass.
        Dispatcher.UIThread.Post(UpdateScroll, DispatcherPriority.Render);
        if (!idle.IsEnabled) idle.Start();
    }

    private void ApplyColors()
    {
        if (appliedColors == colors) return;
        appliedColors = colors;
        var foreground = Rgb(colors.Foreground); var background = Rgb(colors.Background);
        document.Send(ScintillaMessage.StyleSetFore, 32, (nint)foreground);
        document.Send(ScintillaMessage.StyleSetBack, 32, (nint)background);
        document.Send(ScintillaMessage.StyleSetSize, 32, 13);
        document.Send(ScintillaMessage.StyleClearAll);
        ApplyLineStyles(Rgb);
        document.Send(ScintillaMessage.StyleSetFore, 33, (nint)Rgb(colors.LineNumbers));
        document.Send(ScintillaMessage.StyleSetBack, 33, (nint)background);
        document.Send(ScintillaMessage.SetCaretFore, (nint)(colors.Caret is { } caret ? Rgb(caret) : foreground));
        if (colors.CurrentLine is { } line) document.Send(ScintillaMessage.SetElementColour, CaretLineBack, (nint)(Rgb(line) | 0xff000000));
        else document.Send(ScintillaMessage.ResetElementColour, CaretLineBack);
        var selection = Rgb(colors.Selection) | 0xff000000;
        document.Send(ScintillaMessage.SetElementColour, SelectionBack, (nint)selection);
        document.Send(ScintillaMessage.SetElementColour, SelectionInactiveBack, (nint)selection);
        if (colors.SelectionForeground is { } text)
        {
            document.Send(ScintillaMessage.SetElementColour, SelectionText, (nint)(Rgb(text) | 0xff000000));
            document.Send(ScintillaMessage.SetElementColour, SelectionInactiveText, (nint)(Rgb(text) | 0xff000000));
        }
        else
        {
            document.Send(ScintillaMessage.ResetElementColour, SelectionText);
            document.Send(ScintillaMessage.ResetElementColour, SelectionInactiveText);
        }
        return;
        static uint Rgb(Color c) => (uint)(c.R | c.G << 8 | c.B << 16);
    }

    // Scintilla's SC_ELEMENT_SELECTION_* ids; element colours are 0xAABBGGRR.
    private const int SelectionText = 10, SelectionBack = 11, SelectionInactiveText = 16, SelectionInactiveBack = 17, CaretLineBack = 50;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    { base.OnGotFocus(e); document.Focus(true); timer.Start(); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    { base.OnLostFocus(e); if (!disposed) document.Focus(false); timer.Stop(); InvalidateVisual(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { timer.Stop(); base.OnDetachedFromVisualTree(e); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); idle.Stop(); document.Dispose();
    }

    private sealed class PictureOperation(Rect bounds, SKPicture picture) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            lease.SkCanvas.DrawPicture(picture);
        }
    }
}