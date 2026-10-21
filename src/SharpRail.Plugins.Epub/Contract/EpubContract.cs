using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Epub;

/// <summary>Names one EPUB file of a workspace.</summary>
public sealed record EpubOpen(string WorkspaceId, string Path);

/// <summary>Names one resource, such as an image, inside an EPUB file.</summary>
public sealed record EpubResourceQuery(string WorkspaceId, string Path, string Href);

/// <summary>A resource's bytes; empty when the book has no such resource.</summary>
public sealed record EpubResource(byte[] Data);

/// <summary>One table-of-contents entry: its title, nesting depth and the one-based Markdown line it jumps to.</summary>
public sealed record EpubTocEntry(string Title, int Depth, int Line);

/// <summary>One reading-order document: its path inside the package and the one-based Markdown line it starts on.</summary>
public sealed record EpubChapter(string Href, int Line);

/// <summary>
/// A book converted for the kit's Markdown renderer. <see cref="Markdown"/> holds every reading-order document in
/// order; images name <c>res/&lt;package path&gt;</c>, links inside the book name <c>line/&lt;n&gt;</c>, and external
/// links keep their URL. <see cref="Error"/> is set instead when the file is not a readable EPUB.
/// </summary>
public sealed record EpubBook(string Title, string Authors, string Markdown, IReadOnlyList<EpubChapter> Chapters, IReadOnlyList<EpubTocEntry> Toc)
{
    public string? Error { get; init; }
}

public sealed record EpubSettings;

public static class EpubContract
{
    public const string Id = "epub-reader";
    public const string ResourcePrefix = "res/";
    public const string LinePrefix = "line/";
    public static readonly PluginMethod<EpubOpen, EpubBook> Open = new("open");
    public static readonly PluginMethod<EpubResourceQuery, EpubResource> Resource = new("resource");
    public static readonly PluginContract Contract = PluginContract.Create<EpubSettings>(Id, 1, [Open, Resource], []);
    public static readonly PluginManifest Manifest = new(Id, "EPUB Reader", "book-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Opens .epub books in a reader with a table of contents, scrolling and paginated modes, and search.",
        EnabledByDefault = false,
        Host = "SharpRail.Plugins.Epub.Host.dll",
        Ui = "SharpRail.Plugins.Epub.UI.dll",
        Contributes = new() { FileViewers = [new() { Extensions = ["epub"], Read = PluginFileRead.None }] }
    };
}