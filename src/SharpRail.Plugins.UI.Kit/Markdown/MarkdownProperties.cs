using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit.Markdown;

public sealed partial class MarkdownPreview
{
    private readonly List<(MarkdownLink Button, string Id)> specLinks = [];

    private MarkdownLink LinkButton(string label, FontWeight weight, FontStyle style)
    {
        var text = Ui.Text(label, Ui.Accent, context.FontSize);
        text.FontWeight = weight; text.FontStyle = style;
        return new MarkdownLink(text)
        {
            Content = text,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
    }

    // A [[id]] inside a spec resolves through the context when clicked; an id it cannot resolve renders disabled.
    private MarkdownLink SpecLink(string id, string label, FontWeight weight, FontStyle style)
    {
        var button = LinkButton(label, weight, style);
        button.Name = "SpecLink";
        ToolTip.SetTip(button, id);
        button.Click += (_, _) =>
        {
            if (context.ResolveSpecLink(id) is { } target) context.Navigate(target, null);
        };
        specLinks.Add((button, id));
        return button;
    }

    /// <summary>Asks the context again which <c>[[id]]</c> links resolve, after what it resolves against changed.</summary>
    public void RefreshSpecLinks()
    {
        foreach (var (button, id) in specLinks)
        {
            var known = context.ResolveSpecLink(id) is not null;
            button.IsEnabled = known;
            ToolTip.SetTip(button, known ? id : $"No spec with id {id} in this workspace");
            ToolTip.SetShowOnDisabled(button, !known);
        }
    }

    // Read-only, Obsidian-style: key/value rows with list values as chips, collapsible. With a previous block (a
    // rendered diff), added, removed and changed values carry the same ins/del marks as the prose.
    private Control Properties(FrontmatterBlock? block, FrontmatterBlock? previous)
    {
        var rows = new Grid { Name = "FrontmatterRows", ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        var readable = (block?.Readable ?? true) && (previous?.Readable ?? true);
        if (!readable)
        {
            var raw = Code(block?.Raw ?? previous!.Raw);
            raw.Name = "FrontmatterRaw";
            Ui.Place(rows, raw);
            Grid.SetColumnSpan(raw, 2);
        }
        else
        {
            var current = block?.Properties ?? [];
            var before = previous?.Properties;
            var keys = current.Select(property => property.Key)
                .Concat(before?.Select(property => property.Key) ?? []).Distinct().ToArray();
            for (var index = 0; index < keys.Length; index++)
            {
                var now = current.FirstOrDefault(property => property.Key == keys[index]);
                var then = before is null ? now : before.FirstOrDefault(property => property.Key == keys[index]);
                rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var key = Ui.Text(keys[index], Ui.Muted, context.FontSize - 1);
                key.Name = "FrontmatterKey";
                key.Margin = new Thickness(0, 4, 16, 4);
                key.VerticalAlignment = VerticalAlignment.Top;
                Ui.Place(rows, key, index);
                var value = new WrapPanel { Name = "FrontmatterValue", Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
                if (before is null || Same(now, then)) AddValue(value, now!, null);
                else
                {
                    if (then is not null) AddValue(value, then, "del");
                    if (now is not null) AddValue(value, now, "ins");
                }
                var row = new Border { Name = "FrontmatterProperty", Child = value };
                Ui.Place(rows, row, index, 1);
            }
        }
        var chevron = Ui.Icon("arrowDown", Ui.Muted, 14);
        var label = Ui.Text("Properties", Ui.Muted, 12);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        header.Children.Add(chevron); header.Children.Add(label);
        var toggle = new Button
        {
            Name = "FrontmatterToggle",
            Content = header,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(toggle, "Toggle properties");
        toggle.Click += (_, _) =>
        {
            rows.IsVisible = !rows.IsVisible;
            chevron.RenderTransform = rows.IsVisible ? null : new RotateTransform(-90);
        };
        var panel = new StackPanel();
        panel.Children.Add(toggle);
        panel.Children.Add(rows);
        return new Border
        {
            Name = "FrontmatterProperties",
            Child = panel,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Margin = new Thickness(0, 0, 0, 16)
        };
    }

    private static bool Same(FrontmatterProperty? left, FrontmatterProperty? right) =>
        left is not null && right is not null && left.Text == right.Text &&
        (left.Items ?? []).SequenceEqual(right.Items ?? []) && (left.Entries ?? []).SequenceEqual(right.Entries ?? []);

    private void AddValue(WrapPanel target, FrontmatterProperty property, string? mark)
    {
        diffMark = mark;
        if (property.Items is { } items)
            foreach (var item in items)
            {
                var chip = Ui.Chip(ValueText(item), new Thickness(6, 1));
                chip.Name = "FrontmatterListItem";
                chip.Margin = new Thickness(0, 2, 4, 2);
                target.Children.Add(chip);
            }
        else if (property.Entries is { } entries)
        {
            var map = new StackPanel();
            foreach (var entry in entries) map.Children.Add(ValueText($"{entry.Key}: {entry.Value}"));
            target.Children.Add(map);
        }
        else target.Children.Add(ValueText(property.Text ?? ""));
        diffMark = null;
    }

    private SelectableTextBlock ValueText(string text)
    {
        var block = new SelectableTextBlock { Foreground = Ui.TextBrush, FontSize = context.FontSize - 1, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 2) };
        block.Inlines!.Add(Mark(new Run(text)));
        return block;
    }
}