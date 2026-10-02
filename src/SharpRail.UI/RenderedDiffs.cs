using Avalonia.Controls;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly HashSet<string> sourceDiffs = [];

    /// <summary>Merges two Markdown sources for a rendered diff; runs on a thread-pool thread.</summary>
    public Func<string, string, CancellationToken, string> RenderedDiffMerge { get; set; } = MarkdownDiff.Merge;

    private DiffView DiffDocument(FileDocument document, DockTab tab, string key)
    {
        var markdown = Path.GetExtension(tab.Path).ToLowerInvariant() is ".md" or ".markdown";
        return new DiffView(document.Text, tab.Path, LineWidths.File(Preferences.FileLineWidth, Preferences.FileLineWidthBounded),
            markdown ? token => RenderMergedAsync(tab, token) : null, !sourceDiffs.Contains(key),
            show => { if (show) sourceDiffs.Remove(key); else sourceDiffs.Add(key); },
            Plugins.FileIcon(tab.Path, SharpRail.Plugins.Api.UI.FileIconKind.File) is null ? null : FileIcon(tab.Path, false, "fileText"));
    }

    private async Task<Control?> RenderMergedAsync(DockTab tab, CancellationToken token)
    {
        var merge = RenderedDiffMerge;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var parsed = await Task.Run<(Markdig.Syntax.MarkdownDocument Document, FrontmatterBlock? Before, FrontmatterBlock? After)?>(async () =>
        {
            var sides = await host.GetDiffSidesAsync(tab.Path, tab.Scope, tab.Comparison, linked.Token);
            return sides.Original.Length + sides.Modified.Length > ViewerLimits.RenderedMarkdown
                ? null : (Document: MarkdownPreview.Parse(merge(sides.Original, sides.Modified, linked.Token)),
                    Before: Frontmatter.Parse(sides.Original), After: Frontmatter.Parse(sides.Modified));
        }, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        return parsed is not { } rendered ? null
            : new MarkdownPreview(rendered.Document, tab.Path, MarkdownContexts.For(host, Preferences, FollowLink, SpecLink()), renderDiagrams: false,
                frontmatter: rendered.After, previousFrontmatter: rendered.Before)
            { Name = "RenderedDiff" };
    }
}