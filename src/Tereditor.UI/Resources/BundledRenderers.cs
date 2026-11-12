namespace SharpRail.UI.Resources;

/// <summary>The format renderers that need nothing from the window. Code and Markdown are registered by the workbench.</summary>
internal static class BundledRenderers
{
    internal static void Register(ResourceRegistry registry)
    {
        registry.Register(new(ResourceRegistry.Binary, "File", new(Text: false), 0)
        {
            View = ResourceCards.BinaryView,
            Diff = (diff, _) => Task.FromResult<Avalonia.Controls.Control?>(ResourceCards.BinaryDiff(diff))
        });
        registry.Register(new("sharprail/lfs", "LFS", new(Mime: ["application/vnd.git-lfs"], Text: true), 130)
        {
            View = view => ResourceCards.Centered(ResourceCards.Lfs("LfsPointer", null, view.Content)),
            Diff = (diff, _) => Task.FromResult<Avalonia.Controls.Control?>(ResourceCards.LfsDiff(diff))
        });
        registry.Register(new("sharprail/image", "Image", new(Mime: ["image/*"], Text: false), 120)
        {
            View = view => new ImageView(view, Pictures.RasterAsync),
            Diff = (diff, token) => ImageDiffView.CreateAsync(diff, Pictures.RasterAsync, token)
        });
        registry.Register(new("sharprail/svg", "Vector", new(Mime: ["image/svg+xml"], Text: true), 130)
        {
            View = view => new ImageView(view, VectorPictures.RasterAsync),
            Diff = (diff, token) => ImageDiffView.CreateAsync(diff, VectorPictures.RasterAsync, token)
        });
        registry.Register(new("sharprail/csv", "Table", new(Glob: ["*.csv", "*.tsv"], Text: true), 120) { View = TableViews.View, Diff = TableViews.DiffAsync });
        registry.Register(new("sharprail/json", "Tree", new(Glob: ["*.json", "*.jsonc"], Text: true), 120) { View = JsonViews.View, Diff = JsonViews.DiffAsync });
        registry.Register(new("sharprail/notebook", "Notebook", new(Glob: ["*.ipynb"], Text: true), 130) { View = NotebookView.View, Diff = NotebookView.DiffAsync });
    }
}