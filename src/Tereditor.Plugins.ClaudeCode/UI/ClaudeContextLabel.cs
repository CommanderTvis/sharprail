using Avalonia;
using Avalonia.Controls;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// The Context surface's header label: "CONTEXT" where the row offers room for it, "CTX" where it does not. Both
/// spellings are measured and only the one that fits the offered width is drawn, so the choice follows the layout
/// (the dock gives the label what the token summary leaves) without changing any property during layout.
/// </summary>
internal sealed class ClaudeContextLabel : Panel
{
    private readonly TextBlock full = Part("CONTEXT");
    private readonly TextBlock narrow = Part("CTX");
    private bool isNarrow;

    /// <summary>Creates the label.</summary>
    public ClaudeContextLabel()
    {
        Children.Add(full);
        Children.Add(narrow);
        ToolTip.SetTip(this, "Context");
        Avalonia.Automation.AutomationProperties.SetName(this, "Context");
    }

    /// <summary>The text currently drawn.</summary>
    public string Text => (isNarrow ? narrow : full).Text ?? "";

    private static TextBlock Part(string text)
    {
        var block = Ui.Text(text, Ui.Muted, 11);
        block.ClipToBounds = true;
        return block;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        full.Measure(Size.Infinity);
        narrow.Measure(Size.Infinity);
        isNarrow = availableSize.Width < full.DesiredSize.Width;
        return (isNarrow ? narrow : full).DesiredSize;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var (shown, hidden) = isNarrow ? (narrow, full) : (full, narrow);
        shown.Arrange(new Rect(finalSize));
        hidden.Arrange(default);
        return finalSize;
    }
}