using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;

using SharpRail.UI.Panels;

namespace SharpRail.UI.Rendering;

/// <summary>Full-screen Mermaid view: 100% fits the viewer width, with drag panning and 25–500% zoom as in the reference.</summary>
internal static class MermaidDialog
{
    private const double MinZoom = 0.25, MaxZoom = 5;

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
        var level = Ui.Text("100%", Ui.Muted, 12);
        level.Name = "MermaidZoomLevel";
        level.MinWidth = 36; level.TextAlignment = TextAlignment.Center;

        void Apply()
        {
            image.Width = Math.Max(1, viewer.Viewport.Width * zoom);
            level.Text = $"{Math.Round(zoom * 100)}%";
        }
        void Zoom(double factor) { zoom = Math.Clamp(zoom * factor, MinZoom, MaxZoom); Apply(); }
        Button Control(string name, string label, string icon, Action action)
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
            AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, label);
            button.Click += (_, _) => action();
            return button;
        }
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Control("MermaidZoomOut", "Zoom out", "subtract", () => Zoom(1 / 1.25)));
        controls.Children.Add(level);
        controls.Children.Add(Control("MermaidZoomIn", "Zoom in", "add", () => Zoom(1.25)));
        controls.Children.Add(Control("MermaidZoomReset", "Reset zoom", "arrowGoBack", () =>
        {
            zoom = 1; Apply(); viewer.Offset = default;
        }));
        var stage = new Grid { Height = window.Height - 110 };
        stage.Children.Add(viewer);
        stage.Children.Add(new Border
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
        });
        window.FindControl<StackPanel>("DialogFields")!.Children.Add(stage);
        window.SizeChanged += (_, _) => stage.Height = Math.Max(120, window.Bounds.Height - 110);
        viewer.SizeChanged += (_, _) => Apply();

        viewer.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            Zoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        }, handledEventsToo: true);
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
}