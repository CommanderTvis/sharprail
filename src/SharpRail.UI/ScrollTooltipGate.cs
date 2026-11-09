#if !ANDROID && !BROWSER
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SharpRail.UI;

/// <summary>
/// Keeps tooltips quiet while content scrolls under a still pointer (wheel, trackpad or scrollbar thumb drag): every row that
/// passes under the pointer would otherwise open its own tooltip. While a scroll burst lasts, no tooltip opens and the open one
/// closes; <see cref="Quiet"/> after the last scroll delta the tip of the element under the pointer opens after its normal
/// show delay. Times are passed in so a check can drive the state with a fake clock.
/// </summary>
internal sealed class ScrollTooltipGate
{
    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private TimeSpan lastScroll = TimeSpan.MinValue;

    /// <summary>Records a scroll delta at <paramref name="now"/>.</summary>
    internal void Scrolled(TimeSpan now) => lastScroll = now;

    /// <summary>True while tooltips are suppressed: a scroll delta arrived less than <see cref="Quiet"/> ago.</summary>
    internal bool IsScrolling(TimeSpan now) => lastScroll != TimeSpan.MinValue && now - lastScroll < Quiet;

    private static readonly ScrollTooltipGate Gate = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly List<WeakReference<TopLevel>> Touched = [];
    private static WeakReference<Control>? open;
    private static TopLevel? pointerWindow;
    private static Point pointer;
    private static DispatcherTimer? settle, show;

    /// <summary>Installs the app-wide handlers once, at startup.</summary>
    internal static void Start()
    {
        settle = new DispatcherTimer { Interval = Quiet };
        settle.Tick += (_, _) => Settled();
        InputElement.PointerWheelChangedEvent.AddClassHandler(typeof(InputElement), (sender, e) => Scroll(e), RoutingStrategies.Tunnel, true);
        Thumb.DragDeltaEvent.AddClassHandler(typeof(Thumb), (sender, e) =>
        {
            if (sender is Thumb thumb && thumb.FindAncestorOfType<ScrollBar>() is not null) Scroll(e);
        }, RoutingStrategies.Bubble, true);
        InputElement.PointerMovedEvent.AddClassHandler(typeof(InputElement), (sender, e) =>
        {
            if (e is PointerEventArgs moved && moved.Source is Visual { } visual && TopLevel.GetTopLevel(visual) is { } top)
            {
                pointerWindow = top;
                pointer = moved.GetPosition(top);
            }
        }, RoutingStrategies.Tunnel, true);
        // A tip the service opens during a burst is shut again at once.
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (change.NewValue is not true) return;
            if (Gate.IsScrolling(Clock.Elapsed)) ToolTip.SetIsOpen(control, false);
            else open = new WeakReference<Control>(control);
        });
    }

    private static void Scroll(RoutedEventArgs e)
    {
        if (e.Source is not Visual visual || TopLevel.GetTopLevel(visual) is not { } top) return;
        if (e is PointerWheelEventArgs wheel) { pointerWindow = top; pointer = wheel.GetPosition(top); }
        Gate.Scrolled(Clock.Elapsed);
        show?.Stop();
        if (open is not null && open.TryGetTarget(out var tipped)) ToolTip.SetIsOpen(tipped, false);
        open = null;
        ToolTip.SetServiceEnabled(top, false);
        if (!Touched.Exists(t => t.TryGetTarget(out var known) && known == top)) Touched.Add(new WeakReference<TopLevel>(top));
        settle!.Stop();
        settle.Start();
    }

    private static void Settled()
    {
        settle!.Stop();
        foreach (var weak in Touched)
            if (weak.TryGetTarget(out var top)) top.ClearValue(ToolTip.ServiceEnabledProperty);
        Touched.Clear();
        // The pointer did not move, so no pointer-enter will come: arm the tip under it ourselves.
        if (pointerWindow is not { } window || window.GetVisualAt(pointer) is not { } hit) return;
        for (var visual = hit; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control control || ToolTip.GetTip(control) is null) continue;
            show = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(control)) };
            show.Tick += (_, _) =>
            {
                show!.Stop();
                if (control.IsPointerOver && !Gate.IsScrolling(Clock.Elapsed)) ToolTip.SetIsOpen(control, true);
            };
            show.Start();
            return;
        }
    }
}
#endif