using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using SharpRail.Host.Abstractions;
using SharpRail.Scintilla;
using SharpRail.UI.Rendering;
using SkiaSharp;

namespace SharpRail.UI.Editor;

internal sealed partial class CodeDocumentView : UserControl, IDisposable
{
    // Shared by every editor for the application's lifetime.
    private static readonly Lazy<SKTypeface> Typeface = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/Fonts/JetBrainsMono-Regular.ttf"));
        return SKTypeface.FromStream(stream);
    });
    private readonly IProjectServices host;
    private readonly string workspace;
    private readonly string path;
    private readonly Action<Exception> report;
    private readonly Action<string> saved;
    private readonly Action modifiedChanged;
    private readonly ScrollBar vertical;
    private readonly ScrollBar horizontal;
    private string original;
    private bool saving, disposed, modified, syncingScroll, reloading;
    internal ScintillaEditor Editor { get; }
    internal string FileName => Path.GetFileName(path);
    internal bool HasPendingChanges => saving || Editor.IsModified;

    internal CodeDocumentView(FileDocument file, string workspace, IProjectServices host,
        Action changed, Action<string> saved, Action modifiedChanged, Action<Exception> report)
    {
        AvaloniaXamlLoader.Load(this);
        this.host = host; this.workspace = workspace; path = file.Path;
        original = file.Text; this.saved = saved; this.modifiedChanged = modifiedChanged; this.report = report;
        vertical = this.FindControl<ScrollBar>("EditorVerticalScroll")!;
        horizontal = this.FindControl<ScrollBar>("EditorHorizontalScroll")!;
        Editor = new(file.Text, Typeface.Value) { Name = "CodeEditor", Colors = ThemeColors() };
        this.FindControl<ContentControl>("EditorBody")!.Content = Editor;
        Editor.TextChanged += (_, _) => { UpdateModified(); if (Editor.IsModified && !reloading) changed(); };
        Editor.OperationFailed += (_, error) => report(error);
        Editor.ScrollChanged += (_, _) => SyncScrollBars();
        vertical.ValueChanged += (_, e) => { if (!syncingScroll) Editor.ScrollToLine(e.NewValue); };
        horizontal.ValueChanged += (_, e) => { if (!syncingScroll) Editor.ScrollToX(e.NewValue); };
    }

    private static ScintillaColors ThemeColors() => new(Ui.TextBrush.Color, Ui.Surface.Color, Ui.Muted.Color,
        Ui.Over(Ui.Theme["editorSelection"], Ui.Surface.Color), Ui.Theme.Colors["editorSelectionForeground"]);

    private void ApplyTheme() => Editor.Colors = ThemeColors();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); ApplyTheme(); Ui.ThemeChanged += ApplyTheme; }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Ui.ThemeChanged -= ApplyTheme; base.OnDetachedFromVisualTree(e); }

    private void SyncScrollBars()
    {
        syncingScroll = true;
        try
        {
            Apply(vertical, Editor.VerticalScroll);
            Apply(horizontal, Editor.HorizontalScroll);
        }
        finally { syncingScroll = false; }

        static void Apply(ScrollBar bar, EditorScroll scroll)
        {
            bar.Maximum = scroll.Maximum; bar.ViewportSize = scroll.Viewport;
            bar.LargeChange = Math.Max(1, scroll.Viewport); bar.Value = scroll.Value;
            bar.IsVisible = scroll.Maximum > 0;
        }
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
