using System.Runtime.InteropServices;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ghostty.Avalonia;

public sealed partial class GhosttyTextureView
{
    private TerminalLinkHover? fileHover;
    private TerminalLinkHover FileHover => fileHover ??= new(() => { RefreshFileLink(); InvalidateVisual(); });
    private TerminalLinkHighlights? highlights;
    private bool highlightDirty = true;
    private readonly DispatcherTimer highlightTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    // Cells Ghostty's renderer underlines, by viewport index (row * columns + column) with the base codepoint each
    // showed. The renderer draws them with the text of the same frame and drops a cell whose text changed.
    private readonly Dictionary<int, uint> highlightMarks = [];
    private readonly Dictionary<int, uint> hoverMarks = [];
    private readonly List<(Rect Cell, TerminalLink Link)> highlightTargets = [];
    private int[] pushedCells = [];
    /// <summary>The viewport cells last handed to the renderer to underline, for checks.</summary>
    internal IReadOnlyList<int> UnderlinedCells => pushedCells;
    internal IReadOnlyList<(string Visible, TerminalLink Link)> HighlightedLinks => highlights?.Underlined ?? [];
    /// <summary>Resolves references to underline throughout the visible terminal, without requiring hover.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? ResolveHighlightedLinks
    {
        get => highlights?.Resolve;
        set
        {
            highlights ??= new(() => { RefreshHighlights(); InvalidateVisual(); });
            highlights.Clear();
            highlights.Resolve = value;
            ClearHighlights();
            highlightDirty = true;
            if (active && value is not null) highlightTimer.Start();
            else highlightTimer.Stop();
            InvalidateVisual();
        }
    }

    private void ClearHighlights()
    {
        highlightMarks.Clear();
        highlightTargets.Clear();
        PushUnderlines();
    }

    private void PushUnderlines()
    {
        if (disposed) return;
        var cells = highlightMarks.Keys.Union(hoverMarks.Keys).Order().ToArray();
        if (cells.SequenceEqual(pushedCells)) return;
        var codepoints = cells.Select(cell => highlightMarks.TryGetValue(cell, out var code) ? code : hoverMarks[cell]).ToArray();
        if (SetLinkUnderlines(terminal.Handle, cells.Select(cell => (uint)cell).ToArray(), codepoints, (nuint)cells.Length)) pushedCells = cells;
    }

