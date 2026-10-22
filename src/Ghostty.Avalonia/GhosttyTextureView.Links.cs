using System.Runtime.InteropServices;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    // Cells to underline, by viewport index (row * columns + column) with the base codepoint each showed. Visible-link
    // cells go to the renderer attached to their line's text, so it keeps underlining them in every frame showing that
    // line; hover cells are a transient set in screen coordinates. A cell whose text changed is dropped in that frame.
    private readonly Dictionary<int, uint> highlightMarks = [];
    private readonly Dictionary<int, uint> hoverMarks = [];
    private readonly List<(Rect Cell, TerminalLink Link)> highlightTargets = [];
    private int[] pushedCells = [];
    private int[] pushedHighlights = [];
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

    // Line reads reuse these UI-thread buffers: highlights refresh while output streams.
    private readonly uint[] linkScalars = new uint[16384], linkCells = new uint[16384];
    private readonly double[] linkInfo = new double[6];
    private readonly StringBuilder linkText = new();
    private readonly List<(int Cell, uint Code)> linkPositions = [];

    // One UTF-16 position per char of the scalar, each with its cell and the cell's base codepoint.
    private static void AppendScalar(StringBuilder text, List<(int Cell, uint Code)> positions, uint[] scalars, uint[] cells, int i)
    {
        var value = Rune.IsValid((int)scalars[i]) ? new Rune(scalars[i]) : Rune.ReplacementChar;
        Span<char> chars = stackalloc char[2];
        var length = value.EncodeToUtf16(chars);
        text.Append(chars[..length]);
        var code = i > 0 && cells[i - 1] == cells[i] ? positions[^1].Code : scalars[i];
        for (var unit = 0; unit < length; unit++) positions.Add(((int)cells[i], code));
    }

    private void ClearHighlights()
    {
        highlightMarks.Clear();
        highlightTargets.Clear();
        pushedHighlights = [];
        if (!disposed) SetLineLinks(terminal.Handle, [], [], 0, 2);
    }

    // A settled resolution of the visible text replaces every visible line's links; otherwise only lines resolved
    // earlier are repeated, so nothing already underlined is dropped while a resolution is pending.
    private void PushHighlights(bool settled)
    {
        if (disposed) return;
        var cells = highlightMarks.Keys.Order().ToArray();
        if (!settled && cells.SequenceEqual(pushedHighlights)) return;
        var codepoints = cells.Select(cell => highlightMarks[cell]).ToArray();
        if (SetLineLinks(terminal.Handle, cells.Select(cell => (uint)cell).ToArray(), codepoints, (nuint)cells.Length, settled ? 1u : 0u)) pushedHighlights = cells;
    }

    private void PushUnderlines()
    {
        if (disposed) return;
        var cells = hoverMarks.Keys.Order().ToArray();
        if (cells.SequenceEqual(pushedCells)) return;
        var codepoints = cells.Select(cell => hoverMarks[cell]).ToArray();
        if (SetLinkUnderlines(terminal.Handle, cells.Select(cell => (uint)cell).ToArray(), codepoints, (nuint)cells.Length)) pushedCells = cells;
    }

    private void RefreshHighlights()
    {
        highlightMarks.Clear();
        highlightTargets.Clear();
        if (!active || disposed || highlights?.Resolve is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var (scalars, cells, info) = (linkScalars, linkCells, linkInfo);
        if (ReadLinkLine(terminal.Handle, Bounds.Width / 2, Bounds.Height / 2, scalars, cells, (nuint)scalars.Length, info) == 0 || info[2] <= 0 || info[3] <= 0)
            return;
        var (text, positions) = (linkText.Clear(), linkPositions);
        positions.Clear();
        var rows = Size.Rows;
        var row = 0;
        for (; row < rows && text.Length < 16000;)
        {
            var count = (int)ReadLinkLine(terminal.Handle, info[4] + info[2] / 2, info[5] + (row + 0.5) * info[3], scalars, cells, (nuint)scalars.Length, info);
            if (count == 0) break;
            // A grapheme's later scalars share its cell; the renderer compares the cell's base codepoint.
            for (var i = 0; i < count; i++) AppendScalar(text, positions, scalars, cells, i);
            text.Append('\n'); positions.Add((-1, 0));
            row = (int)cells[count - 1] / (int)info[0] + 1;
        }
        var columns = (int)info[0];
        var resolved = highlights.Update(text.ToString());
        foreach (var link in resolved)
            foreach (var (cell, code) in positions.Skip(link.Start).Take(link.Length).Where(mark => mark.Cell >= 0).Distinct())
            {
                highlightMarks.TryAdd(cell, code);
                highlightTargets.Add((new Rect(info[4] + cell % columns * info[2], info[5] + cell / columns * info[3], info[2], info[3]), link));
            }
        // A resolution of text cut short says nothing about the lines after the cut.
        PushHighlights(highlights.Settled && row >= rows);
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
    private TerminalLink? pressedHighlight;
    private Point filePressPoint;
    private KeyModifiers linkModifiers;
    private static KeyModifiers LinkModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Resolves file references off the dispatcher, on the terminal's host.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? ResolveFileLinks
    { get => FileHover.Resolve; set => FileHover.Resolve = value; }
    /// <summary>Opens a resolved file in the embedding application's editor.</summary>
    public Func<TerminalLink, Task>? OpenFileLink
    { get => FileHover.Open; set => FileHover.Open = value; }
    /// <summary>Adds the embedding application's items to the context menu just before it opens, below the clipboard items.</summary>
    public Action<ContextMenu>? ExtendContextMenu { get; set; }

    [DllImport("GhosttyAvaloniaView", EntryPoint = "gav_texture_set_link_underlines")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetLinkUnderlines(nint view, uint[] cells, uint[] codepoints, nuint count);

    [DllImport("GhosttyAvaloniaView", EntryPoint = "gav_texture_set_line_links")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetLineLinks(nint view, uint[] cells, uint[] codepoints, nuint count, uint mode);

    [DllImport("GhosttyAvaloniaView", EntryPoint = "gav_texture_link_line")]
    private static extern nuint ReadLinkLine(nint view, double x, double y, [Out] uint[] text, [Out] uint[] cells, nuint capacity, [Out] double[] info);

    private void RefreshFileLink()
    {
        hoverMarks.Clear();
        if (!disposed) UpdatePointer();
        if (disposed || ResolveFileLinks is null || linkModifiers != LinkModifier || !new Rect(Bounds.Size).Contains(linkPointer) || Native.OverLink(terminal.Handle))
        { FileHover.Update(null, -1); PushUnderlines(); return; }
        var (scalars, cells, info) = (linkScalars, linkCells, linkInfo);
        var count = (int)ReadLinkLine(terminal.Handle, linkPointer.X, linkPointer.Y, scalars, cells, (nuint)scalars.Length, info);
        if (count == 0) { FileHover.Update(null, -1); PushUnderlines(); return; }
        var (text, positions) = (linkText.Clear(), linkPositions);
        positions.Clear();
        var offset = -1;
        for (var i = 0; i < count; i++)
        {
            if (cells[i] == info[1] && offset < 0) offset = text.Length;
            AppendScalar(text, positions, scalars, cells, i);
        }
        FileHover.Update(text.ToString(), offset);
        if (FileHover.Link is not { } link) { PushUnderlines(); return; }
        foreach (var (cell, code) in positions.Skip(link.Start).Take(link.Length).Distinct()) hoverMarks.TryAdd(cell, code);
        PushUnderlines();
        pointerShape = StandardCursorType.Hand;
        if (!ReferenceEquals(Cursor, SharedCursor(StandardCursorType.Hand))) Cursor = SharedCursor(StandardCursorType.Hand);
    }

    // Each Cursor owns a native platform cursor that only its finalizer releases (through a dispatcher post), and the
    // pointer state refreshes on every pointer move and frame: share one cursor per shape and assign only on a change.
    private static readonly Dictionary<StandardCursorType, Cursor> cursors = [];

    private static Cursor SharedCursor(StandardCursorType shape)
    {
        if (!cursors.TryGetValue(shape, out var cursor)) cursors[shape] = cursor = new Cursor(shape);
        return cursor;
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

    /// <summary>The selected text; empty without a selection.</summary>
    public string SelectedText
    {
        get
        {
            if (disposed) return "";
            var length = (int)Native.Selection(terminal.Handle, null, 0);
            if (length == 0) return "";
            var buffer = new byte[length + 1];
            Native.Selection(terminal.Handle, buffer, (nuint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, length);
        }
    }

    /// <summary>The output of the command at a point in the view, or null when the point is not in a command's output.</summary>
    public string? CommandOutputAt(Point point)
    {
        if (disposed) return null;
        var length = (long)Native.CommandOutput(terminal.Handle, point.X, point.Y, false, null, 0);
        if (length < 0) return null;
        var buffer = new byte[length];
        length = Native.CommandOutput(terminal.Handle, point.X, point.Y, false, buffer, (nuint)buffer.Length);
        return length < 0 ? null : Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(length, buffer.Length));
    }

    private async void ShowClipboardMenu(string? url, Task<TerminalContextLink?>? lookup = null, string? output = null)
    {
        // A native popup keeps the size it measured when it opened, so the file-link lookup finishes before the menu
        // opens with its final items; a newer request drops this one.
        var request = ++clipboardMenuRequest;
        var resolved = url is null && lookup is not null ? await lookup : null;
        if (disposed || request != clipboardMenuRequest) return;
        var menu = new ContextMenu();
        if (url is not null)
        {
            var open = new MenuItem { Header = "Open link", Tag = url };
            open.Click += async (_, _) => await OpenUrlAsync(url);
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
        }
        else if (resolved is not null)
        {
            var open = new MenuItem { Header = "Open link", Tag = resolved.Link.Path };
            open.Click += async (_, _) => await resolved.Activate();
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
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
            menu.Items.Add(item);
            if (action == "copy_to_clipboard" && output is not null)
            {
                var copyOutput = new MenuItem { Header = "Copy Output of This Command" };
                copyOutput.Click += async (_, _) =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(output);
                };
                menu.Items.Add(copyOutput);
            }
        }
        ExtendContextMenu?.Invoke(menu);
        if (MenuExtras?.Invoke() is { } extras) foreach (var extra in extras) menu.Items.Add(extra);
        ClipboardMenu = menu;
        menu.Open(this);
    }

    // Ghostty's own links (OSC 8 hyperlinks, URLs) and the menu's Open link: file URLs open in the application.
    private async Task OpenUrlAsync(string url)
    {
        try { await TerminalLinkLauncher.Open(this, url, ResolveFileLinks, OpenFileLink); }
        catch (Exception) { /* Opening a link must not replace the terminal renderer. */ }
    }

    private async Task OpenHighlightAsync(TerminalLink link)
    {
        try { if (!disposed && OpenFileLink is { } open) await open(link); }
        catch (Exception) { /* A failed link must not replace a live terminal with the renderer fallback. */ }
    }

    private async Task OpenFileAsync(TerminalLink link)
    {
        try { await FileHover.Activate(link); }
        catch (Exception) { /* A failed link must not replace a live terminal with the renderer fallback. */ }
    }
}