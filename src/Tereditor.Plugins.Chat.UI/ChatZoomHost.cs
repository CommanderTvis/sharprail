using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SharpRail.Plugins.Chat.UI;

/// <summary>
/// Lays its child out at the host's size divided by the zoom, then scales it from the top-left corner. Unlike a layout transform
/// control it never centres the child or grows it to its desired size, so the chat always fills the pane: the composer stays
/// on the bottom edge whatever the child asked for.
/// </summary>
internal sealed class ChatZoomHost : Decorator
{
    private double zoom = 1;

    internal double Zoom
    {
        get => zoom;
        set
        {
            if (zoom == value) return;
            zoom = value;
            if (Child is { } child) child.RenderTransform = new ScaleTransform(zoom, zoom);
            InvalidateMeasure();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ChildProperty || change.NewValue is not Control child) return;
        child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        child.RenderTransform = new ScaleTransform(zoom, zoom);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(new Size(availableSize.Width / zoom, availableSize.Height / zoom));
        return new Size(child.DesiredSize.Width * zoom, child.DesiredSize.Height * zoom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width / zoom, finalSize.Height / zoom));
        return finalSize;
    }
}