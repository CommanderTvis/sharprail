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

    /// <summary>What a file tab shows for bytes that are neither text nor a picture the app draws.</summary>
    internal static Control ByteOnly(SharpRail.Host.Abstractions.ContentMetadata info)
    {
        var size = info.ByteLength is { } length ? string.Create(CultureInfo.InvariantCulture, $"{length:N0} bytes") : "";
        var detail = string.Join(", ", new[] { info.MediaType ?? "", size }.Where(part => part.Length > 0));
        var text = Ui.Text("Binary files cannot be previewed" + (detail.Length > 0 ? $" ({detail})." : "."), Ui.Muted, 12);
        text.Name = "BinaryNotice";
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

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