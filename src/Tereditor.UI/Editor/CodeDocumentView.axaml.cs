using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Host.Abstractions;
using SharpRail.Scintilla;

namespace SharpRail.UI.Editor;

internal sealed partial class CodeDocumentView : UserControl, IDisposable
{
    private readonly IProjectServices host;
    private readonly string workspace;
    private string path;
    private readonly Action<Exception> report;
    private readonly Action<string> saved;
    private readonly Action modifiedChanged;
    private string original;
    private bool saving, disposed, modified, reloading;
    internal ScintillaEditor Editor { get; }
    internal string FileName => Path.GetFileName(path);
    /// <summary>The workspace-relative path this buffer saves to; a rename followed by <see cref="Retarget"/> changes it.</summary>
    internal string FilePath => path;
    /// <summary>The workspace root this buffer was opened in and saves to, whichever workspace the window shows.</summary>
    internal string Workspace => workspace;
    internal bool IsDisposed => disposed;
    internal bool HasPendingChanges => saving || Editor.IsModified;
    private readonly Border deletedBanner, changedBanner;

    /// <summary>The file on disk now differs from the text this buffer started from while it has unsaved edits; a banner says so until it is saved or reloaded.</summary>
    internal bool ChangedOnDisk { get => changedBanner.IsVisible; set => changedBanner.IsVisible = value; }

    /// <summary>The file is gone from disk; the buffer stays and a banner says so until the file returns.</summary>
    internal bool DeletedOnDisk { get => deletedBanner.IsVisible; set => deletedBanner.IsVisible = value; }

    internal CodeDocumentView(FileDocument file, string workspace, IProjectServices host,
        Action changed, Action<string> saved, Action modifiedChanged, Action<Exception> report, bool find = true)
    {
        AvaloniaXamlLoader.Load(this);
        this.host = host; this.workspace = workspace; path = file.Path;
        original = file.Text; this.saved = saved; this.modifiedChanged = modifiedChanged; this.report = report;
        frame = new EditorFrame(file.Text, "CodeEditor") { DeferSyntaxColors = true };
        frame.Highlight(file.Path);
        frame.Opacity = 0;
        Editor = frame.Editor;
        Editor.IsReadOnly = file.ReadOnly;
        deletedBanner = this.FindControl<Border>("FileDeletedOnDisk")!;
        deletedBanner.Background = Ui.Elevated; deletedBanner.BorderBrush = Ui.Danger;
        changedBanner = this.FindControl<Border>("FileChangedOnDisk")!;
        changedBanner.Background = Ui.Elevated; changedBanner.BorderBrush = Ui.Danger;
        this.FindControl<ContentControl>("CodeBody")!.Content = frame;
        if (find)
        {
            var bar = new FindBar(() => Editor);
            this.FindControl<ContentControl>("CodeFindBody")!.Content = bar;
            bar.Attach(this);
        }

        Editor.TextChanged += (_, _) => { UpdateModified(); if (Editor.IsModified && !reloading) changed(); };
        Editor.OperationFailed += (_, error) => report(error);
        WireLanguage();
    }

    internal async Task SaveAsync()
    {
        if (await TrySaveAsync() is { } error && !disposed) report(error);
    }

    /// <summary>Saves to this buffer's own workspace; returns why it failed (the buffer keeps its edits) or null.</summary>
    internal async Task<Exception?> TrySaveAsync(bool overwrite = false)
    {
        if (saving || disposed || !Editor.IsModified) return null;
        saving = true;
        var text = Editor.Text;
        try
        {
            await host.SaveFileAsync(new(workspace, path, original, text, overwrite));
            original = text;
            if (!disposed) ChangedOnDisk = false;
            if (!disposed)
            {
                if (Editor.Text == text) Editor.MarkSaved();
                saved(text);
            }
            return null;
        }
        catch (Exception error) { return error; }
        finally { saving = false; if (!disposed) UpdateModified(); }
    }

    /// <summary>
    /// Follows a rename of this file: saves go to <paramref name="next"/>, highlighting follows its extension and the
    /// language server closes the old document; the next inspection opens the new one. Text, selection, scroll and
    /// undo history stay because the buffer is the same.
    /// </summary>
    internal void Retarget(string next)
    {
        if (disposed || next == path) return;
        StopLanguage();
        path = next;
        frame.Highlight(next);
        if (IsEffectivelyVisible) languageTimer.Start();
    }

    internal void Discard() { Editor.Text = original; ChangedOnDisk = false; UpdateModified(); }

    /// <summary>
    /// Throws away the unsaved edits and shows what is on disk now, after the user confirmed losing them; the read happens off the UI thread.
    /// </summary>
    internal async Task ReloadFromDiskAsync()
    {
        var file = await host.ReadFileAsync(path);
        if (disposed) return;
        var line = Editor.FirstVisibleLine;
        reloading = true;
        try { original = file.Text; Editor.Text = file.Text; Editor.MarkSaved(); }
        finally { reloading = false; }
        Editor.ScrollToLine(line);
        ChangedOnDisk = false;
        saved(file.Text);
        UpdateModified();
    }

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
        if (!HasPendingChanges && changedBanner.IsVisible) ChangedOnDisk = false;
        if (modified == HasPendingChanges) return;
        modified = HasPendingChanges;
        modifiedChanged();
    }

    public void Dispose() { if (disposed) return; disposed = true; StopLanguage(); Editor.Dispose(); }
}