using System.Text;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// Find in a rendered document: Mod+F over a preview opens it, Enter / Shift+Enter step with wrap, a count shows
/// <c>i/n</c>, and Escape closes it. The current match is selected and scrolled into view; the whole visible
/// document is searched case-insensitively, and a match spanning two text blocks is not found.
/// </summary>
public sealed class FindBar : Border
{
    private readonly Func<Control?> root;
    private readonly TextBox input = new() { Name = "FindInput", Width = 200, FontSize = 12, MinHeight = 0, Padding = new Thickness(6, 2), PlaceholderText = "Find" };
    private readonly TextBlock count = Ui.Text("0/0", Ui.Muted, 12);
    private readonly List<(SelectableTextBlock Block, int Start)> matches = [];
    private int current = -1;

    public FindBar(Func<Control?> root)
    {
        this.root = root;
        Name = "FindBar";
        IsVisible = false;
        Background = Ui.Elevated;
        BorderBrush = Ui.BorderBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(4);
        Margin = new Thickness(8, 8, 20, 8);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        count.Name = "FindCount";
        count.MinWidth = 36; count.TextAlignment = TextAlignment.Center; count.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(input, "Find");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(input); row.Children.Add(count);
        Child = row;
        input.TextChanged += (_, _) => Search();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Step(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
    }

    public bool Missed => matches.Count == 0 && !string.IsNullOrEmpty(input.Text);

    /// <summary>Routes Mod+F from <paramref name="host"/> to this bar.</summary>
    public void Attach(Control host) => host.AddHandler(KeyDownEvent, (_, e) =>
    {
        if (e.Key != Key.F || !(e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))) return;
        e.Handled = true;
        Open();
    }, Avalonia.Interactivity.RoutingStrategies.Bubble);

    public void Open()
    {
        IsVisible = true;
        input.Focus();
        input.SelectAll();
        Search();
    }

    public void Close()
    {
        Select(-1);
        matches.Clear();
        IsVisible = false;
    }

    private void Search()
    {
        Select(-1);
        matches.Clear();
        var query = input.Text ?? "";
        if (query.Length > 0 && root() is { } scope)
            foreach (var block in scope.GetLogicalDescendants().OfType<SelectableTextBlock>().Where(block => block.IsEffectivelyVisible))
            {
                var text = TextOf(block);
                for (var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
                    matches.Add((block, at));
            }
        Select(matches.Count > 0 ? 0 : -1);
        input.BorderBrush = Missed ? Ui.Danger : null;
    }

    private void Step(int direction)
    {
        if (matches.Count == 0) return;
        Select(((current + direction) % matches.Count + matches.Count) % matches.Count);
    }

    private void Select(int index)
    {
        if (current >= 0 && current < matches.Count) matches[current].Block.ClearSelection();
        current = index;
        count.Text = matches.Count == 0 ? "0/0" : $"{current + 1}/{matches.Count}";
        if (index < 0) return;
        var (block, start) = matches[index];
        var length = (input.Text ?? "").Length;
        block.SelectionStart = start;
        block.SelectionEnd = start + length;
        var bounds = block.TextLayout.HitTestTextRange(start, length).FirstOrDefault();
        block.BringIntoView(bounds.Height > 0 ? bounds.Inflate(24) : new Rect(block.Bounds.Size));
    }

    // Positions follow the text source: a run is its text, an embedded control one placeholder character.
    private static string TextOf(SelectableTextBlock block)
    {
        if (block.Inlines is not { Count: > 0 } inlines) return block.Text ?? "";
        var text = new StringBuilder();
        foreach (var inline in inlines)
            text.Append(inline switch { Run run => run.Text, LineBreak => "\n", _ => "￼" });
        return text.ToString();
    }
}