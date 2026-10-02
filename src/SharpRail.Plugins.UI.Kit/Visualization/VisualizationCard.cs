using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>Renders a visualize tool's diagram or comparison arguments and reports the renderer's verdict.</summary>
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
        return new VisualizationCardFrame(new DiagramView(args, interactive, onRender), false);
    }
}

internal sealed partial class DiagramView : DockPanel
{
    internal DiagramView(JsonElement args, bool interactive, Action<string?>? onRender)
    {
        AvaloniaXamlLoader.Load(this);
        var heading = this.FindControl<TextBlock>("Heading")!;
        heading.Text = VisualizationArgs.String(args, "title");
        heading.IsVisible = !interactive && heading.Text.Length > 0;
        var source = VisualizationArgs.String(args, "mermaid");
        this.FindControl<ContentControl>("Drawing")!.Content = source.Length == 0
            ? new VisualizationCardFrame(null, false, "(no diagram)")
            : new MermaidView(source, interactive, onRender);
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