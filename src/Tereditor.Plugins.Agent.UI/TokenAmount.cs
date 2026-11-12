using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Agent.UI;

/// <summary>
/// A token count with its unit: "1 token" or "23.2k tokens" where the row has room, "1 tok" or "23.2k toks" where it is
/// narrower than <see cref="NarrowWidth"/>. Both spellings are measured and only the one that fits is drawn, so the
/// choice follows the width the layout offers without changing any property during layout.
/// </summary>
public sealed class TokenAmount : Panel
{
    /// <summary>Below this offered width the unit is abbreviated.</summary>
    public const double NarrowWidth = 320;

    private readonly TextBlock full;
    private readonly TextBlock narrow;

    /// <summary>Creates the amount.</summary>
    /// <param name="amount">The count as shown, such as <c>23.2k</c>.</param>
    /// <param name="tokens">The exact count, which picks the singular or plural unit.</param>
    /// <param name="foreground">The text colour.</param>
    /// <param name="suffix">Text after the unit, such as <c> in new session</c>.</param>
    public TokenAmount(string amount, long tokens, IBrush foreground, string suffix = "")
    {
        full = Part(amount + " " + Unit(tokens, false) + suffix, foreground);
        narrow = Part(amount + " " + Unit(tokens, true) + suffix, foreground);
        Children.Add(full);
        Children.Add(narrow);
    }

    /// <summary>Whether the last layout chose the abbreviated unit.</summary>
    public bool IsNarrow { get; private set; }

    /// <summary>The text currently drawn.</summary>
    public string Text => (IsNarrow ? narrow : full).Text ?? "";

    /// <summary>"token" or "tokens", or "tok" or "toks" when <paramref name="abbreviated"/>.</summary>
    public static string Unit(long tokens, bool abbreviated) => (abbreviated ? "tok" : "token") + (tokens == 1 ? "" : "s");

    private static TextBlock Part(string text, IBrush foreground)
    {
        var block = Ui.Text(text, foreground, 12);
        block.FontFamily = Ui.CodeFont;
        block.ClipToBounds = true;
        return block;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        full.Measure(Size.Infinity);
        narrow.Measure(Size.Infinity);
        IsNarrow = availableSize.Width < NarrowWidth;
        return (IsNarrow ? narrow : full).DesiredSize;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var (shown, hidden) = IsNarrow ? (narrow, full) : (full, narrow);
        shown.Arrange(new Rect(finalSize));
        hidden.Arrange(default);
        return finalSize;
    }
}