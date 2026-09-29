using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SharpRail.UI.Rendering;
using SkiaSharp;

namespace SharpRail.UI.Editor;

public sealed partial class ScintillaEditor : Control, IDisposable
{
    private readonly ScintillaDocument document;
    private readonly DispatcherTimer timer;
    private readonly InputClient inputClient;
    private bool disposed;
    private uint foreground, background;
    private nint revision;
    private readonly nint defaultRightMargin;
    private double wrapWidth = double.PositiveInfinity;
    public event EventHandler? TextChanged;
    public event EventHandler<Exception>? OperationFailed;

    public ScintillaEditor(string text = "")
    {
        Focusable = true; ClipToBounds = true; Cursor = new Cursor(StandardCursorType.Ibeam);
        document = new(text);
        revision = document.Revision;
        defaultRightMargin = document.Send(ScintillaMessage.GetMarginRight);
        inputClient = new(this);
        TextInputMethodClientRequested += (_, e) => { e.Client = inputClient; e.Handled = true; };
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        { if (IsFocused && !disposed) { document.Tick(); InvalidateVisual(); } });
        timer.Stop();
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
        var picture = document.Record((float)Bounds.Width, (float)Bounds.Height);
        context.Custom(new PictureOperation(new Rect(Bounds.Size), picture));
        // Scintilla settles scroll extents while painting; publish them after the render pass.
        Dispatcher.UIThread.Post(UpdateScroll, DispatcherPriority.Render);
    }

    private void ApplyColors()
    {
        var nextForeground = Rgb(Ui.TextBrush); var nextBackground = Rgb(Ui.Surface);
        if (foreground == nextForeground && background == nextBackground) return;
        foreground = nextForeground; background = nextBackground;
        document.Send(ScintillaMessage.StyleSetFore, 32, (nint)foreground);
        document.Send(ScintillaMessage.StyleSetBack, 32, (nint)background);
        document.Send(ScintillaMessage.StyleSetSize, 32, 13);
        document.Send(ScintillaMessage.StyleClearAll);
        document.Send(ScintillaMessage.StyleSetFore, 33, (nint)Rgb(Ui.Muted));
        document.Send(ScintillaMessage.StyleSetBack, 33, (nint)background);
        document.Send(ScintillaMessage.SetCaretFore, (nint)foreground);
        document.Send(ScintillaMessage.SetSelBack, 1, (nint)Rgb(Ui.Hover));
        return;
        static uint Rgb(IBrush brush) { var c = ((ISolidColorBrush)brush).Color; return (uint)(c.R | c.G << 8 | c.B << 16); }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    { base.OnGotFocus(e); document.Focus(true); timer.Start(); InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    { base.OnLostFocus(e); if (!disposed) document.Focus(false); timer.Stop(); InvalidateVisual(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { timer.Stop(); base.OnDetachedFromVisualTree(e); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); document.Dispose();
    }

    private sealed class PictureOperation(Rect bounds, SKPicture picture) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() => picture.Dispose();
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            lease.SkCanvas.DrawPicture(picture);
        }
    }
}
