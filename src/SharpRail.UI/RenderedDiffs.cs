using Avalonia.Controls;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;
using SharpRail.UI.Resources;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    /// <summary>Merges two Markdown sources for a rendered diff; runs on a thread-pool thread.</summary>
    public Func<string, string, CancellationToken, string> RenderedDiffMerge { get; set; } = MarkdownDiff.Merge;

    private Control DiffDocument(FileDocument document, DockTab tab, string key)
    {
        // Bytes, and text that may be a Git LFS pointer, are described by their sides' metadata, which only the host
        // knows; any other text diff is described by its path.
        var described = DiffView.IsBinaryDiff(document.Text) || document.Text.Contains("version https://git-lfs.github.com/spec/v1\n", StringComparison.Ordinal);
        var view = new DiffView(document.Text, tab.Path, LineWidths.File(Preferences.FileLineWidth, Preferences.FileLineWidthBounded),
            described ? null : DiffChoices(tab, key, new(workspaceRoot, tab.Path, ResourceRegistry.InferredMime(tab.Path), true, null), null),
            tabViews.GetValueOrDefault(key)?.RendererId, id => WriteTabView(key, new(id)), pending: described,
            revert: DiffRevert(tab, key), canRevert: state.Supports(HostProtocol.ChangeWritePath));
        if (described) _ = DescribeDiffAsync(view, tab, key);
        return view;
    }

    private IReadOnlyList<DiffChoice> DiffChoices(DockTab tab, string key, ResourceDescriptor resource, DiffSides? known) =>
        [.. Renderers.Resolve(resource, ResourceIntent.Diff).Select(renderer => renderer.Diff is not { } render
            ? new DiffChoice(renderer.Id, renderer.Label, null)
            : new DiffChoice(renderer.Id, renderer.Label, async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                var sides = known ?? await Task.Run(async () => await host.GetDiffSidesAsync(tab.Path, tab.Scope, tab.Comparison, linked.Token), linked.Token);
                var state = tabViews.GetValueOrDefault(key);
                var content = await render(new(resource, tab.Id,
                    DiffSide(tab.Path, sides.OriginalInfo, sides.Original, sides.OriginalRevision),
                    DiffSide(tab.Path, sides.ModifiedInfo, sides.Modified, sides.ModifiedRevision),
                    state is null || state.RendererId is null || state.RendererId == renderer.Id ? state?.ViewState : null,
                    value => WriteTabView(key, new(renderer.Id, value))), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                return content;
            }))];

    private ResourceContent DiffSide(string path, ContentMetadata? info, string text, string? revision) =>
        ResourceContent.Of(info, text, token => ReadBytesAsync(path, revision, info?.Sha256, token));

    private static string SidesIdentity(DiffSides sides) => sides.OriginalInfo?.Sha256 + ":" + sides.ModifiedInfo?.Sha256;

    private async Task DescribeDiffAsync(DiffView view, DockTab tab, string key)
    {
        try
        {
            var request = projectRequest;
            var sides = await Task.Run(async () => await host.GetDiffSidesAsync(tab.Path, tab.Scope, tab.Comparison, lifetime.Token), lifetime.Token);
            if (request != projectRequest || !ReferenceEquals(documentContent.GetValueOrDefault(key), view) || view.Identity == SidesIdentity(sides)) return;
            var original = sides.OriginalInfo ?? new(null, null, true, null);
            var modified = sides.ModifiedInfo ?? new(null, null, true, null);
            var named = modified.Sha256 is null ? original : modified;
            var resource = ResourceRegistry.Describe(workspaceRoot, tab.Path, named) with { IsText = original.IsText && modified.IsText };
            view.SetChoices(DiffChoices(tab, key, resource, sides), tabViews.GetValueOrDefault(key)?.RendererId, SidesIdentity(sides));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Grpc.Core.RpcException)
        {
            if (ReferenceEquals(documentContent.GetValueOrDefault(key), view)) view.ShowError("Could not load this file's bytes: " + error.Message);
        }
    }

    private async Task<Control?> RenderMergedAsync(ResourceDiff diff, CancellationToken token)
    {
        if (diff.Original is ResourceContent.Bytes || diff.Modified is ResourceContent.Bytes) return null;
        string original = (diff.Original as ResourceContent.Text)?.Value ?? "", modified = (diff.Modified as ResourceContent.Text)?.Value ?? "";
        if (original.Length + modified.Length > ViewerLimits.RenderedMarkdown) return null;
        var merge = RenderedDiffMerge;
        // The runs the reader expanded are the tab's view state for this renderer.
        var expanded = diff.ViewState as HashSet<string> ?? [];
        var (parsed, focus) = await Task.Run(() =>
        {
            var merged = merge(original, modified, token);
            var document = MarkdownPreview.Parse(merged);
            return (document, DiffFocus.Create(original, merged, document, expanded, () => diff.SaveViewState(expanded), token));
        }, token);
        token.ThrowIfCancellationRequested();
        return new MarkdownPreview(parsed, diff.Resource.Path, MarkdownContexts.For(host, Preferences, FollowLink, SpecLink()), renderDiagrams: false, focus, Frontmatter.Parse(modified), Frontmatter.Parse(original))
        { Name = "RenderedDiff" };
    }
}