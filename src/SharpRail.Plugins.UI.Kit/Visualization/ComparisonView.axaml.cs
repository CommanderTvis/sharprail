using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit.Visualization;

internal sealed partial class ComparisonView : StackPanel
{
    internal ComparisonView(JsonElement args)
    {
        AvaloniaXamlLoader.Load(this);
        var heading = this.FindControl<TextBlock>("Heading")!;
        heading.Text = VisualizationArgs.String(args, "title");
        heading.IsVisible = heading.Text.Length > 0;
        var grid = this.FindControl<UniformGrid>("Options")!;
        grid.SizeChanged += (_, e) => grid.Columns = e.NewSize.Width >= 640 ? 2 : 1;
        var options = VisualizationArgs.ComparisonOptions(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("options", out var value) ? value : default);
        foreach (var option in options) grid.Children.Add(new ComparisonOptionControl(option));
    }
}

internal sealed partial class ComparisonOptionControl : Border
{
    internal ComparisonOptionControl(ComparisonOptionView option)
    {
        AvaloniaXamlLoader.Load(this);
        BorderBrush = option.Recommended ? Ui.Accent : Ui.BorderBrush;
        Background = option.Recommended ? Ui.Elevated : Brushes.Transparent;
        this.FindControl<TextBlock>("Label")!.Text = option.Name;
        this.FindControl<Border>("ComparisonRecommended")!.IsVisible = option.Recommended;
        var description = this.FindControl<TextBlock>("Description")!;
        description.Text = option.Description;
        description.IsVisible = option.Description is { Length: > 0 };
        this.FindControl<StackPanel>("Pros")!.IsVisible = option.Pros.Count > 0;
        this.FindControl<StackPanel>("Cons")!.IsVisible = option.Cons.Count > 0;
        this.FindControl<ContentControl>("Diagram")!.IsVisible = option.Mermaid is { Length: > 0 };
        foreach (var point in option.Pros) this.FindControl<StackPanel>("Pros")!.Children.Add(new ComparisonPoint(point, true));
        foreach (var point in option.Cons) this.FindControl<StackPanel>("Cons")!.Children.Add(new ComparisonPoint(point, false));
        if (option.Mermaid is { Length: > 0 } source) this.FindControl<ContentControl>("Diagram")!.Content = new MermaidView(source);
    }
}

internal sealed partial class ComparisonPoint : DockPanel
{
    internal ComparisonPoint(string text, bool pro)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("Label")!.Text = text;
        this.FindControl<ContentControl>("Glyph")!.Content = Ui.Icon(pro ? "check" : "close", pro ? Ui.Success : Ui.Danger, 12);
    }
}