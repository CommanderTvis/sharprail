using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit.Editor;
using SharpRail.Scintilla;
namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>A Markdown file with Preview, Source and Split views, an outline column and find.</summary>
public sealed partial class MarkdownDocumentView : UserControl, IDisposable
{
    public enum Mode { Preview, Source, Split }

    private const double SourceLineHeight = 21;
    private MarkdownPreview preview;
    private readonly string path;
    private MarkdownContext context;
    private Control? source;
    private readonly Func<string, Control>? sourceFactory;
    private int previewVersion;
    private bool disposed;
    private string sourceText;
    private readonly Button previewButton;
    private readonly Button sourceButton;
    private readonly Button splitButton;
    private readonly Button outlineButton;
    private readonly Grid body;
    private readonly Border outline;
    private readonly GridSplitter splitter;
    private readonly FindBar find;
    private bool findSource;
    private Mode mode;

    public MarkdownDocumentView(string text, string path, MarkdownContext context) : this(text, path, context, null) { }

    /// <summary>Creates a Markdown view with an optional, lazily created source control supplied by its owner.</summary>
    public MarkdownDocumentView(string text, string path, MarkdownContext context, Func<string, Control>? sourceFactory)
    {
        this.sourceFactory = sourceFactory;
        AvaloniaXamlLoader.Load(this);
        this.path = path; this.context = context;
        preview = new(text, path, context);
        sourceText = text;
        previewButton = this.FindControl<Button>("MarkdownPreviewMode")!;
        sourceButton = this.FindControl<Button>("MarkdownSourceMode")!;
        splitButton = this.FindControl<Button>("MarkdownSplitMode")!;
        outlineButton = this.FindControl<Button>("MarkdownOutlineToggle")!;
        body = this.FindControl<Grid>("MarkdownBody")!;
        outline = this.FindControl<Border>("MarkdownOutline")!;
        splitter = this.FindControl<GridSplitter>("MarkdownSplitter")!;
        Grid.SetColumn(preview, 3);
        body.Children.Add(preview);
        previewButton.Click += (_, _) => Show(Mode.Preview);
        sourceButton.Click += (_, _) => Show(Mode.Source);
        splitButton.Click += (_, _) => Show(Mode.Split);
        outlineButton.Click += (_, _) => { outline.IsVisible = !outline.IsVisible; Paint(outlineButton, outline.IsVisible); };
        Outline.Fill(this.FindControl<StackPanel>("MarkdownOutlineEntries")!, preview, sourceText,
            RevealSource);
        Paint(outlineButton, false);
        find = new FindBar(() => mode == Mode.Source || mode == Mode.Split && findSource ? source : preview) { ZIndex = 1 };
        Grid.SetColumnSpan(find, 4);
        body.Children.Add(find);
        find.Attach(this);
        body.GotFocus += (_, _) =>
        {
            if (source?.IsKeyboardFocusWithin == true) findSource = true;
            else if (preview.IsKeyboardFocusWithin) findSource = false;
        };
        Show(Mode.Preview);
    }

    public Mode View => mode;
    public MarkdownPreview Preview => preview;
    /// <summary>The source control after Source or Split has been opened.</summary>
    public Control? Source => source;
    /// <summary>Raised when the preview is replaced, so owners can attach navigation and selection observers.</summary>
    public event Action? PreviewChanged;

    private ScintillaEditor? SourceEditor => source is EditorFrame frame ? frame.Editor
        : source?.GetLogicalDescendants().OfType<ScintillaEditor>().FirstOrDefault();

    /// <summary>Where the reader is: the mode and both panes' scroll positions, for a view that replaces this one.</summary>
    public readonly record struct ViewState(Mode Mode, Vector Preview, Vector Source);

    public ViewState State => new(mode, preview.Offset, SourceOffset);

    private ViewState? pending;
    private int pendingPasses;

    /// <summary>
    /// Takes on a previous view's state: its mode at once, its scroll positions once the panes are long enough. Images
    /// can finish later and lengthen the document, so a few layout passes may pass before the offsets hold.
    /// </summary>
    public void Restore(ViewState state)
    {
        Show(state.Mode);
        pending = state; pendingPasses = 0;
        LayoutUpdated -= ApplyPending;
        LayoutUpdated += ApplyPending;
    }

    private void ApplyPending(object? sender, EventArgs e)
    {
        if (pending is not { } state) return;
        preview.Offset = state.Preview;
        SetSourceOffset(state.Source);
        var held = Math.Abs(preview.Offset.Y - state.Preview.Y) < 1 && (source is null || Math.Abs(SourceOffset.Y - state.Source.Y) < 1);
        if (held || ++pendingPasses >= 20) { pending = null; LayoutUpdated -= ApplyPending; }
    }

