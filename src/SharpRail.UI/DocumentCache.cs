using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;

using SharpRail.UI.Resources;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly HashSet<string> restoringDocuments = [];
    private readonly Dictionary<string, HashSet<string>> documentWorkspaces = [];

    private HashSet<string> LiveDocuments() => Layout.State.Workspaces.SelectMany(workspace =>
        workspace.Value.Documents.Values.SelectMany(tabs => tabs).Select(tab => workspace.Key + ":" + tab.Id)).ToHashSet();

    private void PruneDocuments()
    {
        var live = LiveDocuments();
        deletedDocuments.RemoveWhere(key => !live.Contains(key));
        foreach (var key in documents.Keys.Where(key => !live.Contains(key)).ToArray())
        { documents.Remove(key); DropDocumentContent(key); }
        foreach (var key in documentContent.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            // A closed terminal tab ends its host session; other drops only detach the window from it.
            if (documentContent[key] is Terminal.TerminalView terminal) terminal.Close();
            DropDocumentContent(key);
        }
        foreach (var key in tabViews.Keys.Where(key => !live.Contains(key)).ToArray()) tabViews.Remove(key);
    }

    private void DropDocumentContent(string key)
    {
        if (!documentContent.Remove(key, out var control)) return;
        if (control is IDisposable disposable) disposable.Dispose();
        else foreach (var image in control.GetLogicalDescendants().OfType<Image>())
            (image.Source as Bitmap)?.Dispose();
    }

    private void ReleaseProjectDocuments(string project)
    {
        var roots = documentWorkspaces.GetValueOrDefault(project) ?? [];
        if (project == projectRoot)
        {
            roots.Add(workspaceRoot);
            if (WorkspaceMounted)
            {
                projectRequest++; WorkspaceMounted = false;
                gitRefresh?.Cancel(); StopWatching();
            }
        }
        foreach (var workspace in roots)
        {
            var prefix = workspace + ":";
            foreach (var key in documents.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) documents.Remove(key);
            foreach (var key in documentContent.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) DropDocumentContent(key);
            deletedDocuments.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
            selectionHistory.Remove(workspace);
        }
        documentWorkspaces.Remove(project);
        InvalidatePluginTools(project);
    }

    /// <summary>Reattaches every open terminal tab, for example with another renderer; shells keep running.</summary>
    internal void RestartTerminals()
    {
        foreach (var terminal in documentContent.Values.OfType<Terminal.TerminalView>().Where(terminal => !terminal.IsDetached)) terminal.Restart();
    }

    private void ClearDocumentContent(bool preserveDocuments = false)
    {
        foreach (var key in documentContent.Keys.ToArray())
            if (!preserveDocuments || documentContent[key] is not (Terminal.TerminalView or SharpRail.UI.Resources.ResourcePane)) DropDocumentContent(key);
            else if (documentContent[key] is SharpRail.UI.Resources.ResourcePane pane)
            {
                if (pane.Find<MarkdownPreviewBody>() is { } markdown)
                    markdown.Refresh(Rendering.MarkdownContexts.For(host, Preferences, FollowLink, SpecLink()));
                pane.Reset(body => body is Editor.CodeDocumentView or MarkdownPreviewBody);
                if (pane.Find<Editor.CodeDocumentView>() is { } view) view.Editor.WrapWidth = FileWrapWidth;
            }
    }
}