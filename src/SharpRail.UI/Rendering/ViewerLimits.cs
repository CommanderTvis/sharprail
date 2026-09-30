using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SharpRail.UI.Rendering;

/// <summary>Sizes, in characters, past which a viewer degrades instead of stalling the window.</summary>
internal static class ViewerLimits
{
    /// <summary>The rendered Markdown view merges whole documents and builds a control per block.</summary>
    internal const int RenderedMarkdown = 1024 * 1024;

    /// <summary>Scintilla lays out only visible lines, but loading, styling and wrapping still scale with the text.</summary>
    internal const int Scintilla = 32 * 1024 * 1024;

    internal static Control TooLarge(string what, int length)
    {
        var text = Ui.Text(string.Create(CultureInfo.InvariantCulture,
            $"This {what} is too large to display ({length / 1048576.0:0.#} M characters; the limit is {Scintilla / 1048576} M)."), Ui.Muted, 12);
        text.Name = "TooLargeNotice";
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }
}
