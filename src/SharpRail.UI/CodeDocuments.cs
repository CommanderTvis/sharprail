using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Editor;
using SharpRail.UI.Panels;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private bool askingToSave, closeConfirmed;

    private double FileWrapWidth => LineWidths.File(Preferences.FileLineWidth, Preferences.FileLineWidthBounded);

    private CodeDocumentView CodeDocument(FileDocument document, string tabId, string key)
    {
        var view = new CodeDocumentView(document, workspaceRoot, host, () =>
        {
            var group = Layout.State.Groups.FirstOrDefault(group => Layout.Tabs(group.Id).Any(item => item.Id == tabId && item.Preview));
            if (group is not null)
            {
                var editor = Body<CodeDocumentView>(key)?.Editor;
                bool focused = editor?.IsFocused == true;
                Layout.Keep(group.Id, tabId);
                if (focused) editor!.Focus();
            }
        }, text => { documents[key] = document with { Text = text }; EmitSaved(key); }, surface.RefreshModified, Report);
        view.Editor.WrapWidth = FileWrapWidth;
        view.DeletedOnDisk = deletedDocuments.Contains(key);
        return view;
    }

    private CodeDocumentView? PendingDocument(string workspace, string tabId) =>
        Body<CodeDocumentView>(workspace + ":" + tabId) is { HasPendingChanges: true } view ? view : null;

    private void WireEditorLifetime()
    {
        Layout.CanRemoveDocument = (workspace, tabId) => PendingDocument(workspace, tabId) is null && TerminalMayClose(workspace, tabId);
        Layout.RemovalBlocked += async (blocked, retry) =>
        {
            try
            {
                if (await ResolveTerminalsAsync(blocked) &&
                    await ResolvePendingAsync(blocked.Select(item => PendingDocument(item.Workspace, item.TabId)).OfType<CodeDocumentView>().ToArray()))
                    retry();
            }
            finally { approvedTerminalCloses.Clear(); }
        };
        surface.IsModified = tab => PendingDocument(workspaceRoot, tab.Id) is not null;
        surface.IsDeleted = tab => deletedDocuments.Contains(workspaceRoot + ":" + tab.Id);
        Closing += async (_, e) =>
        {
            if (closeConfirmed) return;
            var pending = documentContent.Keys.Select(Body<CodeDocumentView>).OfType<CodeDocumentView>().Where(view => view.HasPendingChanges).ToArray();
            if (pending.Length == 0) return;
            e.Cancel = true;
            if (await ResolvePendingAsync(pending)) { closeConfirmed = true; Close(); }
        };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.S || !(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))) return;
            e.Handled = true;
            if (Layout.Selected(Layout.View.FocusedCenter) is { } tab && Body<CodeDocumentView>(workspaceRoot + ":" + tab.Id) is { } view)
                await view.SaveAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    // Returns whether the documents may now be removed.
    private async Task<bool> ResolvePendingAsync(IReadOnlyList<CodeDocumentView> pending)
    {
        if (pending.Count == 0) return true;
        if (askingToSave) return false;
        askingToSave = true;
        try
        {
            var title = pending.Count == 1
                ? $"Do you want to save the changes you made to {pending[0].FileName}?"
                : $"Do you want to save the changes to {pending.Count} files?";
            switch (await Dialogs.AskToSave(this, title, pending.Count > 1))
            {
                case Dialogs.SaveChoice.Save:
                    foreach (var view in pending) await view.SaveAsync();
                    return pending.All(view => !view.HasPendingChanges);
                case Dialogs.SaveChoice.DontSave:
                    foreach (var view in pending) view.Discard();
                    return true;
                default:
                    return false;
            }
        }
        finally { askingToSave = false; }
    }
}