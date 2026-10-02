namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>
/// What a rendered Markdown document reaches outside the kit for: workspace images, the spec a
/// <c>[[id]]</c> link names, navigation, and the reading measure.
/// </summary>
/// <param name="ReadImage">Reads a workspace-relative image; null when the file is not an image.</param>
/// <param name="ResolveSpecLink">Maps a spec id to its workspace-relative path; an id it answers null for renders disabled.</param>
/// <param name="Navigate">Opens a workspace-relative path at an optional anchor.</param>
/// <param name="FontSize">The body font size.</param>
/// <param name="LineWidth">The reading measure in symbols.</param>
/// <param name="LineWidthBounded">Whether the column is capped at <paramref name="LineWidth"/>.</param>
public sealed record MarkdownContext(
    Func<string, CancellationToken, ValueTask<byte[]?>> ReadImage,
    Func<string, string?> ResolveSpecLink,
    Action<string, string?> Navigate,
    double FontSize,
    int LineWidth,
    bool LineWidthBounded)
{
    /// <summary>The source editor's wrapping width in logical pixels.</summary>
    public double SourceWrapWidth { get; init; } = LineWidths.File(LineWidths.FileDefault, true);
}