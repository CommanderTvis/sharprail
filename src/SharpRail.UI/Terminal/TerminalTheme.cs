using Avalonia.Platform;

using Ghostty.Avalonia;

using SharpRail.UI.Rendering;

using SkiaSharp;

namespace SharpRail.UI.Terminal;

/// <summary>The workbench's terminal colours and font.</summary>
internal static class TerminalTheme
{
    /// <summary>Uses the workbench palette, preserving terminal colours outside high-contrast themes.</summary>
    internal static TerminalColors Colors()
    {
        var theme = Ui.Theme;
        var background = Ui.Surface.Color;
        return new TerminalColors(background, Ui.TextBrush.Color, [.. theme.Ansi])
        {
            Cursor = Ui.Accent.Color,
            SelectionBackground = Ui.Over(theme["editorSelection"], background),
            SelectionForeground = theme.Colors["editorSelectionForeground"],
            MinimumContrast = theme.IsHighContrast ? 7 : 1
        };
    }

    // JetBrains Mono, Ghostty's own default, from the app's bundled fonts; shared for the app's lifetime.
    internal static readonly Lazy<SKTypeface> Typeface = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/Fonts/JetBrainsMono-Regular.ttf"));
        return SKTypeface.FromStream(stream);
    });
}