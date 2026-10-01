using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>
/// Document-wide selection: each paragraph, cell and code body is its own text block, so a drag that leaves
/// the block it started in extends through every block between it and the pointer, as in a browser.
/// </summary>
public sealed partial class MarkdownPreview
{
    private SelectableTextBlock[] selectable = [];
    private (int Block, int Index)? anchor;

    private void WireSelection()
    {
        AddHandler(PointerPressedEvent, SelectionPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, SelectionMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => anchor = null, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, SelectionKey, RoutingStrategies.Tunnel);
    }

    /// <summary>The blocks in document order, refreshed per gesture because diagrams and images finish later.</summary>
    private SelectableTextBlock[] Selectable() =>
        selectable = [.. ((Control)Content!).GetLogicalDescendants().OfType<SelectableTextBlock>().Where(block => block.IsEffectivelyVisible)];

    private void SelectionPressed(object? sender, PointerPressedEventArgs e)
    {
        anchor = null;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        var blocks = Selectable();
        var origin = (e.Source as Visual)?.FindLogicalAncestorOfType<SelectableTextBlock>(includeSelf: true);
        foreach (var block in blocks) if (block != origin) block.ClearSelection();
        var index = Array.IndexOf(blocks, origin);
        if (index >= 0 && e.ClickCount == 1) anchor = (index, Hit(origin!, e.GetPosition(origin)));
    }

    private void SelectionMoved(object? sender, PointerEventArgs e)
    {
        if (anchor is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var end = Locate(e);
        if (end.Block == start.Block && selectable.Count(block => block.SelectionStart != block.SelectionEnd) <= 1) return;
        var (first, last) = end.Block < start.Block || (end.Block == start.Block && end.Index < start.Index) ? (end, start) : (start, end);
        for (var i = 0; i < selectable.Length; i++)
        {
            var block = selectable[i];
            if (i < first.Block || i > last.Block) { block.ClearSelection(); continue; }
            block.SelectionStart = i == first.Block ? first.Index : 0;
            block.SelectionEnd = i == last.Block ? last.Index : Length(block);
        }
    }

    /// <summary>The block under the pointer, or the last block that starts above it, with the caret position there.</summary>
    private (int Block, int Index) Locate(PointerEventArgs e)
    {
        var candidate = 0;
        for (var i = 0; i < selectable.Length; i++)
        {
            var block = selectable[i];
            var position = e.GetPosition(block);
            if (position.Y < 0) break;
            candidate = i;
            if (position.Y < block.Bounds.Height && position.X >= 0 && position.X < block.Bounds.Width) break;
        }
        var target = selectable[candidate];
        var local = e.GetPosition(target);
        return (candidate, local.Y >= target.Bounds.Height ? Length(target) : Hit(target, local));
    }

    private static int Hit(SelectableTextBlock block, Point point) =>
        Math.Clamp(block.TextLayout.HitTestPoint(new Point(Math.Max(0, point.X), Math.Max(0, point.Y))).TextPosition, 0, Length(block));

    private static int Length(SelectableTextBlock block) => block.Text?.Length ?? block.Inlines?.Text?.Length ?? 0;

    public string SelectedText => string.Join("\n\n", selectable.Where(block => block.SelectionStart != block.SelectionEnd)
        .Select(block => block.SelectedText));

    private void SelectionKey(object? sender, KeyEventArgs e)
    {
        if (!(e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))) return;
        if (e.Key == Key.A)
        {
            foreach (var block in Selectable()) block.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.C && selectable.Count(block => block.SelectionStart != block.SelectionEnd) > 1)
        {
            _ = CopyAsync(SelectedText);
            e.Handled = true;
        }
    }
}