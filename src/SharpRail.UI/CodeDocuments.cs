using Avalonia.Controls;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.UI.Kit.Markdown;
using SharpRail.UI.Docking;
using SharpRail.UI.Editor;
using SharpRail.UI.Panels;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private bool askingToSave, closeConfirmed;
    private readonly Dictionary<string, string> dependencySourceServers = [];

    private static bool IsDependencySource(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;

    internal async Task OpenLanguageDocumentAsync(LanguageItem item, bool keep = false)
    {
        if (IsDependencySource(item.Path)) dependencySourceServers[workspaceRoot + "\0" + item.Path] = item.Source;
        await OpenDocumentAsync(item.Path, keep, line: item.Line + 1, column: item.Column + 1);
    }

    private async Task<FileDocument> ReadDependencySourceAsync(string path, CancellationToken token)
    {
        var sourceServer = dependencySourceServers.GetValueOrDefault(workspaceRoot + "\0" + path) ?? "";
        if (sourceServer.Length == 0) throw new IOException("Reopen this dependency from Go to declaration or Find usages in the current language-server session.");
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var result = await host.LanguageAsync(new(workspaceRoot, path, "source", Server: sourceServer, Offset: text.Length), token);
            if (result.State != "ready" || result.Locations.Count != 1) throw new IOException(result.Detail);
            var body = result.Locations[0].Body;
            if (body.Length == 0 && result.Truncated) throw new IOException("The language server returned no source continuation.");
            if (text.Length + body.Length > 1_048_576) throw new IOException("Dependency source exceeds the 1 Mi character limit.");
            text.Append(body);
            if (!result.Truncated) return new(path, text.ToString()) { ReadOnly = true };
        }
    }

    private double FileWrapWidth => LineWidths.File(Preferences.FileLineWidth, Preferences.FileLineWidthBounded);

    private CodeDocumentView CodeDocument(FileDocument document, string tabId, string key, bool find)
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
        }, text => { documents[key] = document with { Text = text }; EmitSaved(key); }, () => { surface.RefreshModified(); SaveStateChanged?.Invoke(); }, Report, find);
        view.LanguageSourceServer = document.ReadOnly && IsDependencySource(document.Path) ? dependencySourceServers.GetValueOrDefault(workspaceRoot + "\0" + document.Path) ?? "" : "";
        view.Editor.WrapWidth = FileWrapWidth;
        view.DeletedOnDisk = deletedDocuments.Contains(key);
        ReportSelections(view.Editor, new DockTab(tabId, Path.GetFileName(document.Path), "file", document.Path));
        OfferFileActions(view.Editor, document.Path);
        if (view.Editor.ContextMenu is { } menu) view.AddLanguageMenu(menu);
        var workspace = workspaceRoot;
        view.NavigateLanguage = item => workspaceRoot == workspace
            ? OpenLanguageDocumentAsync(item) : Task.CompletedTask;
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
            if (closeConfirmed || workbench.ExitConfirmed) return;
            var pending = documentContent.Keys.Select(Body<CodeDocumentView>).OfType<CodeDocumentView>().Where(view => view.HasPendingChanges).ToArray();
            // Closing the last window, or quitting, ends every agent's turn.
            var exiting = (workbench.ShuttingDown || workbench.Windows.Count == 1) && workbench.AgentInProgress;
            if (pending.Length == 0 && !exiting) return;
            e.Cancel = true;
            if (exiting && workbench.AskingToExit) return;
            if (pending.Length > 0 && !await ResolvePendingAsync(pending)) { workbench.ShuttingDown = false; return; }
            if (exiting && !await ConfirmExitAsync()) return;
            closeConfirmed = true;
            if (workbench.ShuttingDown) workbench.Commands.Shutdown(); else Close();
        };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.S || !(e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))) return;
            e.Handled = true;
            await SaveSelectedAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Layout.SelectionChanged += _ => SaveStateChanged?.Invoke();
        Layout.Focused += () => SaveStateChanged?.Invoke();
        Layout.Changed += () => SaveStateChanged?.Invoke();
    }

    /// <summary>The focused center group's selected file editor, the one Command-S saves.</summary>
    // A window whose workspace has not mounted yet has no focused center group to read.
    private CodeDocumentView? SaveTarget =>
        Layout.State.Groups.Any(group => group.Id == Layout.View.FocusedCenter) && Layout.Selected(Layout.View.FocusedCenter) is { } tab
            ? Body<CodeDocumentView>(workspaceRoot + ":" + tab.Id) : null;

    /// <summary>Whether Command-S has unsaved changes to save; <see cref="SaveStateChanged"/> says when this may have changed.</summary>
    internal bool CanSave => SaveTarget is { HasPendingChanges: true };

    internal event Action? SaveStateChanged;

    /// <summary>Command-S: saves the focused center group's selected file editor.</summary>
    internal async Task SaveSelectedAsync()
    {
        if (SaveTarget is { } view) await view.SaveAsync();
    }

    // Returns whether the app may exit with agents still working.
    private async Task<bool> ConfirmExitAsync()
    {
        workbench.AskingToExit = true;
        try
        {
            if (await Dialogs.Confirm(this, "Quit with an agent working?", "An agent is in the middle of a turn. Quitting ends its terminal session.", "Quit", "ExitWithAgentConfirm"))
            {
                workbench.ExitConfirmed = true;
                return true;
            }
            workbench.ShuttingDown = false;
            return false;
        }
        finally { workbench.AskingToExit = false; }
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