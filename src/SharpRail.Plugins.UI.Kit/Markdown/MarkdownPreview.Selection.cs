using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>
/// Document-wide selection: each paragraph, cell and code body is its own text block, so a drag that leaves
/// the block it started in extends through every block between it and the pointer, as in a browser.
/// </summary>
public sealed partial class MarkdownPreview
{
    private SelectableTextBlock[] selectable = [];
    private SelectableTextBlock[]? selectionCatalog;
    private readonly Dictionary<SelectableTextBlock, int> selectableIndexes = [];
    private readonly HashSet<SelectableTextBlock> observedSelection = [];
    private readonly HashSet<SelectableTextBlock> selectedBlocks = [];
    private (int First, int Last)? extendedRange;
    // What the selection began with: a caret, or the word or paragraph a double or triple click chose. Dragging and
    // Shift+click extend from it to the pointer, in either direction and across blocks, as in a browser.
    private (int Block, int Start, int End)? origin;
    private bool dragging;

    // The source lines each rendered block came from, so a selection in the preview can be named in the file.
    private static readonly AttachedProperty<(int Start, int End)?> SourceLinesProperty =
        AvaloniaProperty.RegisterAttached<MarkdownPreview, Control, (int Start, int End)?>("SourceLines");

    private readonly List<(Control Control, int Start, int End)> sourceBlocks = [];

    private Control Sourced(Control control, Markdig.Syntax.Block block, bool selection = true, Markdig.Syntax.Block? end = null)
    {
        var (start, last) = (block.Line + 1, SourceRange(end ?? block).End);
        return SourcedLines(control, start, last, selection);
    }

    /// <summary>The one-based first and last source lines of a block.</summary>
    private (int Start, int End) SourceRange(Markdig.Syntax.Block block)
    {
        var last = block.Line;
        if (Document.LineStartIndexes is { } starts)
        {
            var found = starts.BinarySearch(block.Span.End);
            last = Math.Max(last, found >= 0 ? found : ~found - 1);
        }
        return (block.Line + 1, last + 1);
    }

    private Control SourcedLines(Control control, int start, int end, bool selection = true)
    {
        if (selection) control.SetValue(SourceLinesProperty, (start, end));
        sourceBlocks.Add((control, start, end));
        return control;
    }

    internal void RevealSourceLine(int line)
    {
        Control? target = null;
        var nearest = int.MaxValue;
        var smallest = int.MaxValue;
        foreach (var (block, start, end) in sourceBlocks)
        {
            if (!block.GetSelfAndLogicalAncestors().OfType<Control>().All(control => control.IsVisible)) continue;
            var distance = Math.Max(0, Math.Max(start - line, line - end));
            var size = end - start;
            if (distance > nearest || distance == nearest && size >= smallest) continue;
            target = block; nearest = distance; smallest = size;
        }
        if (target is null) return;
        if (target.TranslatePoint(default, this) is null) RevealBlock(target);
        if (target.TranslatePoint(default, this) is not { } point) return;
        var height = Math.Min(target.Bounds.Height, Viewport.Height);
        if (point.Y < 0) Offset = new Vector(Offset.X, Offset.Y + point.Y);
        else if (point.Y + height > Viewport.Height)
            Offset = new Vector(Offset.X, Offset.Y + point.Y + height - Viewport.Height);
    }

    /// <summary>The one-based source lines of the blocks the selection touches; null when nothing is selected.</summary>
    public (int Start, int End)? SelectedLines
    {
        get
        {
            (int Start, int End)? lines = null;
            foreach (var block in selectedBlocks.Where(Visible))
                if (block.GetSelfAndLogicalAncestors().OfType<Control>().Select(control => control.GetValue(SourceLinesProperty))
                    .FirstOrDefault(span => span is not null) is { } span)
                    lines = lines is { } known ? (Math.Min(known.Start, span.Start), Math.Max(known.End, span.End)) : span;
            return lines;
        }
    }

    /// <summary>Extra context-menu items, asked for as the menu opens; with none the text keeps its own menu.</summary>
    public Func<IReadOnlyList<Control>>? ContextActions { get; set; }

    private void OfferContextActions(object? sender, ContextRequestedEventArgs e)
    {
        // A menu left attached from an earlier open would otherwise answer this click with actions that are gone.
        if (ContextActions?.Invoke() is not { Count: > 0 } actions) { ContextMenu = null; return; }
        var text = SelectedText;
        var menu = ContextMenu ??= new ContextMenu { Name = "MarkdownPreviewActions" };
        menu.Items.Clear();
        menu.Items.Add(Ui.Menu("Copy", () => _ = CopyAsync(text), text.Length > 0));
        menu.Items.Add(new Separator());
        foreach (var action in actions) menu.Items.Add(action);
        menu.Open(this);
        e.Handled = true;
    }

    private void WireSelection()
    {
        AddHandler(ContextRequestedEvent, OfferContextActions, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, SelectionPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, SelectionChosen, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, SelectionMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => dragging = false, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, SelectionKey, RoutingStrategies.Tunnel);
    }

    private static bool Visible(SelectableTextBlock block) =>
        block.GetSelfAndLogicalAncestors().OfType<Control>().All(control => control.IsVisible);

