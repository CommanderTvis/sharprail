using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>App-wide behaviour of every <see cref="ScrollViewer"/> behind the quiet scrollbar look in ControlStyles:
/// the scrolling-intent class with its grace period, and directional edge curtains that fade content on clipped edges.</summary>
internal static class QuietScroll
{
    public const string IntentClass = "quiet-scroll-intent";
    public const double Curtain = 16;
    private const double Epsilon = 1;
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(700);
    private static readonly ConditionalWeakTable<ScrollViewer, State> States = [];
    private static bool installed;

    private sealed class State
    {
        public DispatcherTimer? Timer;
        public (bool Top, bool Bottom, bool Left, bool Right, Size Size) Edges;
    }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>((viewer, args) =>
        {
            if (args.OffsetDelta != default) MarkScrolling(viewer);
            UpdateCurtains(viewer);
        });
        Visual.BoundsProperty.Changed.AddClassHandler<ScrollContentPresenter>((presenter, _) =>
        {
            if (presenter.TemplatedParent is ScrollViewer viewer) UpdateCurtains(viewer);
        });
    }

    private static void MarkScrolling(ScrollViewer viewer)
    {
        var state = States.GetOrCreateValue(viewer);
        viewer.Classes.Add(IntentClass);
        if (state.Timer is null)
        {
            state.Timer = new DispatcherTimer { Interval = Grace };
            state.Timer.Tick += (_, _) =>
            {
                state.Timer.Stop();
                viewer.Classes.Remove(IntentClass);
            };
        }
        state.Timer.Stop();
        state.Timer.Start();
    }

    /// <summary>Curtains are an opacity mask on the content presenter, so they reveal whatever surface lies beneath and never
    /// take pointer input. Only the clipped directions fade; with both axes clipped the vertical edges win.</summary>
    private static void UpdateCurtains(ScrollViewer viewer)
    {
        if (viewer.Presenter is not ScrollContentPresenter presenter) return;
        var (offset, viewport, extent) = (viewer.Offset, viewer.Viewport, viewer.Extent);
        var size = presenter.Bounds.Size;
        var edges = (offset.Y > Epsilon, offset.Y + viewport.Height < extent.Height - Epsilon,
            offset.X > Epsilon, offset.X + viewport.Width < extent.Width - Epsilon, size);
        var state = States.GetOrCreateValue(viewer);
        if (state.Edges == edges) return;
        state.Edges = edges;
        var (top, bottom, left, right, _) = edges;
        presenter.OpacityMask = top || bottom ? Mask(top, bottom, size.Height, vertical: true)
            : left || right ? Mask(left, right, size.Width, vertical: false)
            : null;
    }

    private static LinearGradientBrush? Mask(bool start, bool end, double length, bool vertical)
    {
        if (length <= 2 * Curtain) return null;
        var fraction = Curtain / length;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(start ? Colors.Transparent : Colors.Black, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, start ? fraction : 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, end ? 1 - fraction : 1));
        brush.GradientStops.Add(new GradientStop(end ? Colors.Transparent : Colors.Black, 1));
        return brush;
    }
}