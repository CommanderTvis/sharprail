namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>
/// What a rendered Markdown document reaches outside the kit for: workspace images, the spec catalog behind
/// <c>[[id]]</c> links, navigation, and the reading measure.
/// </summary>
/// <param name="ReadImage">Reads a workspace-relative image; null when the file is not an image.</param>
/// <param name="ResolveSpecLinks">Maps spec ids to workspace-relative paths; an id it omits renders disabled.</param>
/// <param name="Navigate">Opens a workspace-relative path at an optional anchor.</param>
/// <param name="FontSize">The body font size.</param>
/// <param name="LineWidth">The reading measure in symbols.</param>
/// <param name="LineWidthBounded">Whether the column is capped at <paramref name="LineWidth"/>.</param>
public sealed record MarkdownContext(
    Func<string, CancellationToken, ValueTask<byte[]?>> ReadImage,
    Func<CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>> ResolveSpecLinks,
    Action<string, string?> Navigate,
    double FontSize,
    int LineWidth,
    bool LineWidthBounded)
{
    /// <summary>The source editor's wrapping width in logical pixels.</summary>
    public double SourceWrapWidth { get; init; } = LineWidths.File(LineWidths.FileDefault, true);
}
