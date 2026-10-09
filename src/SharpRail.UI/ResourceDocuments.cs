using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Editor;
using SharpRail.UI.Rendering;
using SharpRail.UI.Resources;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    // The renderer each tab chose and that renderer's view state, by owning workspace and tab.
    private readonly Dictionary<string, TabView> tabViews = [];
    private ResourceRegistry? resources;

    internal ResourceRegistry Renderers => resources ??= BuildResources();

    private ResourceRegistry BuildResources()
    {
        var registry = new ResourceRegistry();
        BundledRenderers.Register(registry);
        registry.Register(new(ResourceRegistry.Code, "Source", new(Text: true), 100) { View = CodeBody, DiffsInPane = true });
        registry.Register(new(ResourceRegistry.Markdown, "Preview", new(Glob: ["*.md", "*.markdown"], Text: true), 110)
        {
            View = view => new MarkdownPreviewBody(view, text => new(text, view.Resource.Path, host, Preferences, (path, anchor) => _ = OpenDocumentAsync(path, false, anchor))),
            Diff = RenderMergedAsync
        });
        return registry;
    }

    private T? Body<T>(string key) where T : class => documentContent.GetValueOrDefault(key) switch
    {
        T direct => direct,
        ResourcePane pane => pane.Find<T>(),
        _ => null
    };

    // The rendered Markdown view builds a control per block, so a document past its limit keeps only the editor.
    private IReadOnlyList<ResourceRenderer> ViewCandidates(ResourceDescriptor resource, FileDocument document) =>
        [.. Renderers.Resolve(resource, ResourceIntent.View).Where(renderer =>
            renderer.Id != ResourceRegistry.Markdown || document.Text.Length <= ViewerLimits.RenderedMarkdown || !OperatingSystem.IsMacOS())];

    // A host that predates content metadata still sends a picture's bytes; describe them by the file name.
    private static ContentMetadata? Described(FileDocument document) => document.Info ??
        (document.ImageData is { } data ? new("", data.Length, false, ResourceRegistry.InferredMime(document.Path) ?? "image/png") : null);

    private ResourceContent FileContent(FileDocument document) => ResourceContent.Of(Described(document), document.Text,
        token => document.ImageData is { } data ? Task.FromResult(data) : ReadBytesAsync(document.Path, null, document.Info?.Sha256, token));

    // The hash is the identity: bytes that moved since the metadata was read are not what the view describes.
    private async Task<byte[]> ReadBytesAsync(string path, string? revision, string? hash, CancellationToken token)
    {
        var content = await Task.Run(async () => await host.ReadContentBytesAsync(path, revision, token), token);
        return string.IsNullOrEmpty(hash) || content.Info.Sha256 == hash ? content.Data : throw new IOException("The file changed since it was opened.");
    }

    private void WriteTabView(string key, TabView view)
    {
        if (LiveDocuments().Contains(key)) tabViews[key] = view;
    }

    private Control FileBody(FileDocument document, DockTab tab, string key)
    {
        if (document.Text.Length > ViewerLimits.Scintilla) return ViewerLimits.TooLarge("file", document.Text.Length);
        var resource = ResourceRegistry.Describe(workspaceRoot, tab.Path, Described(document));
        return new ResourcePane(ViewCandidates(resource, document), resource, tab.Id, FileContent(document),
            () => tabViews.GetValueOrDefault(key) ?? new(), view => WriteTabView(key, view));
    }

    private Control CodeBody(ResourceView view)
    {
        var key = view.Resource.Workspace + ":" + view.TabId;
        var document = documents[key];
        var hasPreview = view.TabId.StartsWith("markdown:", StringComparison.Ordinal) && document.Text.Length <= ViewerLimits.RenderedMarkdown;
        if (!OperatingSystem.IsMacOS())
            return new ScrollViewer
            {
                Name = hasPreview ? "MarkdownSource" : null,
                Content = MarkdownPreview.Code(document.Text),
                Margin = new Thickness(24),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
        return hasPreview ? new MarkdownSourceBody(view, FileWrapWidth) : CodeDocument(document, view.TabId, key);
    }

    /// <summary>Shows a reloaded file in its open body, keeping every view that can take the new content.</summary>
    private bool ReloadFileBody(string key, DockTab tab, FileDocument file)
    {
        if (documentContent.GetValueOrDefault(key) is not ResourcePane pane) return false;
        var resource = ResourceRegistry.Describe(workspaceRoot, tab.Path, Described(file));
        if (file.Text.Length > ViewerLimits.Scintilla || !ViewCandidates(resource, file).Select(candidate => candidate.Id).SequenceEqual(pane.Candidates)) return false;
        var editor = pane.Find<CodeDocumentView>();
        if (editor is not null && !editor.Reload(file.Text)) return true;
        documents[key] = file;
        pane.Reload(resource, FileContent(file), editor);
        return true;
    }

    private void ScrollToAnchor(string key, string anchor)
    {
        if (documentContent.GetValueOrDefault(key) is not ResourcePane pane || !pane.Candidates.Contains(ResourceRegistry.Markdown)) return;
        pane.Select(ResourceRegistry.Markdown);
        Dispatcher.UIThread.Post(() => pane.Find<MarkdownPreviewBody>()?.ScrollToAnchor(anchor), DispatcherPriority.Loaded);
    }
}