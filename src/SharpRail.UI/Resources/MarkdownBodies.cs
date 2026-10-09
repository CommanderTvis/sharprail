using Avalonia;
using Avalonia.Controls;

using SharpRail.UI.Editor;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Resources;

/// <summary>The rendered Markdown view of a file; a reload keeps the reader's place.</summary>
internal sealed class MarkdownPreviewBody : Decorator, IResourceBody, IDisposable
{
    private readonly Func<string, MarkdownPreview> create;
    private MarkdownPreview preview;

    internal MarkdownPreviewBody(ResourceView view, Func<string, MarkdownPreview> create)
    {
        this.create = create;
        Child = preview = create(Text(view.Content));
        if (view.ViewState is Vector offset) preview.Offset = offset;
    }

    private static string Text(ResourceContent content) => (content as ResourceContent.Text)?.Value ?? "";

    public object? ViewState => preview.Offset;

    public bool Reload(ResourceContent content)
    {
        var offset = preview.Offset;
        preview.Dispose();
        Child = preview = create(Text(content));
        preview.Offset = offset;
        return true;
    }

    internal void ScrollToAnchor(string id) => preview.ScrollToAnchor(id);

    public void Dispose() => preview.Dispose();
}

/// <summary>The read-only source of a Markdown file that also has a rendered view.</summary>
internal sealed class MarkdownSourceBody : Decorator, IResourceBody, IDisposable
{
    private readonly EditorFrame frame;

    internal MarkdownSourceBody(ResourceView view, double wrapWidth)
    {
        frame = new EditorFrame((view.Content as ResourceContent.Text)?.Value ?? "", "MarkdownSource");
        frame.Editor.IsReadOnly = true;
        frame.Editor.WrapWidth = wrapWidth;
        Child = frame;
        if (view.ViewState is int line) frame.Editor.ScrollToLine(line);
    }

    internal double WrapWidth { set => frame.Editor.WrapWidth = value; }

    public object? ViewState => frame.Editor.FirstVisibleLine;

    public bool Reload(ResourceContent content)
    {
        if (content is not ResourceContent.Text text) return false;
        var line = frame.Editor.FirstVisibleLine;
        frame.Editor.IsReadOnly = false;
        try { frame.Editor.Text = text.Value; }
        finally { frame.Editor.IsReadOnly = true; }
        frame.Editor.ScrollToLine(line);
        return true;
    }

    public void Dispose() => frame.Editor.Dispose();
}