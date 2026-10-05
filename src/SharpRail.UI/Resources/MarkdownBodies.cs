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