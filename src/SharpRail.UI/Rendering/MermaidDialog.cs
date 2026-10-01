using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;

using SharpRail.UI.Panels;

namespace SharpRail.UI.Rendering;

/// <summary>Full-screen Mermaid view: 100% fits the viewer width, with drag panning, pinch and 25–500% zoom as in the reference.</summary>
internal static class MermaidDialog
{
    private const double MinZoom = 0.25, MaxZoom = 5;
    internal const double InlineCap = 480;

    internal static void Show(Window owner, SvgSource diagram)
    {
        var window = Dialogs.Create("Diagram", Math.Max(480, owner.Bounds.Width * 0.95));
        // The viewer fills a fixed fraction of the owner rather than sizing the dialog to its content.
        window.SizeToContent = SizeToContent.Manual;
        window.Height = Math.Max(360, owner.Bounds.Height * 0.9);
        window.CanResize = true;
        var zoom = 1.0;
        var image = new Image { Source = new SvgImage { Source = diagram }, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var viewer = new ScrollViewer
        {
            Name = "MermaidFullscreenViewer",
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = image,
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        var level = ZoomLevel();
        var controls = ZoomControls(level, factor => Zoom(factor), () => { zoom = 1; Apply(); viewer.Offset = default; });

        void Apply()
        {
            image.Width = Math.Max(1, viewer.Viewport.Width * zoom);
            level.Text = $"{Math.Round(zoom * 100)}%";
        }
        void Zoom(double factor) { zoom = Math.Clamp(zoom * factor, MinZoom, MaxZoom); Apply(); }
        var stage = new Grid { Height = window.Height - 110 };
        stage.Children.Add(viewer);
        stage.Children.Add(controls);
        window.FindControl<StackPanel>("DialogFields")!.Children.Add(stage);
        window.SizeChanged += (_, _) => stage.Height = Math.Max(120, window.Bounds.Height - 110);
        viewer.SizeChanged += (_, _) => Apply();

        viewer.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            Zoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        }, handledEventsToo: true);
        // A trackpad pinch reports each step's change in magnification; claiming it keeps the pinch on the diagram.
        viewer.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            Zoom(1 + e.Delta.X);
            e.Handled = true;
        });
        Point? drag = null;
        viewer.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(viewer).Properties.IsLeftButtonPressed) return;
            drag = e.GetPosition(viewer); e.Pointer.Capture(viewer);
        };
        viewer.PointerMoved += (_, e) =>
        {
            if (drag is not { } start) return;
            var position = e.GetPosition(viewer);
            viewer.Offset = new Vector(viewer.Offset.X - (position.X - start.X), viewer.Offset.Y - (position.Y - start.Y));
            drag = position;
        };
        viewer.PointerReleased += (_, e) => { drag = null; e.Pointer.Capture(null); };

        window.Opened += (_, _) => Apply();
        _ = window.ShowDialog(owner);
    }

    /// <summary>
    /// The diagram inline in a document: natural size up to the width it is given, in a box that stops at
    /// <see cref="InlineCap"/> and clips. Zoom scales the drawing, never the box; drag pans inside it, and a
    /// plain wheel carries on scrolling the document.
    /// </summary>
    internal static Control Inline(SvgSource diagram, Size natural, Control fullscreen)
    {
        var zoom = 1.0;
        var pan = new Point();
        var image = new Image { Name = "MermaidDiagram", Source = new SvgImage { Source = diagram }, Stretch = Stretch.Fill };
        var canvas = new Canvas();
        canvas.Children.Add(image);
        var box = new Border { Name = "MermaidInline", ClipToBounds = true, Child = canvas, Cursor = new Cursor(StandardCursorType.SizeAll), Background = Brushes.Transparent };
        var level = ZoomLevel();
        var controls = ZoomControls(level, factor => Zoom(factor), () => { zoom = 1; pan = default; Apply(); });
        controls.Margin = new Thickness(4);
        controls.VerticalAlignment = VerticalAlignment.Top;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(controls);
        tools.Children.Add(fullscreen);

        void Apply()
        {
            var fitted = box.Bounds.Width > 0 ? Math.Min(natural.Width, box.Bounds.Width) : natural.Width;
            var aspect = natural.Height / Math.Max(1, natural.Width);
            image.Width = Math.Max(1, fitted * zoom);
            image.Height = Math.Max(1, fitted * zoom * aspect);
            box.Height = Math.Min(fitted * aspect, InlineCap);
            pan = new Point(
                Math.Clamp(pan.X, 0, Math.Max(0, image.Width - box.Bounds.Width)),
                Math.Clamp(pan.Y, 0, Math.Max(0, image.Height - box.Height)));
            Canvas.SetLeft(image, -pan.X); Canvas.SetTop(image, -pan.Y);
            level.Text = $"{Math.Round(zoom * 100)}%";
        }
        void Zoom(double factor) { zoom = Math.Clamp(zoom * factor, MinZoom, MaxZoom); Apply(); }
        box.SizeChanged += (_, e) => { if (e.WidthChanged) Apply(); };
        box.PointerWheelChanged += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            Zoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        };
        Point? drag = null;
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            drag = e.GetPosition(box); e.Pointer.Capture(box);
        };
        box.PointerMoved += (_, e) =>
        {
            if (drag is not { } start) return;
            var position = e.GetPosition(box);
            pan = new Point(pan.X - (position.X - start.X), pan.Y - (position.Y - start.Y));
            drag = position;
            Apply();
        };
        box.PointerReleased += (_, e) => { drag = null; e.Pointer.Capture(null); };
        Apply();
        var diagramView = new Grid();
        diagramView.Children.Add(box);
        diagramView.Children.Add(tools);
        return diagramView;
    }

    private static TextBlock ZoomLevel()
    {
        var label = Ui.Text("100%", Ui.Muted, 12);
        label.Name = "MermaidZoomLevel";
        label.MinWidth = 36; label.TextAlignment = TextAlignment.Center;
        return label;
    }

    private static Border ZoomControls(TextBlock label, Action<double> zoom, Action reset)
    {
        Button Control(string name, string text, string icon, Action action)
        {
            var button = new Button
            {
                Name = name,
                Content = Ui.Icon(icon, size: 14),
                Padding = new Thickness(4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4)
            };
            AutomationProperties.SetName(button, text);
            ToolTip.SetTip(button, text);
            button.Click += (_, _) => action();
            return button;
        }
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Control("MermaidZoomOut", "Zoom out", "subtract", () => zoom(1 / 1.25)));
        controls.Children.Add(label);
        controls.Children.Add(Control("MermaidZoomIn", "Zoom in", "add", () => zoom(1.25)));
        controls.Children.Add(Control("MermaidZoomReset", "Reset zoom", "arrowGoBack", reset));
        return new Border
        {
            Child = controls,
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
    }
}
