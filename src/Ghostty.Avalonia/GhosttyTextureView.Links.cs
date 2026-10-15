using System.Runtime.InteropServices;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Ghostty.Avalonia;

public sealed partial class GhosttyTextureView
{
    private TerminalLinkHover? fileHover;
    private TerminalLinkHover FileHover => fileHover ??= new(() => { RefreshFileLink(); InvalidateVisual(); });
    private TerminalLink? pressedFile;
    private Point filePressPoint;
    private KeyModifiers linkModifiers;
    private readonly List<Rect> fileUnderlines = [];
    private static KeyModifiers LinkModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Resolves file references off the dispatcher, on the terminal's host.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? ResolveFileLinks
    { get => FileHover.Resolve; set => FileHover.Resolve = value; }
    /// <summary>Opens a resolved file in the embedding application's editor.</summary>
    public Func<TerminalLink, Task>? OpenFileLink
    { get => FileHover.Open; set => FileHover.Open = value; }

    [DllImport("GhosttyAvaloniaView", EntryPoint = "gav_texture_link_line")]
    private static extern nuint ReadLinkLine(nint view, double x, double y, [Out] uint[] text, [Out] uint[] cells, nuint capacity, [Out] double[] info);

    private void RefreshFileLink()
    {
        fileUnderlines.Clear();
        if (!disposed) Cursor = new Cursor(Native.OverLink(terminal.Handle) ? StandardCursorType.Hand : StandardCursorType.Ibeam);
        if (disposed || ResolveFileLinks is null || linkModifiers != LinkModifier || !new Rect(Bounds.Size).Contains(linkPointer) || Native.OverLink(terminal.Handle))
        { FileHover.Update(null, -1); return; }
        var scalars = new uint[16384];
        var cells = new uint[scalars.Length];
        var info = new double[6];
        var count = (int)ReadLinkLine(terminal.Handle, linkPointer.X, linkPointer.Y, scalars, cells, (nuint)scalars.Length, info);
        if (count == 0) { FileHover.Update(null, -1); return; }
        var text = new StringBuilder();
        var positions = new List<int>();
        var offset = -1;
        for (var i = 0; i < count; i++)
        {
            if (cells[i] == info[1] && offset < 0) offset = text.Length;
            var value = char.ConvertFromUtf32((int)scalars[i]);
            text.Append(value);
            foreach (var unused in value) positions.Add((int)cells[i]);
        }
        FileHover.Update(text.ToString(), offset);
        if (FileHover.Link is not { } link) return;
        var widths = new Dictionary<int, int>();
        for (var i = 0; i + 1 < count; i++)
            if (cells[i + 1] != cells[i]) widths[(int)cells[i]] = cells[i + 1] - cells[i] == 2 ? 2 : 1;
        foreach (var cell in positions.Skip(link.Start).Take(link.Length).Distinct())
        {
            var width = widths.GetValueOrDefault(cell, 1);
            fileUnderlines.Add(new(info[4] + cell % (int)info[0] * info[2], info[5] + (cell / (int)info[0] + 1) * info[3] - 2, info[2] * width, 1));
        }
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
            item.Click += (_, _) => { if (!disposed) Native.Action(terminal.Handle, action); };
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