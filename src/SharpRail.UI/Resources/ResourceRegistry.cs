using System.Text.RegularExpressions;

using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Resources;

/// <summary>What a renderer is chosen by: the path and the host's content metadata, never the content itself.</summary>
internal sealed record ResourceDescriptor(string Workspace, string Path, string? Mime, bool IsText, long? ByteLength);

/// <summary>Text, bytes that can be fetched, and an absent diff side stay distinct.</summary>
internal abstract record ResourceContent
{
    /// <summary>The media type of this side alone; a diff's descriptor names only one.</summary>
    internal string? Mime { get; init; }

    internal sealed record Text(string Value, string Hash) : ResourceContent;
    internal sealed record Bytes(string Hash, long ByteLength, Func<CancellationToken, Task<byte[]>> Load) : ResourceContent;
    internal sealed record Absent : ResourceContent;

    internal static ResourceContent Of(ContentMetadata? info, string text, Func<CancellationToken, Task<byte[]>> load) =>
        info is null ? new Text(text, "")
        : info.Sha256 is null || info.ByteLength is null ? new Absent()
        : info.IsText ? new Text(text, info.Sha256) { Mime = info.MediaType }
        : new Bytes(info.Sha256, info.ByteLength.Value, load) { Mime = info.MediaType };
}

/// <summary>Only the renderer interprets <paramref name="ViewState"/>; it reports a new one through <paramref name="SaveViewState"/>.</summary>
internal sealed record ResourceView(ResourceDescriptor Resource, string TabId, ResourceContent Content, object? ViewState, Action<object?> SaveViewState);

internal sealed record ResourceDiff(ResourceDescriptor Resource, string TabId, ResourceContent Original, ResourceContent Modified, object? ViewState, Action<object?> SaveViewState);

/// <summary>Every stated condition must hold. A glob without a slash matches the file name; a MIME pattern may end in <c>/*</c>.</summary>
internal sealed record ResourceMatch(string[]? Glob = null, string[]? Mime = null, bool? Text = null);

internal sealed record ResourceRenderer(string Id, string Label, ResourceMatch Match, int Rank)
{
    internal Func<ResourceView, Control>? View { get; init; }
    /// <summary>Null means this content cannot be drawn by the renderer and another view should be used.</summary>
    internal Func<ResourceDiff, CancellationToken, Task<Control?>>? Diff { get; init; }
    /// <summary>The source diff is drawn by the diff pane itself, so the text fallback supports diffs without a factory.</summary>
    internal bool DiffsInPane { get; init; }

    internal bool Supports(ResourceIntent intent) => intent == ResourceIntent.View ? View is not null : Diff is not null || DiffsInPane;
}

internal enum ResourceIntent { View, Diff }

/// <summary>A view a pane can keep across a content change and ask for its state before it is detached.</summary>
internal interface IResourceBody
{
    object? ViewState { get; }
    /// <summary>False when the body cannot show the new content and must be rebuilt.</summary>
    bool Reload(ResourceContent content);
}

/// <summary>
/// Renderers by id. The ranked matches for a resource, ending in the required text or byte fallback, are both the
/// dispatch order and the document's view toggle. The registry holds no content and no tab state.
/// </summary>
internal sealed class ResourceRegistry
{
    internal const string Code = "sharprail/code";
    internal const string Binary = "sharprail/binary";
    internal const string Markdown = "sharprail/markdown";

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".json"] = "application/json",
        [".jsonc"] = "application/json",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf"
    };

    private readonly Dictionary<string, ResourceRenderer> renderers = [];

    internal void Register(ResourceRenderer renderer) => renderers[renderer.Id] = renderer;

    internal static string? InferredMime(string path) => MimeByExtension.GetValueOrDefault(Path.GetExtension(path));

    internal static ResourceDescriptor Describe(string workspace, string path, ContentMetadata? info) =>
        new(workspace, path.Replace('\\', '/'), info?.MediaType ?? InferredMime(path), info?.IsText ?? true, info?.ByteLength);

    internal IReadOnlyList<ResourceRenderer> Resolve(ResourceDescriptor resource, ResourceIntent intent)
    {
        var fallbackId = resource.IsText ? Code : Binary;
        if (!renderers.TryGetValue(fallbackId, out var fallback) || !fallback.Supports(intent))
            throw new InvalidOperationException($"Resource renderer fallback is unavailable: {fallbackId} ({intent}).");
        return [.. renderers.Values.Where(renderer => renderer.Id != fallbackId && renderer.Supports(intent) && Matches(renderer.Match, resource))
            .OrderByDescending(renderer => renderer.Rank).ThenBy(renderer => renderer.Id, StringComparer.Ordinal), fallback];
    }

    /// <summary>The tab's chosen renderer while it is still a candidate, otherwise the best match.</summary>
    internal static ResourceRenderer Select(IReadOnlyList<ResourceRenderer> candidates, string? selected) =>
        candidates.FirstOrDefault(candidate => candidate.Id == selected) ?? candidates[0];

    private static bool Matches(ResourceMatch match, ResourceDescriptor resource)
    {
        if (match.Text is { } text && text != resource.IsText) return false;
        if (match.Glob is { } globs && !globs.Any(glob => MatchesGlob(resource.Path, glob))) return false;
        if (match.Mime is { } mimes && !((resource.Mime ?? InferredMime(resource.Path)) is { } mime && mimes.Any(pattern => MatchesMime(mime, pattern)))) return false;
        return true;
    }

    private static bool MatchesMime(string mime, string pattern) => pattern.EndsWith("/*", StringComparison.Ordinal)
        ? mime.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : mime.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesGlob(string path, string glob)
    {
        var target = glob.Contains('/') ? path : path[(path.LastIndexOf('/') + 1)..];
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*\*", ".*").Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(target, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}