using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly HashSet<string> restoringDocuments = [];

    private HashSet<string> LiveDocuments() => Layout.State.Workspaces.SelectMany(workspace =>
        workspace.Value.Documents.Values.SelectMany(tabs => tabs).Select(tab => workspace.Key + ":" + tab.Id)).ToHashSet();

    private void PruneDocuments()
    {
        var live = LiveDocuments();
        foreach (var key in documents.Keys.Where(key => !live.Contains(key)).ToArray())
        { documents.Remove(key); DropDocumentContent(key); }
        foreach (var key in documentContent.Keys.Where(key => !live.Contains(key)).ToArray())
            DropDocumentContent(key);
    }

    private void DropDocumentContent(string key)
    {
        if (!documentContent.Remove(key, out var control)) return;
        if (control is IDisposable disposable) disposable.Dispose();
        else foreach (var image in control.GetLogicalDescendants().OfType<Image>())
            (image.Source as Bitmap)?.Dispose();
    }

    private void ClearDocumentContent(bool preserveDocuments = false)
    {
        foreach (var key in documentContent.Keys.ToArray())
            if (!preserveDocuments || documentContent[key] is not (Terminal.GhosttyTerminal or Editor.CodeDocumentView)) DropDocumentContent(key);
            else if (documentContent[key] is Editor.CodeDocumentView view) view.Editor.WrapWidth = FileWrapWidth;
    }
}
