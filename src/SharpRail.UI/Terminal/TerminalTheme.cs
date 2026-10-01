using Ghostty.Avalonia;
using SharpRail.UI.Rendering;
using SkiaSharp;
using Avalonia.Platform;

namespace SharpRail.UI.Terminal;

/// <summary>The workbench's terminal colours and font.</summary>
internal static class TerminalTheme
{
    /// <summary>Mirrors the reference's xterm theme: selection composited over the surface, and its contrast floor.</summary>
    internal static TerminalColors Colors()
    {
        var theme = Ui.Theme;
        var background = Ui.Surface.Color;
        return new TerminalColors(background, Ui.TextBrush.Color, [.. theme.Ansi])
        {
            Cursor = Ui.Accent.Color,
            SelectionBackground = Ui.Over(theme["editorSelection"], background),
            SelectionForeground = theme.Colors["editorSelectionForeground"],
            MinimumContrast = theme.IsHighContrast ? 7 : 4.5
        };
    }

    // JetBrains Mono, Ghostty's own default, from the app's bundled fonts; shared for the app's lifetime.
    internal static readonly Lazy<SKTypeface> Typeface = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/Fonts/JetBrainsMono-Regular.ttf"));
        return SKTypeface.FromStream(stream);
    });
}
