using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>
/// Top-level Markdown blocks laid out from estimated heights: only blocks near the viewport are mounted and measured,
/// and a block's measured height replaces its estimate once it has been realized. Realized blocks are scroll-anchor
/// candidates, so a corrected estimate above the reader moves the extent, not the text being read.
/// </summary>
internal sealed class MarkdownBlockList : Panel
{
    /// <summary>A height a block's builder already knows roughly, such as a chunk's line count times its line height.</summary>
    internal static readonly AttachedProperty<double> EstimateProperty =
        AvaloniaProperty.RegisterAttached<MarkdownBlockList, Control, double>("Estimate", double.NaN);

    private const double FallbackHeight = 40;
    // Realized blocks change the tops of those after them; a few passes settle the viewport without looping forever.
    private const int RealizePasses = 4;

    private readonly List<Control> blocks;
    private readonly List<double> tops = [];
    private readonly List<double> heights = [];
    private readonly Dictionary<Control, double> estimates = [];
    private readonly HashSet<Control> visible = [];
    private readonly Dictionary<Control, int> positions = [];
    private Rect viewport;
    private readonly Dictionary<Control, Size> sizes = [];
    private readonly Dictionary<Control, List<(DocumentText Text, int Characters)>> texts = [];
    private readonly HashSet<Control> stale = [];
    private double measuredWidth = double.NaN;
    private ScrollViewer? anchors;
    internal IEnumerable<Control> Blocks => blocks;

    // The uncollapsed bottom margin of the last block, which Append gives back once a block follows it.
    private double bottom;

    internal MarkdownBlockList(StackPanel source, double lastBottom = 0)
    {
        blocks = source.Children.ToList();
        bottom = lastBottom;
        source.Children.Clear();
        Margin = source.Margin;
        MaxWidth = source.MaxWidth;
        HorizontalAlignment = source.HorizontalAlignment;
        EffectiveViewportChanged += (_, e) =>
        {
            if (viewport == e.EffectiveViewport) return;
            viewport = e.EffectiveViewport;
            InvalidateMeasure();
        };
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (anchors is not null)
            foreach (var child in Children) anchors.UnregisterAnchorCandidate(child);
        anchors = null;
        base.OnDetachedFromLogicalTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (measuredWidth != availableSize.Width)
        {
            // Heights at the old width stay as estimates until each block is realized again.
            stale.UnionWith(sizes.Keys);
            estimates.Clear();
            measuredWidth = availableSize.Width;
        }
        anchors ??= this.GetLogicalAncestors().OfType<ScrollViewer>().FirstOrDefault();
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        var height = Layout(availableSize.Width);
        for (var pass = 0; pass < RealizePasses; pass++)
        {
            var changed = false;
            // Only the blocks the viewport overlaps are visited, so a pass costs the viewport, not the document.
            visible.Clear();
            var first = tops.BinarySearch(viewport.Top);
            first = Math.Max(0, first >= 0 ? first : ~first - 1);
            for (var index = first; index < blocks.Count && tops[index] <= viewport.Bottom; index++)
                if (tops[index] + heights[index] >= viewport.Top) visible.Add(blocks[index]);
            for (var index = Children.Count - 1; index >= 0; index--)
            {
                if (visible.Contains(Children[index])) continue;
                anchors?.UnregisterAnchorCandidate(Children[index]);
                Children.RemoveAt(index);
            }
            for (var index = first; index < blocks.Count && tops[index] <= viewport.Bottom; index++)
            {
                var block = blocks[index];
                if (!visible.Contains(block)) continue;
                if (!Children.Contains(block))
                {
                    Children.Add(block);
                    anchors?.RegisterAnchorCandidate(block);
                }
                block.Measure(constraint);
                if (!sizes.TryGetValue(block, out var size) || stale.Contains(block) || Math.Abs(size.Height - block.DesiredSize.Height) > 0.5)
                    changed = true;
                sizes[block] = block.DesiredSize;
                stale.Remove(block);
            }
            if (!changed) break;
            height = Layout(availableSize.Width);
        }
        var width = 0d;
        foreach (var size in sizes.Values) width = Math.Max(width, size.Width);
        return new Size(width, height);
    }

