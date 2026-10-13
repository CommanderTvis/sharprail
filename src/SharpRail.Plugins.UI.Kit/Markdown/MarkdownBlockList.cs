using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;

namespace SharpRail.Plugins.UI.Kit.Markdown;

internal sealed class MarkdownBlockList : Panel
{
    private readonly List<Control> blocks;
    private readonly List<double> tops = [];
    private Rect viewport;
    private readonly Dictionary<Control, Size> sizes = [];
    private double measuredWidth = double.NaN;
    internal IEnumerable<Control> Blocks => blocks;

    internal MarkdownBlockList(StackPanel source)
    {
        blocks = source.Children.ToList();
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

    protected override Size MeasureOverride(Size availableSize)
    {
        if (measuredWidth != availableSize.Width)
        {
            sizes.Clear();
            measuredWidth = availableSize.Width;
        }
        tops.Clear();
        var height = 0d;
        var width = 0d;
        foreach (var block in blocks)
        {
            tops.Add(height);
            if (!sizes.TryGetValue(block, out var size) || Children.Contains(block))
            {
                var mounted = Children.Contains(block);
                if (!mounted) Children.Add(block);
                block.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                sizes[block] = size = block.DesiredSize;
                if (!mounted) Children.Remove(block);
                if (!mounted)
                    foreach (var text in block.GetLogicalDescendants().Prepend(block).OfType<DocumentText>()) text.ReleaseLayout();
            }
            height += size.Height;
            width = Math.Max(width, size.Width);
        }
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var visible = tops[index] + sizes[block].Height >= viewport.Top && tops[index] <= viewport.Bottom;
            if (visible && !Children.Contains(block)) Children.Add(block);
            else if (!visible && Children.Contains(block)) Children.Remove(block);
        }
        foreach (var block in Children) block.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var index = 0; index < blocks.Count; index++)
            if (Children.Contains(blocks[index]))
                blocks[index].Arrange(new Rect(0, tops[index], finalSize.Width, blocks[index].DesiredSize.Height));
        return finalSize;
    }

    internal void ContentChanged()
    {
        sizes.Clear();
        InvalidateMeasure();
    }

    internal bool Replace(Control previous, Control replacement)
    {
        var index = blocks.IndexOf(previous);
        if (index < 0) return false;
        Children.Remove(previous);
        blocks[index] = replacement;
        sizes.Remove(previous);
        InvalidateMeasure();
        return true;
    }

    internal void Reveal(Control control)
    {
        var index = blocks.FindIndex(block => ReferenceEquals(block, control) || control.GetLogicalAncestors().Contains(block));
        if (index < 0) return;
        if (this.GetLogicalAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } scroll && index < tops.Count)
            scroll.Offset = new Vector(scroll.Offset.X, tops[index] + Margin.Top);
        UpdateLayout();
    }
}