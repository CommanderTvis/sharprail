using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Svg.Skia;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>A Mermaid diagram rendered off the UI thread, with inline or pane pan and zoom and a render verdict.</summary>
public sealed partial class MermaidView : ContentControl
{
    private readonly string source;
    private readonly bool interactive;
    private readonly Action<string?>? onRender;
    private CancellationTokenSource? rendering;
    private SvgSource? shown;
    private PanZoomView? view;

    /// <summary>Creates a diagram from raw Mermaid source; the callback receives null on success or its parse error.</summary>
    public MermaidView(string source, bool interactive = false, Action<string?>? onRender = null)
    {
        this.source = source;
        this.interactive = interactive;
        this.onRender = onRender;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<SelectableTextBlock>("SourceText")!.Text = source;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Ui.ThemeChanged += Render;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Ui.ThemeChanged -= Render;
        CancelRendering();
        this.FindControl<ContentControl>("Drawing")!.Content = null;
        view = null;
        shown?.Dispose();
        shown = null;
    }

    private async void Render()
    {
        CancelRendering();
        var lifetime = rendering = new CancellationTokenSource();
        var options = MermaidRenderer.Options();
        SvgSource? svg = null;
        MermaidRenderer.Result result;
        try
        {
            result = await Task.Run(() =>
            {
                var rendered = MermaidRenderer.Render(source, options);
                if (rendered.Svg is not null) svg = SvgSource.LoadFromSvg(rendered.Svg);
                return rendered;
            }, lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        finally { if (lifetime.IsCancellationRequested) svg?.Dispose(); }
        if (lifetime.IsCancellationRequested) return;
        var failed = result.Error is not null || svg?.Picture is null;
        this.FindControl<TextBlock>("Loading")!.IsVisible = false;
        this.FindControl<StackPanel>("MermaidError")!.IsVisible = failed;
        if (failed)
        {
            svg?.Dispose();
            var error = result.Error ?? "the SVG could not be read.";
            this.FindControl<TextBlock>("FailureText")!.Text = "Diagram failed to render: " + error;
            this.FindControl<ContentControl>("Drawing")!.IsVisible = false;
            onRender?.Invoke(error);
            return;
        }
        var previous = shown;
        shown = svg;
        if (view is null) view = new PanZoomView(svg!, capped: !interactive);
        else view.Update(svg!);
        var drawing = this.FindControl<ContentControl>("Drawing")!;
        drawing.Content = view;
        drawing.IsVisible = true;
        previous?.Dispose();
        onRender?.Invoke(null);
    }

    private void CancelRendering()
    {
        rendering?.Cancel();
        rendering?.Dispose();
        rendering = null;
    }
}