    private double Layout(double width)
    {
        tops.Clear();
        heights.Clear();
        positions.Clear();
        var height = 0d;
        foreach (var block in blocks)
        {
            positions[block] = tops.Count;
            tops.Add(height);
            var blockHeight = BlockHeight(block, width);
            heights.Add(blockHeight);
            height += blockHeight;
        }
        return height;
    }

    private double BlockHeight(Control block, double width)
    {
        if (sizes.TryGetValue(block, out var size)) return size.Height;
        if (estimates.TryGetValue(block, out var known)) return known;
        return estimates[block] = Estimate(block, width);
    }

    // An unrealized block's height at this width, kept until the width changes so a layout pass reads it back.
    private double Estimate(Control block, double width)
    {
        var margin = block.Margin.Top + block.Margin.Bottom;
        if (block.GetValue(EstimateProperty) is var hint && !double.IsNaN(hint)) return hint + margin;
        if (!texts.TryGetValue(block, out var parts))
            texts[block] = parts = [.. block.GetLogicalDescendants().Prepend(block).OfType<DocumentText>()
                .Select(text => (text, text.Inlines?.Text?.Length ?? text.Text?.Length ?? 0))];
        if (parts.Count == 0) return FallbackHeight + margin;
        var estimate = margin;
        foreach (var (text, characters) in parts)
        {
            var line = double.IsNaN(text.LineHeight) ? text.FontSize * 1.4 : text.LineHeight;
            // An average glyph is about half the font size wide; every text has at least one line.
            var perLine = double.IsInfinity(width) ? double.MaxValue : Math.Max(1, width / (text.FontSize * 0.5));
            estimate += Math.Max(1, Math.Ceiling(characters / perLine)) * line + text.Margin.Top + text.Margin.Bottom;
        }
        return estimate;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            if (positions.TryGetValue(child, out var index))
                child.Arrange(new Rect(0, tops[index], finalSize.Width, child.DesiredSize.Height));
        return finalSize;
    }

    internal void ContentChanged()
    {
        // Images and diagrams change heights; mounted blocks are measured again, the rest keep their estimates.
        stale.UnionWith(sizes.Keys);
        InvalidateMeasure();
    }

    /// <summary>
    /// Adds blocks below the last one, collapsing the margin between them the way the source panel's were, for a
    /// preview that renders its document in slices.
    /// </summary>
    internal void Append(IReadOnlyList<Control> added)
    {
        if (added.Count == 0) return;
        var previousBottom = bottom;
        if (blocks.Count > 0)
        {
            var last = blocks[^1];
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, bottom);
            stale.Add(last);
            estimates.Remove(last);
        }
        for (var index = 0; index < added.Count; index++)
        {
            var margin = added[index].Margin;
            var first = blocks.Count == 0 && index == 0;
            added[index].Margin = new(margin.Left, first ? 0 : Math.Max(0, margin.Top - previousBottom), margin.Right,
                index == added.Count - 1 ? 0 : margin.Bottom);
            previousBottom = margin.Bottom;
        }
        bottom = previousBottom;
        blocks.AddRange(added);
        InvalidateMeasure();
    }

    internal bool Replace(Control previous, IReadOnlyList<Control> replacements)
    {
        var index = blocks.IndexOf(previous);
        if (index < 0) return false;
        anchors?.UnregisterAnchorCandidate(previous);
        Children.Remove(previous);
        blocks.RemoveAt(index);
        blocks.InsertRange(index, replacements);
        sizes.Remove(previous);
        estimates.Remove(previous);
        texts.Remove(previous);
        stale.Remove(previous);
        InvalidateMeasure();
        return true;
    }

    internal void Reveal(Control control)
    {
        var index = blocks.FindIndex(block => ReferenceEquals(block, control) || control.GetLogicalAncestors().Contains(block));
        if (index < 0 || this.GetLogicalAncestors().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
        // Realizing the block measures it and the blocks around it, which can move it from its estimated top.
        for (var attempt = 0; attempt < RealizePasses && index < tops.Count; attempt++)
        {
            var top = tops[index] + Margin.Top;
            if (attempt > 0 && Math.Abs(scroll.Offset.Y - top) <= 0.5) break;
            scroll.Offset = new Vector(scroll.Offset.X, top);
            UpdateLayout();
        }
    }
}