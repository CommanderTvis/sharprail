using System.Text;

namespace SharpRail.Scintilla.Core;

/// <summary>Per-line margin text and style (upstream src/PerLine.cxx LineAnnotation), used for margin text.</summary>
/// <remarks>Per-character styles (SetStyles) are not used by SharpRail and are not ported.</remarks>
internal sealed class LineAnnotation
{
    private sealed class Entry
    {
        internal string? Text;
        internal int Style;
        internal int Lines;
    }

    private readonly SplitVector<Entry?> annotations = new();

    private Entry? At(int line) =>
        annotations.Length != 0 && line >= 0 && line < annotations.Length ? annotations[line] : null;

    internal bool Empty => annotations.Length == 0;

    internal void InsertLine(int line)
    {
        if (annotations.Length == 0) return;
        annotations.EnsureLength(line);
        annotations.Insert(line, null);
    }

    internal void InsertLines(int line, int lines)
    {
        if (annotations.Length == 0) return;
        annotations.EnsureLength(line);
        annotations.InsertEmpty(line, lines);
    }

    /// <summary>Removes the entry of line <c>line - 1</c>, as upstream: the line merged into its predecessor.</summary>
    internal void RemoveLine(int line)
    {
        if (annotations.Length != 0 && line > 0 && line <= annotations.Length) annotations.Delete(line - 1);
    }

    internal int Style(int line) => At(line)?.Style ?? 0;

    internal string? Text(int line) => At(line)?.Text;

    /// <summary>Length of the text in UTF-8 bytes.</summary>
    internal int Length(int line) => At(line)?.Text is { } text ? Encoding.UTF8.GetByteCount(text) : 0;

    internal int Lines(int line) => At(line)?.Lines ?? 0;

    internal void SetText(int line, string? text)
    {
        if (text is not null && line >= 0)
        {
            annotations.EnsureLength(line + 1);
            annotations[line] = new Entry { Text = text, Style = Style(line), Lines = text.Count(c => c == '\n') + 1 };
        }
        else if (At(line) is not null)
        {
            annotations[line] = null;
        }
    }

    internal void ClearAll() => annotations.DeleteAll();

    internal void SetStyle(int line, int style)
    {
        annotations.EnsureLength(line + 1);
        var entry = annotations[line] ??= new Entry { Text = "" };
        entry.Style = style;
    }
}