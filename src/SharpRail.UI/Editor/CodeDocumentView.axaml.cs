using Avalonia.Controls;

using SharpRail.Host.Abstractions;
using SharpRail.Scintilla;

namespace SharpRail.UI.Editor;

internal sealed class CodeDocumentView : UserControl, IDisposable
{
    private readonly IProjectServices host;
    private readonly string workspace;
    private readonly string path;
    private readonly Action<Exception> report;
    private readonly Action<string> saved;
    private readonly Action modifiedChanged;
    private string original;
    private bool saving, disposed, modified, reloading;
    internal ScintillaEditor Editor { get; }
    internal string FileName => Path.GetFileName(path);
    internal bool HasPendingChanges => saving || Editor.IsModified;

    internal CodeDocumentView(FileDocument file, string workspace, IProjectServices host,
        Action changed, Action<string> saved, Action modifiedChanged, Action<Exception> report)
    {
        this.host = host; this.workspace = workspace; path = file.Path;
        original = file.Text; this.saved = saved; this.modifiedChanged = modifiedChanged; this.report = report;
        var frame = new EditorFrame(file.Text, "CodeEditor");
        Editor = frame.Editor;
        Content = frame;
        Editor.TextChanged += (_, _) => { UpdateModified(); if (Editor.IsModified && !reloading) changed(); };
        Editor.OperationFailed += (_, error) => report(error);
    }

    internal async Task SaveAsync()
    {
        if (saving || disposed || !Editor.IsModified) return;
        saving = true;
        var text = Editor.Text;
        try
        {
            await host.SaveFileAsync(new(workspace, path, original, text));
            original = text;
            if (!disposed)
            {
                if (Editor.Text == text) Editor.MarkSaved();
                saved(text);
            }
        }
        catch (Exception error) { if (!disposed) report(error); }
        finally { saving = false; if (!disposed) UpdateModified(); }
    }

    internal void Discard() { Editor.Text = original; UpdateModified(); }

    /// <summary>
    /// Follows a change on disk while the buffer has no unsaved edits. A dirty buffer is kept;
    /// saving it later reports the conflict because the host compares against the stale original.
    /// </summary>
    internal bool Reload(string text)
    {
        if (disposed || HasPendingChanges || text == original) return false;
        var line = Editor.FirstVisibleLine;
        reloading = true;
        try { original = text; Editor.Text = text; Editor.MarkSaved(); }
        finally { reloading = false; }
        Editor.ScrollToLine(line);
        UpdateModified();
        return true;
    }

    private void UpdateModified()
    {
        if (modified == HasPendingChanges) return;
        modified = HasPendingChanges;
        modifiedChanged();
    }

    public void Dispose() { if (disposed) return; disposed = true; Editor.Dispose(); }
}