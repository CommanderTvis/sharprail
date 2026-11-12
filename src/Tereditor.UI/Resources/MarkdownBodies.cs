using Avalonia;
using Avalonia.Controls;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Resources;

/// <summary>The rendered Markdown view of a file; a reload keeps the reader's place.</summary>
internal sealed class MarkdownPreviewBody : Decorator, IResourceBody, IDisposable
{
    private readonly MarkdownDocumentView document;
    private string text;

    internal MarkdownPreviewBody(ResourceView view, Func<string, MarkdownDocumentView> create)
    {
        text = Text(view.Content);
        Child = document = create(text);
        if (view.ViewState is MarkdownDocumentView.ViewState state) document.Restore(state);
        else if (view.ViewState is Vector offset) document.Preview.Offset = offset;
    }

    private static string Text(ResourceContent content) => (content as ResourceContent.Text)?.Value ?? "";

    public object? ViewState => document.State;

    public bool Reload(ResourceContent content)
    {
        text = Text(content);
        _ = document.RefreshPreviewAsync(text);
        return true;
    }

    internal void Refresh(MarkdownContext context) => _ = document.RefreshPreviewAsync(text, context);

    internal void ScrollToAnchor(string id)
    {
        document.Show(MarkdownDocumentView.Mode.Preview);
        document.Preview.ScrollToAnchor(id);
    }

    public void Dispose() => document.Dispose();
}

/// <summary>A file's text where the editor has no native library: one read-only code block, replaced when the file changes.</summary>
internal sealed class PlainSourceBody : ScrollViewer, IResourceBody
{
    private string text;

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    internal PlainSourceBody(string text)
    {
        Margin = new Thickness(24);
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Content = MarkdownPreview.Code(this.text = text);
    }

    public object? ViewState => null;

    public bool Reload(ResourceContent content)
    {
        if (content is not ResourceContent.Text next) return false;
        if (next.Value != text) Content = MarkdownPreview.Code(text = next.Value);
        return true;
    }
}