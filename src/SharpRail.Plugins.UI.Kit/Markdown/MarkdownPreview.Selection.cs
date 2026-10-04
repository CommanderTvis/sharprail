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
    // What the selection began with: a caret, or the word or paragraph a double or triple click chose. Dragging and
    // Shift+click extend from it to the pointer, in either direction and across blocks, as in a browser.
    private (int Block, int Start, int End)? origin;
    private bool dragging;

    private void WireSelection()
    {
        AddHandler(PointerPressedEvent, SelectionPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, SelectionChosen, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, SelectionMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => dragging = false, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, SelectionKey, RoutingStrategies.Tunnel);
    }

    /// <summary>The blocks in document order, refreshed per gesture because diagrams and images finish later.</summary>
    private SelectableTextBlock[] Selectable() =>
        selectable = [.. ((Control)Content!).GetLogicalDescendants().OfType<SelectableTextBlock>().Where(block => block.IsEffectivelyVisible)];

    private void SelectionPressed(object? sender, PointerPressedEventArgs e)
    {
        dragging = false;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var blocks = Selectable();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && origin is { } from && from.Block < blocks.Length)
        {
            // The block's own Shift+click knows only itself; the document extends from where the selection began.
            Extend(from, Locate(e));
            dragging = true; e.Handled = true;
            return;
        }
        var target = (e.Source as Visual)?.FindLogicalAncestorOfType<SelectableTextBlock>(includeSelf: true);
        foreach (var block in blocks) if (block != target) block.ClearSelection();
    }

    // After the block has applied its own click (a caret, a word, its whole text), that choice becomes the origin.
    private void SelectionChosen(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var target = (e.Source as Visual)?.FindLogicalAncestorOfType<SelectableTextBlock>(includeSelf: true);
        var index = Array.IndexOf(selectable, target);
        if (index < 0) { origin = null; return; }
        var (start, end) = target!.SelectionStart == target.SelectionEnd
            ? (Hit(target, e.GetPosition(target)), Hit(target, e.GetPosition(target)))
            : (Math.Min(target.SelectionStart, target.SelectionEnd), Math.Max(target.SelectionStart, target.SelectionEnd));
        origin = (index, start, end);
        dragging = true;
    }

    private void SelectionMoved(object? sender, PointerEventArgs e)
    {
        if (!dragging || origin is not { } from || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var to = Locate(e);
        // A plain drag inside its own block is the block's to draw.
        if (to.Block == from.Block && from.Start == from.End && selectable.Count(block => block.SelectionStart != block.SelectionEnd) <= 1) return;
        Extend(from, to);
    }

    private void Extend((int Block, int Start, int End) from, (int Block, int Index) to)
    {
        var before = to.Block < from.Block || (to.Block == from.Block && to.Index < from.Start);
        var (first, last) = before ? (to, (from.Block, from.End)) : ((from.Block, from.Start), to);
        for (var i = 0; i < selectable.Length; i++)
        {
            var block = selectable[i];
            if (i < first.Item1 || i > last.Item1) { block.ClearSelection(); continue; }
            block.SelectionStart = i == first.Item1 ? first.Item2 : 0;
            block.SelectionEnd = i == last.Item1 ? last.Item2 : Length(block);
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
        .Select(Words));

    // A document block counts its links and images as their words; any other block's selection is its own text.
    private static string Words(SelectableTextBlock block) => block is DocumentText document ? document.Selection : block.SelectedText;

    private void SelectionKey(object? sender, KeyEventArgs e)
    {
        if (!(e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))) return;
        if (e.Key == Key.A)
        {
            foreach (var block in Selectable()) block.SelectAll();
            e.Handled = true;
        }
        // Every copy goes through the document's own text, so links and images copy as their words.
        else if (e.Key == Key.C && selectable.Any(block => block.SelectionStart != block.SelectionEnd))
        {
            _ = CopyAsync(SelectedText);
            e.Handled = true;
        }
    }
}