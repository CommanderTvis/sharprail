using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>Renders a visualize tool's diagram, SVG, Markdown or comparison arguments and reports the renderer's verdict.</summary>
public static class VisualizationCard
{
    /// <summary>Creates a drawing; interactive diagrams fill their pane with pan and zoom controls.</summary>
    public static Control Create(JsonElement args, bool interactive = false, Action<string?>? onRender = null)
    {
        if (VisualizationArgs.String(args, "type") == "comparison")
        {
            var options = VisualizationArgs.ComparisonOptions(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("options", out var value) ? value : default);
            var comparison = new VisualizationCardFrame(options.Count == 0 ? null : new ComparisonView(args), interactive, options.Count == 0 ? "(no options)" : null);
            onRender?.Invoke(null);
            return comparison;
        }
        if (VisualizationArgs.String(args, "type") == "markdown")
        {
            var text = VisualizationArgs.String(args, "markdown");
            var title = VisualizationArgs.String(args, "title");
            if (title.Length > 0 && !interactive) text = "# " + title + "\n\n" + text;
            var markdown = new VisualizationCardFrame(text.Length == 0 ? null : new MarkdownPreview(text, "visualize.md",
                new(static (_, _) => ValueTask.FromResult<byte[]?>(null), static _ => null, static (_, _) => { }, Ui.FontSize, 100, false)), false, text.Length == 0 ? "(no markdown)" : null);
            onRender?.Invoke(null);
            return markdown;
        }
        return new VisualizationCardFrame(new DiagramView(args, interactive, onRender), false);
    }

    /// <summary>The text the user has selected in a drawing: diagram labels or card text, empty when none.</summary>
    public static string SelectedText(Control card) =>
        card.GetVisualDescendants().Select(control => control switch
        {
            MarkdownPreview preview => preview.SelectedText,
            PanZoomView diagram => diagram.SelectedText,
            SelectableTextBlock block => block.SelectedText,
            _ => ""
        }).FirstOrDefault(text => text.Length > 0) ?? "";
}

internal sealed partial class DiagramView : DockPanel
{
    internal DiagramView(JsonElement args, bool interactive, Action<string?>? onRender)
    {
        AvaloniaXamlLoader.Load(this);
        var heading = this.FindControl<TextBlock>("Heading")!;
        heading.Text = VisualizationArgs.String(args, "title");
        heading.IsVisible = !interactive && heading.Text.Length > 0;
        var svg = VisualizationArgs.String(args, "type") == "svg";
        var source = VisualizationArgs.String(args, svg ? "svg" : "mermaid");
        this.FindControl<ContentControl>("Drawing")!.Content = source.Length == 0
            ? new VisualizationCardFrame(null, false, "(no diagram)")
            : svg ? MermaidView.FromSvg(source, interactive, onRender) : new MermaidView(source, interactive, onRender);
    }
}

internal sealed partial class VisualizationCardFrame : Border
{
    internal VisualizationCardFrame(Control? body, bool scroll, string? note = null)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>(scroll ? "ScrolledContent" : "CardContent")!.Content = body;
        this.FindControl<ScrollViewer>("CardScroll")!.IsVisible = scroll && body is not null;
        this.FindControl<ContentControl>("CardContent")!.IsVisible = !scroll && body is not null;
        var empty = this.FindControl<TextBlock>("Empty")!;
        empty.Text = note;
        empty.IsVisible = note is not null;
    }
}