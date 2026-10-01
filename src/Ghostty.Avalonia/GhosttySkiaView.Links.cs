using System.Text;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Ghostty.Avalonia;

public sealed partial class GhosttySkiaView
{
    private static readonly Regex UrlPattern = new(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly HashSet<int> linkCells = [];
    private Point linkPointer = new(-1, -1);
    private Point linkPressPointer;
    private KeyModifiers linkModifiers;
    private string? hoveredUrl, pressedUrl;

    private static KeyModifiers LinkModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

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

    private unsafe void UpdateLinks(Frame frame)
    {
        linkCells.Clear();
        hoveredUrl = null;
        if (linkModifiers == LinkModifier && fonts is { } cells)
        {
            var x = (int)Math.Floor((linkPointer.X - Padding.Left) / cells.CellWidth);
            var y = (int)Math.Floor((linkPointer.Y - Padding.Top) / cells.CellHeight);
            if (x >= 0 && x < frame.Columns && y >= 0 && y < frame.Rows)
            {
                var urls = FindUrls(frame);
                if (urls.TryGetValue(y * frame.Columns + x, out hoveredUrl))
                    foreach (var pair in urls)
                        if (ReferenceEquals(pair.Value, hoveredUrl)) linkCells.Add(pair.Key);
            }
        }
        for (var i = 0; i < frame.Columns * frame.Rows; i++)
            frame.Cells[i].Flags = (frame.Cells[i].Flags & ~CellFlags.LinkHover) | (linkCells.Contains(i) ? CellFlags.LinkHover : 0);
        Cursor = new Cursor(hoveredUrl is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
    }

    private unsafe void RefreshLink(PointerEventArgs e)
    {
        linkPointer = e.GetPosition(this);
        linkModifiers = e.KeyModifiers;
        if (linkModifiers != LinkModifier && hoveredUrl is null) return;
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

    private async Task OpenLinkAsync(string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception error) { OperationFailed?.Invoke(this, error); }
    }
}