    private void RefreshHighlights()
    {
        highlightMarks.Clear();
        highlightTargets.Clear();
        if (!active || disposed || highlights?.Resolve is null || Bounds.Width <= 0 || Bounds.Height <= 0) { PushUnderlines(); return; }
        var scalars = new uint[16384];
        var cells = new uint[scalars.Length];
        var info = new double[6];
        if (ReadLinkLine(terminal.Handle, Bounds.Width / 2, Bounds.Height / 2, scalars, cells, (nuint)scalars.Length, info) == 0 || info[2] <= 0 || info[3] <= 0)
        { PushUnderlines(); return; }
        var text = new StringBuilder();
        var positions = new List<(int Cell, uint Code)>();
        var rows = Size.Rows;
        for (var row = 0; row < rows && text.Length < 16000;)
        {
            var count = (int)ReadLinkLine(terminal.Handle, info[4] + info[2] / 2, info[5] + (row + 0.5) * info[3], scalars, cells, (nuint)scalars.Length, info);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var value = char.ConvertFromUtf32((int)scalars[i]);
                text.Append(value);
                // A grapheme's later scalars share its cell; the renderer compares the cell's base codepoint.
                var code = i > 0 && cells[i - 1] == cells[i] ? positions[^1].Code : scalars[i];
                foreach (var unused in value) positions.Add(((int)cells[i], code));
            }
            text.Append('\n'); positions.Add((-1, 0));
            row = (int)cells[count - 1] / (int)info[0] + 1;
        }
        var columns = (int)info[0];
        foreach (var link in highlights.Update(text.ToString()))
            foreach (var (cell, code) in positions.Skip(link.Start).Take(link.Length).Where(mark => mark.Cell >= 0).Distinct())
            {
                highlightMarks.TryAdd(cell, code);
                highlightTargets.Add((new Rect(info[4] + cell % columns * info[2], info[5] + cell / columns * info[3], info[2], info[3]), link));
            }
        PushUnderlines();
    }

    /// <summary>The centre of a viewport cell in view coordinates, for checks.</summary>
    internal Point? CellCenter(int column, int row)
    {
        var info = new double[6];
        if (disposed || ReadLinkLine(terminal.Handle, Bounds.Width / 2, Bounds.Height / 2, new uint[16384], new uint[16384], 16384, info) == 0 || info[2] <= 0) return null;
        return new(info[4] + (column + .5) * info[2], info[5] + (row + .5) * info[3]);
    }

    private TerminalLink? HighlightAt(Point point)
    {
        RefreshHighlights();
        return highlightTargets.FirstOrDefault(target => target.Cell.Contains(point)).Link;
    }

    private TerminalLink? pressedFile;
    private Point filePressPoint;
    private KeyModifiers linkModifiers;
    private static KeyModifiers LinkModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Resolves file references off the dispatcher, on the terminal's host.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? ResolveFileLinks
    { get => FileHover.Resolve; set => FileHover.Resolve = value; }
    /// <summary>Opens a resolved file in the embedding application's editor.</summary>
    public Func<TerminalLink, Task>? OpenFileLink
    { get => FileHover.Open; set => FileHover.Open = value; }

    [DllImport("GhosttyAvaloniaView", EntryPoint = "gav_texture_set_link_underlines")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetLinkUnderlines(nint view, uint[] cells, uint[] codepoints, nuint count);

    [DllImport("GhosttyAvaloniaView", EntryPoint = "gav_texture_link_line")]
    private static extern nuint ReadLinkLine(nint view, double x, double y, [Out] uint[] text, [Out] uint[] cells, nuint capacity, [Out] double[] info);

    private void RefreshFileLink()
    {
        hoverMarks.Clear();
        if (!disposed) Cursor = new Cursor(Native.OverLink(terminal.Handle) ? StandardCursorType.Hand : StandardCursorType.Ibeam);
        if (disposed || ResolveFileLinks is null || linkModifiers != LinkModifier || !new Rect(Bounds.Size).Contains(linkPointer) || Native.OverLink(terminal.Handle))
        { FileHover.Update(null, -1); PushUnderlines(); return; }
        var scalars = new uint[16384];
        var cells = new uint[scalars.Length];
        var info = new double[6];
        var count = (int)ReadLinkLine(terminal.Handle, linkPointer.X, linkPointer.Y, scalars, cells, (nuint)scalars.Length, info);
        if (count == 0) { FileHover.Update(null, -1); PushUnderlines(); return; }
        var text = new StringBuilder();
        var positions = new List<(int Cell, uint Code)>();
        var offset = -1;
        for (var i = 0; i < count; i++)
        {
            if (cells[i] == info[1] && offset < 0) offset = text.Length;
            var value = char.ConvertFromUtf32((int)scalars[i]);
            text.Append(value);
            var code = i > 0 && cells[i - 1] == cells[i] ? positions[^1].Code : scalars[i];
            foreach (var unused in value) positions.Add(((int)cells[i], code));
        }
        FileHover.Update(text.ToString(), offset);
        if (FileHover.Link is not { } link) { PushUnderlines(); return; }
        foreach (var (cell, code) in positions.Skip(link.Start).Take(link.Length).Distinct()) hoverMarks.TryAdd(cell, code);
        PushUnderlines();
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private Task<TerminalContextLink?> ContextFileLink(Point point)
    {
        linkPointer = point;
        var modifiers = linkModifiers;
        linkModifiers = LinkModifier;
        RefreshFileLink();
        linkModifiers = modifiers;
        return FileHover.ResolveContext();
    }

    private async void ShowClipboardMenu(string? url, Task<TerminalContextLink?>? lookup = null)
    {
        ClipboardMenu = new ContextMenu();
        if (url is not null)
        {
            var open = new MenuItem { Header = "Open link", Tag = url };
            open.Click += async (_, _) =>
            {
                try { if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url)); }
                catch (Exception) { /* Opening a link must not replace the terminal renderer. */ }
            };
            ClipboardMenu.Items.Add(open);
            ClipboardMenu.Items.Add(new Separator());
        }
        foreach (var (title, action) in new[] { ("Copy", "copy_to_clipboard"), ("Paste", "paste_from_clipboard"), ("Select All", "select_all") })
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) =>
            {
                if (disposed) return;
                if (action == "paste_from_clipboard") UserInput?.Invoke(this, EventArgs.Empty);
                Native.Action(terminal.Handle, action);
            };
            ClipboardMenu.Items.Add(item);
        }
        var menu = ClipboardMenu;
        menu.Open(this);
        if (url is null && lookup is not null && await lookup is { } resolved && !disposed && ReferenceEquals(ClipboardMenu, menu) && menu.IsOpen)
        {
            var open = new MenuItem { Header = "Open link", Tag = resolved.Link.Path };
            open.Click += async (_, _) => await resolved.Activate();
            menu.Items.Insert(0, new Separator());
            menu.Items.Insert(0, open);
        }
    }

    private async Task OpenFileAsync(TerminalLink link)
    {
        try { await FileHover.Activate(link); }
        catch (Exception) { /* A failed link must not replace a live terminal with the renderer fallback. */ }
    }
}