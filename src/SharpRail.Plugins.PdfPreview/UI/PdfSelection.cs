using System.Text;

using Avalonia;

namespace SharpRail.Plugins.PdfPreview.UI;

internal sealed class PdfSelection
{
    private readonly List<(PdfTextLayer Layer, int Offset)> layers = [];
    private readonly StringBuilder text = new();
    private int anchor = -1, caret = -1;

    public string SelectedText
    {
        get
        {
            if (anchor < 0) return "";
            var start = Math.Min(anchor, caret);
            var end = Math.Max(anchor, caret) + 1;
            if (start > 0 && char.IsLowSurrogate(text[start])) start--;
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end++;
            return text.ToString(start, end - start);
        }
    }

    public void Add(PdfTextLayer layer)
    {
        if (layers.Count > 0) text.Append('\n');
        layers.Add((layer, text.Length));
        text.Append(layer.Text);
    }

    public bool Start(PdfTextLayer layer, int character)
    {
        if (character < 0) { anchor = caret = -1; Refresh(); return false; }
        anchor = caret = layers.Single(item => item.Layer == layer).Offset + character;
        Refresh();
        return true;
    }

    public void Move(Func<PdfTextLayer, Point> position)
    {
        var nearest = layers.Where(item => item.Layer.Text.Length > 0).MinBy(item =>
        {
            var point = position(item.Layer);
            var size = item.Layer.Bounds.Size;
            return Math.Max(0, Math.Max(-point.X, point.X - size.Width)) +
                Math.Max(0, Math.Max(-point.Y, point.Y - size.Height));
        });
        if (nearest.Layer is null) return;
        var character = nearest.Layer.CharacterAt(position(nearest.Layer));
        if (character < 0) return;
        caret = nearest.Offset + character;
        Refresh();
    }

    public void SelectAll()
    {
        if (text.Length == 0) return;
        (anchor, caret) = (0, text.Length - 1);
        Refresh();
    }

    public (int Start, int End) Range(PdfTextLayer layer)
    {
        if (anchor < 0) return (-1, -1);
        var offset = layers.Single(item => item.Layer == layer).Offset;
        var start = Math.Max(0, Math.Min(anchor, caret) - offset);
        var end = Math.Min(layer.Text.Length - 1, Math.Max(anchor, caret) - offset);
        return start > end ? (-1, -1) : (start, end);
    }

    private void Refresh()
    {
        foreach (var (layer, _) in layers) layer.InvalidateVisual();
    }
}