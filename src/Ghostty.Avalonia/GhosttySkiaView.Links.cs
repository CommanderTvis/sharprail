using System.Text;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Ghostty.Avalonia;

public sealed partial class GhosttySkiaView
{
    private TerminalLinkHover? fileHover;
    private TerminalLinkHover FileHover => fileHover ??= new(() => Redraw(false));
    private TerminalLinkHighlights? highlights;
    private TerminalLinkHighlights Highlights => highlights ??= new(() => Redraw(false));
    /// <summary>Resolves references to underline throughout the visible terminal, without requiring hover.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? ResolveHighlightedLinks
    { get => Highlights.Resolve; set { Highlights.Clear(); Highlights.Resolve = value; Redraw(false); } }
    private TerminalLink? pressedFile;
    /// <summary>Resolves file references off the dispatcher, on the terminal's host.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? ResolveFileLinks
    { get => FileHover.Resolve; set => FileHover.Resolve = value; }
    /// <summary>Opens a resolved file in the embedding application's editor.</summary>
    public Func<TerminalLink, Task>? OpenFileLink
    { get => FileHover.Open; set => FileHover.Open = value; }

    private static readonly Regex UrlPattern = new(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly HashSet<int> linkCells = [];
    private Point linkPointer = new(-1, -1);
    private Point linkPressPointer;
    private KeyModifiers linkModifiers;
    private string? hoveredUrl, pressedUrl;

    private KeyModifiers LinkModifier => MacKeyboard ? KeyModifiers.Meta : KeyModifiers.Control;

    internal static unsafe Dictionary<int, string> FindUrls(Frame frame)
    {
        var result = new Dictionary<int, string>();
        var text = new StringBuilder();
        var positions = new List<int>();
        for (var row = 0; row < frame.Rows; row++)
        {
            for (var column = 0; column < frame.Columns; column++)
            {
                var index = row * frame.Columns + column;
                var cell = frame.Cells[index];
                if (cell.Wide >= 2) continue;
                if (cell.Length == 0) { text.Append(' '); positions.Add(index); }
                for (var i = 0; i < cell.Length; i++)
                {
                    var code = frame.Text[cell.Text + i];
                    var value = code == 0 ? " " : char.ConvertFromUtf32((int)code);
                    text.Append(value);
                    for (var j = 0; j < value.Length; j++) positions.Add(index);
                }
            }
            if (frame.Cells[row * frame.Columns].RowWrapped != 0 && row + 1 < frame.Rows) continue;
            foreach (Match match in UrlPattern.Matches(text.ToString()))
            {
                var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '(')) url = url[..^1];
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) continue;
                for (var i = match.Index; i < match.Index + url.Length; i++) result[positions[i]] = url;
            }
            text.Clear();
            positions.Clear();
        }
        return result;
    }

    private unsafe string? UrlAt(Point point)
    {
        if (fonts is not { } cells || !vt.Snapshot(out last)) return null;
        var x = (int)Math.Floor((point.X - Padding.Left) / cells.CellWidth);
        var y = (int)Math.Floor((point.Y - Padding.Top) / cells.CellHeight);
        return x >= 0 && x < last.Columns && y >= 0 && y < last.Rows
            ? FindUrls(last).GetValueOrDefault(y * last.Columns + x) : null;
    }

    private unsafe void UpdateLinks(Frame frame)
    {
        var hadLinks = linkCells.Count != 0;
        var visibleText = new StringBuilder();
        var visiblePositions = new List<int>();
        if (highlights?.Resolve is not null)
            for (var row = 0; row < frame.Rows && visibleText.Length < 16000; row++)
            {
                for (var column = 0; column < frame.Columns; column++)
                {
                    var index = row * frame.Columns + column;
                    var cell = frame.Cells[index];
                    if (cell.Wide >= 2) continue;
                    if (cell.Length == 0) { visibleText.Append(' '); visiblePositions.Add(index); }
                    for (var i = 0; i < cell.Length; i++)
                    {
                        var code = frame.Text[cell.Text + i];
                        var value = code == 0 ? " " : char.ConvertFromUtf32((int)code);
                        visibleText.Append(value);
                        foreach (var unused in value) visiblePositions.Add(index);
                    }
                }
                if (frame.Cells[row * frame.Columns].RowWrapped == 0) { visibleText.Append('\n'); visiblePositions.Add(-1); }
            }
        linkCells.Clear();
        hoveredUrl = null;
        var fileText = (string?)null;
        var fileOffset = -1;
        var filePositions = new List<int>();
        if (linkModifiers == LinkModifier && fonts is { } cells)
        {
            var x = (int)Math.Floor((linkPointer.X - Padding.Left) / cells.CellWidth);
            var y = (int)Math.Floor((linkPointer.Y - Padding.Top) / cells.CellHeight);
            if (x >= 0 && x < frame.Columns && y >= 0 && y < frame.Rows)
            {
                var row = y;
                while (row > 0 && frame.Cells[(row - 1) * frame.Columns].RowWrapped != 0) row--;
                var text = new StringBuilder();
                for (; row < frame.Rows; row++)
                {
                    for (var col = 0; col < frame.Columns; col++)
                    {
                        var index = row * frame.Columns + col;
                        var cell = frame.Cells[index];
                        if (cell.Wide >= 2) continue;
                        if (row == y && (col == x || cell.Wide == 1 && col + 1 == x)) fileOffset = text.Length;
                        if (cell.Length == 0) { text.Append(' '); filePositions.Add(index); }
                        for (var i = 0; i < cell.Length; i++)
                        {
                            var code = frame.Text[cell.Text + i];
                            var value = code == 0 ? " " : char.ConvertFromUtf32((int)code);
                            text.Append(value);
                            for (var j = 0; j < value.Length; j++) filePositions.Add(index);
                        }
                    }
                    if (frame.Cells[row * frame.Columns].RowWrapped == 0 || text.Length > 16384) break;
                }
                fileText = text.ToString();
                var urls = FindUrls(frame);
                if (urls.TryGetValue(y * frame.Columns + x, out hoveredUrl))
                    foreach (var pair in urls)
                        if (ReferenceEquals(pair.Value, hoveredUrl)) linkCells.Add(pair.Key);
            }
        }
        FileHover.Update(hoveredUrl is null ? fileText : null, fileOffset);
        if (FileHover.Link is { } file)
            for (var i = file.Start; i < file.Start + file.Length && i < filePositions.Count; i++)
            {
                var index = filePositions[i];
                linkCells.Add(index);
                if (frame.Cells[index].Wide == 1 && index + 1 < frame.Columns * frame.Rows) linkCells.Add(index + 1);
            }
        if (highlights?.Resolve is not null)
            foreach (var link in Highlights.Update(visibleText.ToString()))
                foreach (var index in visiblePositions.Skip(link.Start).Take(link.Length).Where(index => index >= 0))
                    linkCells.Add(index);
        if (hadLinks || linkCells.Count != 0)
            for (var i = 0; i < frame.Columns * frame.Rows; i++)
                frame.Cells[i].Flags = (frame.Cells[i].Flags & ~CellFlags.LinkHover) | (linkCells.Contains(i) ? CellFlags.LinkHover : 0);
        if (hadLinks != (linkCells.Count != 0)) Cursor = new Cursor(hoveredUrl is null && FileHover.Link is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
    }

    private unsafe void RefreshLink(PointerEventArgs e)
    {
        linkPointer = e.GetPosition(this);
        linkModifiers = e.KeyModifiers;
        if (linkModifiers != LinkModifier && hoveredUrl is null && FileHover.Link is null) return;
        if (vt.Snapshot(out last)) UpdateLinks(last);
        Redraw(false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (disposed) return;
        linkPointer = new(-1, -1);
        Redraw(false);
    }

    private unsafe Task<TerminalContextLink?> ContextFileLink(Point point)
    {
        linkPointer = point;
        var modifiers = linkModifiers;
        linkModifiers = LinkModifier;
        if (vt.Snapshot(out last)) UpdateLinks(last);
        linkModifiers = modifiers;
        return FileHover.ResolveContext();
    }

    private async Task OpenFileAsync(TerminalLink link)
    {
        try { await FileHover.Activate(link); }
        catch (Exception error) { OperationFailed?.Invoke(this, error); }
    }

    private async Task OpenLinkAsync(string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception error) { OperationFailed?.Invoke(this, error); }
    }
}