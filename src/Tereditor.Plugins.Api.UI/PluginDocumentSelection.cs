namespace SharpRail.Plugins.Api.UI;

/// <summary>
/// The selection a user made in a plugin's own document (reported through <see cref="IPluginEditors.ReportSelection"/>, such as a
/// Visualize Markdown drawing's file), kept per workspace for agent integrations whose editor list does not hold it. It stands until a
/// listed editor is selected in or activated, or the selection collapses.
/// </summary>
public sealed class PluginDocumentSelection
{
    private readonly Dictionary<string, (EditorRef Editor, EditorSelection Selection)> current = [];

    /// <summary>Feeds one editor event.</summary>
    /// <param name="editorEvent">The event.</param>
    /// <param name="isListed">Whether an editor is one the window lists as open.</param>
    public void Observe(EditorEvent editorEvent, Func<EditorRef, bool> isListed)
    {
        var workspace = editorEvent.Editor.WorkspaceId;
        switch (editorEvent)
        {
            case EditorSelectionEvent selected:
                if (isListed(selected.Editor)) current.Remove(workspace);
                else if (selected.Selection is { Mapped: true, Text.Length: > 0 } chosen) current[workspace] = (selected.Editor, chosen);
                else if (current.TryGetValue(workspace, out var held) && held.Editor.Id == selected.Editor.Id) current.Remove(workspace);
                break;
            case EditorLifecycleEvent { Lifecycle: EditorLifecycle.Activated }:
                current.Remove(workspace);
                break;
            case EditorLifecycleEvent { Lifecycle: EditorLifecycle.Closed } when current.TryGetValue(workspace, out var open) && open.Editor.Id == editorEvent.Editor.Id:
                current.Remove(workspace);
                break;
        }
    }

    /// <summary>The standing selection in a workspace's plugin document, or <see langword="null"/>.</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns>The document and its selection.</returns>
    public (EditorRef Editor, EditorSelection Selection)? For(string workspaceId) => current.TryGetValue(workspaceId, out var held) ? held : null;
}