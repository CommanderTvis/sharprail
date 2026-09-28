using Avalonia;
using Avalonia.Controls;

namespace SharpRail.UI.Rendering;

internal sealed class MarkdownLink(TextBlock label) : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (label.TextLayout.TextLines.Count > 0)
            TextBlock.SetBaselineOffset(this, label.TextLayout.TextLines[0].Baseline + (size.Height - label.DesiredSize.Height) / 2);
        return size;
    }
}
