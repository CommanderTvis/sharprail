using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Editor;
using SharpRail.UI.Panels;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private bool askingToSave, closeConfirmed;

    private double FileWrapWidth => Preferences.BoundPreviewWidth ? Preferences.PreviewWidth : double.PositiveInfinity;

    private CodeDocumentView CodeDocument(FileDocument document, DockTab tab, string key)
    {
        var view = new CodeDocumentView(document, workspaceRoot, host, () =>
        {
            var group = Layout.State.Groups.FirstOrDefault(group => Layout.Tabs(group.Id).Any(item => item.Id == tab.Id && item.Preview));
            if (group is not null)
            {
                var editor = (documentContent.GetValueOrDefault(key) as CodeDocumentView)?.Editor;
                bool focused = editor?.IsFocused == true;
                Layout.Keep(group.Id, tab.Id);
                if (focused) editor!.Focus();
            }
        }, text => documents[key] = document with { Text = text }, surface.RefreshModified, Report);
        view.Editor.WrapWidth = FileWrapWidth;
        return view;
    }

    private CodeDocumentView? PendingDocument(string workspace, string tabId) =>
        documentContent.GetValueOrDefault(workspace + ":" + tabId) is CodeDocumentView { HasPendingChanges: true } view ? view : null;

    private void WireEditorLifetime()
    {
        Layout.CanRemoveDocument = (workspace, tabId) => PendingDocument(workspace, tabId) is null;
        Layout.RemovalBlocked += async (blocked, retry) =>
        {
            if (await ResolvePendingAsync(blocked.Select(item => PendingDocument(item.Workspace, item.TabId)).OfType<CodeDocumentView>().ToArray()))
                retry();
        };
        surface.IsModified = tab => PendingDocument(workspaceRoot, tab.Id) is not null;
        Closing += async (_, e) =>
        {
            if (closeConfirmed) return;
            var pending = documentContent.Values.OfType<CodeDocumentView>().Where(view => view.HasPendingChanges).ToArray();
            if (pending.Length == 0) return;
            e.Cancel = true;
            if (await ResolvePendingAsync(pending)) { closeConfirmed = true; Close(); }
        };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.S || !(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))) return;
            e.Handled = true;
            if (Layout.Selected(Layout.View.FocusedCenter) is { } tab && documentContent.GetValueOrDefault(workspaceRoot + ":" + tab.Id) is CodeDocumentView view)
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
