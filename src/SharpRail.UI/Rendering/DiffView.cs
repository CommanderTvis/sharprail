using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpRail.UI.Rendering;

internal sealed partial class DiffView : Grid
{
    private sealed record Hunk(int OldStart, int OldCount, string Header, List<string> Lines);

    private readonly Grid body = new();
    private readonly ToggleButton split = Toggle("DiffSplit", "layout", "Side-by-side diff");
    private readonly ToggleButton inline = Toggle("DiffInline", "list", "Inline diff");
    private readonly ToggleButton whitespace = Toggle("DiffWhitespace", "collapseVertical", "Hide whitespace changes");
    private string text;

    internal DiffView(string text, string path, bool splitByDefault)
    {
        this.text = text;
        Name = "DiffPane";
        RowDefinitions = new RowDefinitions("32,*");
        var chip = new Border
        {
            Name = "DiffPath",
            Background = Ui.Elevated,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Text(path, Ui.TextBrush, 12)
        };
        var copy = new Button
        {
            Name = "DiffCopy",
            Content = Ui.Icon("file", size: 14),
            Width = 28,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetName(copy, "Copy diff");
        ToolTip.SetTip(copy, "Copy diff");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(this.text);
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(split); controls.Children.Add(inline); controls.Children.Add(whitespace); controls.Children.Add(copy);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0, 0, 0) };
        Ui.Place(header, chip); Ui.Place(header, controls, 0, 1);
        Ui.Place(this, header);
        Ui.Place(this, body, 1);
        split.IsChecked = splitByDefault; inline.IsChecked = !splitByDefault;
        split.Click += (_, _) => { split.IsChecked = true; inline.IsChecked = false; Render(); };
        inline.Click += (_, _) => { inline.IsChecked = true; split.IsChecked = false; Render(); };
        whitespace.Click += (_, _) => Render();
        Render();
    }

    internal void Update(string diff)
    {
        if (diff == text) return;
        text = diff; Render();
    }

    private static ToggleButton Toggle(string name, string icon, string tooltip)
    {
        var button = new ToggleButton
        {
            Name = name,
            Content = Ui.Icon(icon, size: 14),
            Width = 28,
            Height = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent
        };
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "PointerOver", "Pressed" })
            button.Resources["ToggleButtonBackground" + state] = Ui.Hover;
        AutomationProperties.SetName(button, tooltip);
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private void Render()
    {
        body.Children.Clear();
        var (preamble, hunks) = Parse(text);
        if (whitespace.IsChecked == true) foreach (var hunk in hunks) IgnoreWhitespace(hunk);
        if (hunks.Count == 0 && string.IsNullOrWhiteSpace(text))
        {
            var empty = Ui.Text("No changes", Ui.Hint, 12);
            empty.Name = "DiffEmpty"; empty.Margin = new Thickness(20); empty.HorizontalAlignment = HorizontalAlignment.Left;
            Ui.Place(body, empty); return;
        }
        if (hunks.Count == 0 || inline.IsChecked == true) RenderInline(preamble, hunks);
        else RenderSplit(hunks);
    }

    private void RenderInline(List<string> preamble, List<Hunk> hunks)
    {
        var block = CodeBlock("DiffInlineText");
        foreach (var line in preamble) block.Inlines!.Add(new Run(line + "\n") { Foreground = Ui.Muted });
        var previous = (Hunk?)null;
        foreach (var hunk in hunks)
        {
            AddGap(block, Gap(previous, hunk));
            block.Inlines!.Add(new Run(hunk.Header + "\n") { Foreground = Ui.Accent });
            foreach (var line in hunk.Lines) block.Inlines.Add(new Run(line + "\n") { Foreground = Tint(line) });
            previous = hunk;
        }
        Ui.Place(body, new ScrollViewer { Content = block, Margin = new Thickness(20, 8), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private void RenderSplit(List<Hunk> hunks)
    {
        var oldSide = CodeBlock("DiffOldText");
        var newSide = CodeBlock("DiffNewText");
        var previous = (Hunk?)null;
        foreach (var hunk in hunks)
        {
            var gap = Gap(previous, hunk);
            AddGap(oldSide, gap); AddGap(newSide, gap);
            var removed = new List<string>(); var added = new List<string>();
            void Flush()
            {
                for (var index = 0; index < Math.Max(removed.Count, added.Count); index++)
                {
                    oldSide.Inlines!.Add(new Run((index < removed.Count ? removed[index] : "") + "\n") { Foreground = Ui.Danger });
                    newSide.Inlines!.Add(new Run((index < added.Count ? added[index] : "") + "\n") { Foreground = Ui.Success });
                }
                removed.Clear(); added.Clear();
            }
            foreach (var line in hunk.Lines)
            {
                if (line.StartsWith('-')) removed.Add(line[1..]);
                else if (line.StartsWith('+')) added.Add(line[1..]);
                else if (line.StartsWith('\\')) continue;
                else
                {
                    Flush();
                    var content = line.Length > 0 ? line[1..] : "";
                    oldSide.Inlines!.Add(new Run(content + "\n") { Foreground = Ui.Muted });
                    newSide.Inlines!.Add(new Run(content + "\n") { Foreground = Ui.Muted });
                }
            }
            Flush();
            previous = hunk;
        }
        var divider = new Border { Width = 1, Background = Ui.BorderBrush };
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8) };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
        Ui.Place(columns, new ScrollViewer { Content = oldSide, Margin = new Thickness(12, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Ui.Place(columns, divider, 0, 1);
        Ui.Place(columns, new ScrollViewer { Content = newSide, Margin = new Thickness(12, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, 0, 2);
        scroll.Content = columns;
        Ui.Place(body, scroll);
    }

    private static SelectableTextBlock CodeBlock(string name) => new()
    {
        Name = name,
        FontFamily = Ui.CodeFont,
        FontSize = 13,
        Foreground = Ui.TextBrush,
        TextWrapping = TextWrapping.NoWrap,
        LineHeight = 21
    };

    private static IBrush Tint(string line) => line.StartsWith('+') ? Ui.Success : line.StartsWith('-') ? Ui.Danger : Ui.Muted;

    private static void AddGap(SelectableTextBlock block, int hidden)
    {
        if (hidden > 0) block.Inlines!.Add(new Run($"⋯ {hidden} hidden lines\n") { Foreground = Ui.Hint });
    }

    private static int Gap(Hunk? previous, Hunk next) =>
        previous is null ? Math.Max(0, next.OldCount == 0 ? next.OldStart : next.OldStart - 1)
            : next.OldStart - (previous.OldStart + previous.OldCount);

    private static (List<string> Preamble, List<Hunk> Hunks) Parse(string diff)
    {
        var preamble = new List<string>();
        var hunks = new List<Hunk>();
        var lines = diff.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        Hunk? current = null;
        for (var index = 0; index < count; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var match = HunkHeader().Match(line);
            if (match.Success)
            {
                current = new(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1, line, []);
                hunks.Add(current);
            }
            else if (current is null) preamble.Add(line);
            else current.Lines.Add(line);
        }
        return (preamble, hunks);
    }

    private static void IgnoreWhitespace(Hunk hunk)
    {
        var lines = hunk.Lines;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith('-')) continue;
            var removedEnd = index;
            while (removedEnd < lines.Count && lines[removedEnd].StartsWith('-')) removedEnd++;
            var addedEnd = removedEnd;
            while (addedEnd < lines.Count && lines[addedEnd].StartsWith('+')) addedEnd++;
            var removed = removedEnd - index;
            if (addedEnd - removedEnd == removed &&
                Enumerable.Range(0, removed).All(offset => Strip(lines[index + offset]) == Strip(lines[removedEnd + offset])))
            {
                var context = Enumerable.Range(0, removed).Select(offset => " " + lines[removedEnd + offset][1..]).ToArray();
                lines.RemoveRange(index, addedEnd - index);
                lines.InsertRange(index, context);
                index += removed - 1;
            }
            else index = addedEnd - 1;
        }
    }

    private static string Strip(string line) => WhitespaceRun().Replace(line[1..], "");

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+\d+(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
