using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Svg.Skia;

namespace SharpRail.Plugins.UI.Kit.Visualization;

internal sealed partial class PanZoomView : Grid
{
    private readonly ScrollViewer viewer;
    private readonly Image image;
    private readonly DiagramSelection selection;
    private readonly string? source;
    private readonly TextBlock level;
    private readonly bool capped;
    private SvgSource diagram;
    private double scale = 1;
    private DiagramText text = DiagramText.Empty;
    private Point? drag;
    private bool dragged;
    private int? anchor;

    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    /// <summary>A navigable diagram; with its text it is selectable and copyable, and with its source it offers that too.</summary>
    internal PanZoomView(SvgSource diagram, bool capped, DiagramText? shown = null, string? source = null)
    {
        this.diagram = diagram;
        this.capped = capped;
        this.source = source;
        AvaloniaXamlLoader.Load(this);
        viewer = this.FindControl<ScrollViewer>("MermaidPanZoom")!;
        image = this.FindControl<Image>("DiagramImage")!;
        selection = this.FindControl<DiagramSelection>("DiagramSelection")!;
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
        fullscreen.Click += (_, _) => { if (TopLevel.GetTopLevel(this) is Window window) MermaidDialog.Show(window, this.diagram, this.text, this.source); };
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
        // A press on a label selects its text, as in a document; anywhere else it pans the drawing.
        viewer.PointerPressed += (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Mouse || !e.GetCurrentPoint(viewer).Properties.IsLeftButtonPressed) return;
            viewer.Focus();
            var point = selection.ToPicture(e.GetPosition(selection));
            if (text.RunAt(point, slop: 2) is { } run)
            {
                if (e.ClickCount >= 2) { anchor = null; selection.Select(run.Start, run.End); }
                else
                {
                    var hit = text.HitTest(point);
                    anchor = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && selection.Start != selection.End
                        ? (hit < selection.End ? selection.End : selection.Start)
                        : hit;
                    selection.Select(anchor.Value, hit);
                }
            }
            else
            {
                drag = e.GetPosition(viewer);
                dragged = false;
            }
            e.Pointer.Capture(viewer);
            e.Handled = true;
        };
        viewer.PointerMoved += (_, e) =>
        {
            if (anchor is { } start)
            {
                selection.Select(start, text.HitTest(selection.ToPicture(e.GetPosition(selection))));
                return;
            }
            if (drag is not { } from)
            {
                viewer.Cursor = text.RunAt(selection.ToPicture(e.GetPosition(selection)), slop: 2) is null ? PanCursor : TextCursor;
                return;
            }
            var position = e.GetPosition(viewer);
            viewer.Offset = new Vector(viewer.Offset.X - (position.X - from.X), viewer.Offset.Y - (position.Y - from.Y));
            dragged |= position != from;
            drag = position;
        };
        viewer.PointerReleased += (_, e) =>
        {
            // A plain click beside the labels clears the selection, as clicking a document's margin does.
            if (drag is not null && !dragged) selection.Select(0, 0);
            drag = null;
            anchor = null;
            e.Pointer.Capture(null);
        };
        viewer.PointerCaptureLost += (_, _) => { drag = null; anchor = null; };
        viewer.KeyDown += (_, e) =>
        {
            var keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
            if (keys is null) return;
            // The platform's gestures, or its command key with the letter, as the app's own shortcuts treat it.
            bool Is(List<KeyGesture> gestures, Key key) => gestures.Any(gesture => gesture.Matches(e)) || e.Key == key && e.KeyModifiers == keys.CommandModifiers;
            if (Is(keys.Copy, Key.C) && selection.Start != selection.End) { _ = CopyAsync(SelectedText); e.Handled = true; }
            else if (Is(keys.SelectAll, Key.A)) { SelectAll(); e.Handled = true; }
        };
        viewer.ContextRequested += (_, e) =>
        {
            if (text.Length == 0 && source is null) return;
            var selected = SelectedText;
            var menu = new ContextMenu { Name = "MermaidDiagramActions" };
            menu.Items.Add(Ui.Menu("Copy", () => _ = CopyAsync(selected), selected.Length > 0));
            menu.Items.Add(Ui.Menu("Select All", SelectAll, text.Length > 0));
            menu.Items.Add(Ui.Menu("Copy All Text", () => _ = CopyAsync(text.Text(0, text.Length)), text.Length > 0));
            if (source is { Length: > 0 } mermaid) menu.Items.Add(Ui.Menu("Copy Mermaid Source", () => _ = CopyAsync(mermaid)));
            viewer.ContextMenu = menu;
            menu.Open(viewer);
            e.Handled = true;
        };
        Update(diagram, shown);
    }

    /// <summary>The diagram's text runs, in picture coordinates.</summary>
    internal DiagramText Text => text;

    /// <summary>The drawing's bounds in picture coordinates.</summary>
    internal Rect Picture => diagram.Picture?.CullRect is { } cull ? new Rect(cull.Left, cull.Top, cull.Width, cull.Height) : default;

    /// <summary>The selected diagram text, as it would be copied.</summary>
    internal string SelectedText => text.Text(selection.Start, selection.End);

    internal void SelectAll() => selection.Select(0, text.Length);

    internal void Update(SvgSource drawing, DiagramText? shown)
    {
        diagram = drawing;
        text = shown ?? DiagramText.Empty;
        image.Source = new SvgImage { Source = drawing };
        selection.Show(text, Picture);
        Apply();
    }

    private async Task CopyAsync(string copied)
    {
        if (copied.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(copied);
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