    public void Show(Mode next)
    {
        if (mode != next) find.Close();
        mode = next;
        if (next != Mode.Preview && source is null)
        {
            if (sourceFactory is not null) source = sourceFactory(sourceText);
            else if (ScintillaEditor.IsSupported)
            {
                var frame = new EditorFrame(sourceText, "MarkdownSource");
                frame.Editor.IsReadOnly = true;
                frame.Editor.WrapWidth = context.SourceWrapWidth;
                source = frame;
            }
            else source = new ScrollViewer
            {
                Name = "MarkdownSource",
                Content = MarkdownPreview.Code(sourceText),
                Margin = new Thickness(24),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(source, 1);
            body.Children.Add(source);
        }
        var showSource = next != Mode.Preview;
        var showPreview = next != Mode.Source;
        body.ColumnDefinitions[1].Width = showSource ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        body.ColumnDefinitions[3].Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (source is not null) source.IsVisible = showSource;
        preview.IsVisible = showPreview;
        splitter.IsVisible = next == Mode.Split;
        Paint(previewButton, next == Mode.Preview);
        Paint(sourceButton, next == Mode.Source);
        Paint(splitButton, next == Mode.Split);
    }

    /// <summary>Updates the reading context and preview while retaining the owner's source control.</summary>
    public Task RefreshPreviewAsync(string text, MarkdownContext context)
    {
        this.context = context;
        return RefreshPreviewAsync(text);
    }

    /// <summary>Updates the live preview after an edit, parsing in the background and rejecting superseded results.</summary>
    public async Task RefreshPreviewAsync(string text)
    {
        sourceText = text;
        var version = ++previewVersion;
        await Task.Delay(150);
        if (disposed || version != previewVersion) return;
        var parsed = await Task.Run(() => (Document: MarkdownPreview.Parse(text), Frontmatter: Frontmatter.Parse(text)));
        if (disposed || version != previewVersion) return;
        ReplacePreview(new MarkdownPreview(parsed.Document, path, context, frontmatter: parsed.Frontmatter), text);
    }

    /// <summary>Reloads disk content while preserving the selected view and its controls.</summary>
    public void Reload(string text)
    {
        ++previewVersion;
        ReplacePreview(new MarkdownPreview(text, path, context), text);
        if (source is EditorFrame frame)
        {
            var readOnly = frame.Editor.IsReadOnly;
            frame.Editor.IsReadOnly = false;
            try { frame.Editor.Text = sourceText; }
            finally { frame.Editor.IsReadOnly = readOnly; }
        }
        else if (source is ScrollViewer scroll) scroll.Content = MarkdownPreview.Code(sourceText);
    }

    private void ReplacePreview(MarkdownPreview next, string text)
    {
        var state = State;
        preview.Dispose();
        body.Children.Remove(preview);
        preview = next;
        Grid.SetColumn(preview, 3);
        body.Children.Add(preview);
        sourceText = text;
        var entries = this.FindControl<StackPanel>("MarkdownOutlineEntries")!;
        entries.Children.Clear();
        Outline.Fill(entries, preview, sourceText, RevealSource);
        Restore(state);
        PreviewChanged?.Invoke();
    }

    private Vector SourceOffset => SourceEditor is { } editor
        ? new Vector(0, editor.FirstVisibleLine * SourceLineHeight)
        : (source as ScrollViewer)?.Offset ?? default;

    private void SetSourceOffset(Vector offset)
    {
        if (SourceEditor is { } editor) editor.ScrollToLine(offset.Y / SourceLineHeight);
        else if (source is ScrollViewer scroll) scroll.Offset = offset;
    }

    private void RevealSource(int line)
    {
        if (source?.IsVisible != true) return;
        if (SourceEditor is { } editor) editor.ScrollToLine(line);
        else if (source is ScrollViewer scroll) scroll.Offset = new Vector(scroll.Offset.X, line * SourceLineHeight);
    }

    private static void Paint(Button button, bool active)
    {
        button.Background = active ? Ui.Hover : Brushes.Transparent;
        button.Foreground = active ? Ui.TextBrush : Ui.Muted;
    }

    public void ScrollToAnchor(string id) { if (mode == Mode.Source) Show(Mode.Preview); preview.ScrollToAnchor(id); }
    public void Dispose()
    {
        disposed = true; ++previewVersion;
        preview.Dispose();
        if (source is IDisposable disposable) disposable.Dispose();
        else SourceEditor?.Dispose();
    }
}