    /// <summary>The blocks in document order, refreshed per gesture because diagrams and images finish later.</summary>
    private SelectableTextBlock[] Selectable()
    {
        var blocks = new List<SelectableTextBlock>();
        selectableIndexes.Clear();
        foreach (var block in selectionCatalog ??= TextBlocks().ToArray())
        {
            if (observedSelection.Add(block)) block.PropertyChanged += SelectionMoved;
            TrackSelection(block);
            if (!Visible(block)) continue;
            selectableIndexes[block] = blocks.Count;
            blocks.Add(block);
        }
        selectable = blocks.ToArray();
        return selectable;
    }

    private void TrackSelection(SelectableTextBlock block)
    {
        if (block.SelectionStart != block.SelectionEnd) selectedBlocks.Add(block);
        else selectedBlocks.Remove(block);
    }

    private void SelectionPressed(object? sender, PointerPressedEventArgs e)
    {
        dragging = false;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        extendedRange = null;
        var blocks = Selectable();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && origin is { } from && from.Block < blocks.Length)
        {
            // The block's own Shift+click knows only itself; the document extends from where the selection began.
            Extend(from, Locate(e));
            dragging = true; e.Handled = true;
            return;
        }
        var target = (e.Source as Visual)?.FindLogicalAncestorOfType<SelectableTextBlock>(includeSelf: true);
        foreach (var block in selectedBlocks.ToArray()) if (block != target) block.ClearSelection();
    }

    // After the block has applied its own click (a caret, a word, its whole text), that choice becomes the origin.
    private void SelectionChosen(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var target = (e.Source as Visual)?.FindLogicalAncestorOfType<SelectableTextBlock>(includeSelf: true);
        var index = target is not null ? selectableIndexes.GetValueOrDefault(target, -1) : -1;
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
        if (to.Block == from.Block && from.Start == from.End && selectedBlocks.Count <= 1) return;
        Extend(from, to);
    }

    private void Extend((int Block, int Start, int End) from, (int Block, int Index) to)
    {
        var before = to.Block < from.Block || (to.Block == from.Block && to.Index < from.Start);
        var (first, last) = before ? (to, (from.Block, from.End)) : ((from.Block, from.Start), to);
        if (extendedRange is { } previous)
        {
            for (var index = previous.First; index <= Math.Min(previous.Last, first.Item1 - 1); index++) selectable[index].ClearSelection();
            for (var index = Math.Max(previous.First, last.Item1 + 1); index <= previous.Last; index++) selectable[index].ClearSelection();
        }
        else
            foreach (var block in selectedBlocks.ToArray())
                if (!selectableIndexes.TryGetValue(block, out var index) || index < first.Item1 || index > last.Item1) block.ClearSelection();
        for (var index = first.Item1; index <= last.Item1; index++)
        {
            if (extendedRange is { } unchanged && index > unchanged.First && index < unchanged.Last && index > first.Item1 && index < last.Item1)
            {
                index = Math.Min(unchanged.Last, last.Item1) - 1;
                continue;
            }
            var block = selectable[index];
            block.SelectionStart = index == first.Item1 ? first.Item2 : 0;
            block.SelectionEnd = index == last.Item1 ? last.Item2 : Length(block);
        }
        extendedRange = (first.Item1, last.Item1);
    }

    /// <summary>The block under the pointer, or the last block that starts above it, with the caret position there.</summary>
    private (int Block, int Index) Locate(PointerEventArgs e)
    {
        var candidate = -1;
        foreach (var block in this.GetVisualDescendants().OfType<SelectableTextBlock>().Where(Visible)
            .Where(selectableIndexes.ContainsKey).OrderBy(block => selectableIndexes[block]))
        {
            var i = selectableIndexes[block];
            if (block.TranslatePoint(default, this) is null) continue;
            if (candidate < 0) candidate = i;
            var position = e.GetPosition(block);
            if (position.Y < 0) break;
            candidate = i;
            if (position.Y < block.Bounds.Height && position.X >= 0 && position.X < block.Bounds.Width) break;
        }
        if (candidate < 0) return (0, 0);
        var target = selectable[candidate];
        var local = e.GetPosition(target);
        return (candidate, local.Y >= target.Bounds.Height ? Length(target) : Hit(target, local));
    }

    private static int Hit(SelectableTextBlock block, Point point) =>
        Math.Clamp(block.TextLayout.HitTestPoint(new Point(Math.Max(0, point.X), Math.Max(0, point.Y))).TextPosition, 0, Length(block));

    private static int Length(SelectableTextBlock block) => block.Text?.Length ?? block.Inlines?.Text?.Length ?? 0;

    public string SelectedText
    {
        get
        {
            // Chunks of one code block continue each other's lines; every other pair of blocks is a paragraph apart.
            var text = new System.Text.StringBuilder();
            var previous = int.MinValue;
            foreach (var block in selectedBlocks.Where(Visible).OrderBy(block => selectableIndexes.GetValueOrDefault(block, int.MaxValue)))
            {
                var index = selectableIndexes.GetValueOrDefault(block, int.MaxValue);
                if (previous != int.MinValue) text.Append(Continues(block) && index == previous + 1 ? "\n" : "\n\n");
                text.Append(Words(block));
                previous = index;
            }
            return text.ToString();
        }
    }

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
        else if (e.Key == Key.C && selectedBlocks.Any(Visible))
        {
            _ = CopyAsync(SelectedText);
            e.Handled = true;
        }
    }
}