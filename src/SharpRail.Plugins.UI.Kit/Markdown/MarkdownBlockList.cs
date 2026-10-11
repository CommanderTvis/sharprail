using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace SharpRail.Plugins.UI.Kit.Markdown;

internal sealed class MarkdownBlockList : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);
    internal sealed record Block(Control View);
    private readonly AvaloniaList<Block> blocks;
    internal IEnumerable<Control> Blocks => blocks.Select(block => block.View);

    internal MarkdownBlockList(StackPanel source)
    {
        blocks = new(source.Children.Select(control => new Block(control)));
        source.Children.Clear();
        Margin = source.Margin;
        MaxWidth = source.MaxWidth;
        HorizontalAlignment = source.HorizontalAlignment;
        ItemsSource = blocks;
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        ItemTemplate = new FuncDataTemplate<Block>((block, _) => block?.View);
    }

    internal bool Replace(Control previous, Control replacement)
    {
        var index = -1;
        for (var candidate = 0; candidate < blocks.Count; candidate++)
            if (ReferenceEquals(blocks[candidate].View, previous)) { index = candidate; break; }
        if (index < 0) return false;
        blocks[index] = new(replacement);
        return true;
    }

    internal void Reveal(Control control)
    {
        var block = blocks.FirstOrDefault(block => ReferenceEquals(block.View, control) ||
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(control).Contains(block.View));
        if (block is null) return;
        ScrollIntoView(block);
        UpdateLayout();
    }
}