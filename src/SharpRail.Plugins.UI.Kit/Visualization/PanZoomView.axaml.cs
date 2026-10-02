using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Svg.Skia;

namespace SharpRail.Plugins.UI.Kit.Visualization;

internal sealed partial class PanZoomView : Grid
{
    private readonly ScrollViewer viewer;
    private readonly Image image;
    private readonly TextBlock level;
    private readonly bool capped;
    private SvgSource diagram;
    private double scale = 1;
    private Point? drag;

    internal PanZoomView(SvgSource diagram, bool capped)
    {
        this.diagram = diagram;
        this.capped = capped;
        AvaloniaXamlLoader.Load(this);
        viewer = this.FindControl<ScrollViewer>("MermaidPanZoom")!;
        image = this.FindControl<Image>("DiagramImage")!;
        level = this.FindControl<TextBlock>("MermaidZoomLevel")!;
        if (capped)
        {
            viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            MaxHeight = MermaidDialog.InlineCap;
        }
        Wire("MermaidZoomOut", "subtract", () => Zoom(scale / ZoomGesture.ScaleStep));
        Wire("MermaidZoomIn", "add", () => Zoom(scale * ZoomGesture.ScaleStep));
        Wire("MermaidZoomReset", "arrowGoBack", () => { scale = 1; viewer.Offset = default; Apply(); });
        var fullscreen = this.FindControl<Button>("MermaidFullscreen")!;
        fullscreen.Content = Ui.Icon("fullscreen", size: 14);
        fullscreen.IsVisible = capped;
        fullscreen.Click += (_, _) => { if (TopLevel.GetTopLevel(this) is Window window) MermaidDialog.Show(window, this.diagram); };
        viewer.SizeChanged += (_, _) => Apply();
        viewer.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!ZoomGesture.IsZoom(e.KeyModifiers)) return;
            Zoom(ZoomGesture.ForWheel(scale, e.Delta.Y));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        viewer.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            Zoom(scale * (1 + e.Delta.X));
            e.Handled = true;
        });
        viewer.PointerPressed += (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Mouse || !e.GetCurrentPoint(viewer).Properties.IsLeftButtonPressed) return;
            drag = e.GetPosition(viewer);
            e.Pointer.Capture(viewer);
        };
        viewer.PointerMoved += (_, e) =>
        {
            if (drag is not { } start) return;
            var position = e.GetPosition(viewer);
            viewer.Offset = new Vector(viewer.Offset.X - (position.X - start.X), viewer.Offset.Y - (position.Y - start.Y));
            drag = position;
        };
        viewer.PointerReleased += (_, e) => { drag = null; e.Pointer.Capture(null); };
        viewer.PointerCaptureLost += (_, _) => drag = null;
        Update(diagram);
    }

    internal void Update(SvgSource source)
    {
        diagram = source;
        image.Source = new SvgImage { Source = source };
        Apply();
    }

    private void Wire(string name, string icon, Action action)
    {
        var button = this.FindControl<Button>(name)!;
        button.Content = Ui.Icon(icon, size: 16);
        button.Click += (_, _) => action();
    }

    private void Zoom(double requested) { scale = ZoomGesture.Clamp(requested); Apply(); }

    private void Apply()
    {
        var natural = diagram.Picture?.CullRect;
        var width = Math.Max(1, viewer.Viewport.Width);
        var ratio = natural is { Width: > 0 } size ? size.Height / size.Width : 1;
        image.Width = width * scale;
        image.Height = width * scale * ratio;
        if (capped)
        {
            var cap = TopLevel.GetTopLevel(this)?.Bounds.Height * 0.6 ?? MermaidDialog.InlineCap;
            Height = Math.Min(width * ratio, Math.Min(MermaidDialog.InlineCap, cap));
        }
        level.Text = $"{Math.Round(scale * 100)}%";
    }
}