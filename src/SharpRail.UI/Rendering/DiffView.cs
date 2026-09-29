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

internal sealed partial class DiffView : Grid, IDisposable
{
    private sealed record Hunk(int OldStart, int OldCount, string Header, List<string> Lines);

    private readonly Grid body = new();
    private readonly ToggleButton split = Toggle("DiffSplit", "layout", "Side-by-side diff");
    private readonly ToggleButton inline = Toggle("DiffInline", "list", "Inline diff");
    private readonly ToggleButton whitespace = Toggle("DiffWhitespace", "collapseVertical", "Hide whitespace changes");
    private readonly ToggleButton source = Segment("DiffSource", "Source");
    private readonly ToggleButton rendered = Segment("DiffRendered", "Rendered");
    private readonly double wrapWidth;
    private readonly Func<CancellationToken, Task<Control>>? renderMerged;
    private readonly Action<bool>? renderedChanged;
    private CancellationTokenSource? merge;
    private Control? merged;
    private bool disposed;
    private string text;

    /// <summary>
    /// A unified diff viewer. Markdown diffs pass <paramref name="renderMerged"/> to offer the reference's
    /// Source|Rendered toggle; their source view is always side by side.
    /// </summary>
    internal DiffView(string text, string path, double wrapWidth,
        Func<CancellationToken, Task<Control>>? renderMerged = null, bool showRendered = false, Action<bool>? renderedChanged = null)
    {
        this.text = text;
        this.wrapWidth = wrapWidth;
        this.renderMerged = renderMerged;
        this.renderedChanged = renderedChanged;
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
        controls.Children.Add(whitespace); controls.Children.Add(copy);
        if (renderMerged is null) { controls.Children.Insert(0, inline); controls.Children.Insert(0, split); }
        else { controls.Children.Add(source); controls.Children.Add(rendered); }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0, 0, 0) };
        Ui.Place(header, chip); Ui.Place(header, controls, 0, 1);
        Ui.Place(this, header);
        Ui.Place(this, body, 1);
        split.IsChecked = true; inline.IsChecked = false;
        split.Click += (_, _) => { split.IsChecked = true; inline.IsChecked = false; Render(); };
        inline.Click += (_, _) => { inline.IsChecked = true; split.IsChecked = false; Render(); };
        whitespace.Click += (_, _) => Render();
        IsRendered = showRendered && renderMerged is not null;
        source.IsChecked = !IsRendered; rendered.IsChecked = IsRendered;
        source.Click += (_, _) => ShowRendered(false);
        rendered.Click += (_, _) => ShowRendered(true);
        Render();
    }

    internal bool IsRendered { get; private set; }

    internal void Update(string diff)
    {
        if (diff == text) return;
        text = diff;
        if (IsRendered) StartMerge(keepCurrent: true);
        else Render();
    }

    private void ShowRendered(bool show)
    {
        source.IsChecked = !show; rendered.IsChecked = show;
        if (show == IsRendered) return;
        IsRendered = show;
        renderedChanged?.Invoke(show);
        Render();
    }

    private static ToggleButton Segment(string name, string label)
    {
        var button = new ToggleButton
        {
            Name = name,
            Content = label,
            Height = 20,
            Padding = new Thickness(8, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Foreground = Ui.Muted
        };
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "PointerOver", "Pressed" })
        {
            button.Resources["ToggleButtonBackground" + state] = Ui.Hover;
            button.Resources["ToggleButtonForeground" + state] = Ui.TextBrush;
        }
        AutomationProperties.SetName(button, label);
        return button;
    }

    // Merges run off the UI thread; a newer merge or leaving the rendered view cancels the stale one.
    private void StartMerge(bool keepCurrent)
    {
        merge?.Cancel(); merge?.Dispose();
        var cancellation = merge = new CancellationTokenSource();
        if (!keepCurrent || merged is null) ShowBody(Placeholder("RenderedDiffLoading", "Rendering diff…", Ui.Muted));
        _ = Complete();

        async Task Complete()
        {
            Control result;
            try { result = await renderMerged!(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (cancellation.IsCancellationRequested || disposed) return;
                Console.Error.WriteLine(error);
                result = Placeholder("RenderedDiffError", "Rendered diff failed — use the Source view.", Ui.Danger);
            }
            if (cancellation.IsCancellationRequested || disposed) { (result as IDisposable)?.Dispose(); return; }
            ShowBody(result);
            merged = result;
            if (ReferenceEquals(merge, cancellation)) { merge = null; cancellation.Dispose(); }
        }
    }

    private void ShowBody(Control content)
    {
        DropMerged();
        Ui.Place(body, content);
    }

    private void DropMerged()
    {
        body.Children.Clear();
        (merged as IDisposable)?.Dispose();
        merged = null;
    }

    private static Control Placeholder(string name, string message, IBrush color)
    {
        var text = Ui.Text(message, color, 12);
        text.Name = name;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        merge?.Cancel(); merge?.Dispose(); merge = null;
        (merged as IDisposable)?.Dispose(); merged = null;
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
        whitespace.IsVisible = !IsRendered;
        if (IsRendered) { StartMerge(keepCurrent: false); return; }
        merge?.Cancel(); merge?.Dispose(); merge = null;
        DropMerged();
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
        Ui.Place(body, new ScrollViewer { Content = block, Margin = new Thickness(20, 8), HorizontalScrollBarVisibility = Overflow });
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
        Ui.Place(columns, new ScrollViewer { Content = oldSide, Margin = new Thickness(12, 0), HorizontalScrollBarVisibility = Overflow, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Ui.Place(columns, divider, 0, 1);
        Ui.Place(columns, new ScrollViewer { Content = newSide, Margin = new Thickness(12, 0), HorizontalScrollBarVisibility = Overflow, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, 0, 2);
        scroll.Content = columns;
        Ui.Place(body, scroll);
    }

    private ScrollBarVisibility Overflow => double.IsFinite(wrapWidth) ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    private SelectableTextBlock CodeBlock(string name) => new()
    {
        Name = name,
        FontFamily = Ui.CodeFont,
        FontSize = 13,
        Foreground = Ui.TextBrush,
        TextWrapping = double.IsFinite(wrapWidth) ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MaxWidth = wrapWidth,
        HorizontalAlignment = HorizontalAlignment.Left,
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
