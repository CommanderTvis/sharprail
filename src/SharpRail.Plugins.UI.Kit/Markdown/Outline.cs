using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>The heading column a Markdown document and a rendered Markdown diff show beside the preview.</summary>
public static class Outline
{
    // Read from the parsed source, so each entry knows both its rendered anchor and its source line.
    // A rendered diff shares it without a source side to reveal.
    public static void Fill(StackPanel entries, MarkdownPreview preview, string sourceText, Action<int>? revealSource)
    {
        if (preview.SpecTitle is { } title)
        {
            var line = Array.FindIndex(sourceText.Split('\n'), text => text.StartsWith("title:", StringComparison.Ordinal));
            entries.Children.Add(Entry(1, title.Text ?? "", Math.Max(0, line), preview, preview.ScrollToTitle, revealSource));
        }
        foreach (var (level, text, id, line) in preview.Headings)
            entries.Children.Add(Entry(level, text, line, preview, () => preview.ScrollToAnchor(id), revealSource));
        if (entries.Children.Count == 0) entries.Children.Add(Ui.Text("No headings", Ui.Hint, 12));
    }

    private static Button Entry(int level, string text, int line, MarkdownPreview preview, Action scrollPreview, Action<int>? revealSource)
    {
        var label = Ui.Text(text, Ui.Muted, 12);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var entry = new Button
        {
            Name = "OutlineEntry",
            Content = label,
            Tag = line,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Padding = new Thickness(6 + (level - 1) * 12, 2, 6, 2),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetName(entry, text);
        ToolTip.SetTip(entry, text);
        // Jumps whichever sides are showing, and selects neither.
        entry.Click += (_, _) =>
        {
            if (preview.IsVisible) scrollPreview();
            revealSource?.Invoke(line);
        };
        return entry;
    }
}