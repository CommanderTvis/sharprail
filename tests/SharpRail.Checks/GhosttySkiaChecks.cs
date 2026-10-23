using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Ghostty.Avalonia;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

// The Skia renderer: libghostty-vt state drawn by Avalonia's Skia, with real headless input and pixels.
internal static class GhosttySkiaChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Skia terminal: " + message); }

    private static void Pump()
    { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }

    private static void Until(Func<bool> condition, string description)
    {
        var deadline = Awake.Now.AddSeconds(20);
        while (!condition())
        {
            if (Awake.Now > deadline) throw new InvalidOperationException("Skia terminal condition timed out: " + description);
            Pump();
            Thread.Sleep(10);
        }
    }

    private static void Press(Window window, Key key, PhysicalKey physical, string? symbol, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, symbol);
        window.KeyRelease(key, modifiers, physical, symbol);
        Pump();
    }

    internal static void Run(string root)
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP Skia terminal renderer (libghostty-vt is built on macOS)"); return; }
        CheckClusters();
        CheckPlaceholderCells();
        CheckUrls();
        CheckUrlHover();
        CheckLinkContextMenu();
        CheckHighlightedLinkContextMenu();
        CheckFileLinks();
        CheckHyperlinks();
        CheckPointerShapes();
        CheckZoomedDensity();
        Osc52Checks.RunSkia();
        CheckView();
        CheckTouchAndSoftKeyboard();
        CheckIncrementalRendering();
        SkiaOutputChecks.Run(root);
        CheckSession(root);
        CheckWatching(root);
        CheckRendererSetting(root);
        TerminalHostChecks.ReplaySetting(root);
        Console.WriteLine("PASS Skia terminal renderer: libghostty-vt cells, colours, wide/combining text, box drawing, cursor, keyboard and mouse encoding, paste, selection, scrollback, resize, touch and soft-keyboard input, and a host PTY session that survives a renderer restart");
    }

    // An agent's empty prompt draws its hint faint, with its own cursor as an inverse cell on the hint's first glyph. The
    // viewport read blanks both, so only typed text is left for the draft guard. The attributes are the SGR the agents
    // emit (2 faint, 7 inverse), not captured from a live session.
    private static void CheckPlaceholderCells()
    {
        const string Faint = "\e[2m", Inverse = "\e[7m", Reset = "\e[0m";
        string Screen(string content)
        {
            using var view = new GhosttySkiaView();
            view.Write(Encoding.UTF8.GetBytes(content));
            return view.ReadViewport(omitFaint: true);
        }
        string[] Lines(string screen) => screen.Split('\n');
        string? Claude(string content) => SharpRail.Plugins.ClaudeCode.UI.ModelPicker.ComposerDraft(Lines(Screen(content)));
        string? Codex(string content) => SharpRail.Plugins.Codex.UI.CodexModelPicker.ComposerDraft(Lines(Screen(content)));

        Require(Claude($"previous output\r\n❯ {Inverse}P{Reset}{Faint}ress up to edit queued messages{Reset}") is null, "a faint placeholder with a drawn cursor is not a draft");
        Require(Claude($"❯ {Faint}Some hint nobody listed{Reset}") is null, "an unlisted faint hint is not a draft");
        Require(Claude($"❯ {Inverse} {Reset}{Faint}Try \"fix the build\"{Reset}") is null, "a space cursor before a faint suggestion is not a draft");
        Require(Claude("❯ fix the build") == "fix the build", "typed text is a draft");
        Require(Claude($"❯ fix the build{Inverse} {Reset}") == "fix the build", "typed text with the cursor after it is a draft");
        Require(Claude($"❯ fix the {Inverse}b{Reset}uild") == "fix the build", "typed text with the cursor inside it is a draft");
        Require(Claude($"❯ fix the build {Faint}(tab to accept a suggestion){Reset}") == "fix the build", "typed text beside a faint suggestion is a draft without the suggestion");
        Require(Claude("❯ first line\r\n  second line") == "first line", "a multi-line draft is a draft");
        Require(Codex($"› {Inverse}A{Reset}{Faint}sk Codex to do anything{Reset}") is null, "Codex's faint placeholder is not a draft");
        Require(Codex($"› {Faint}A hint Codex adds later{Reset}") is null, "an unlisted faint Codex hint is not a draft");
        Require(Codex("› fix the build") == "fix the build", "typed text at Codex's prompt is a draft");
        // Decision: faint text is never counted as typed, whatever it says; agents do not draw typed text faint.
        Require(Claude($"❯ {Faint}typed but dim{Reset}") is null, "text drawn faint is a hint, even if the user typed it");
        using var plain = new GhosttySkiaView();
        plain.Write(Encoding.UTF8.GetBytes($"❯ {Faint}hint{Reset}"));
        Require(plain.ReadViewport(omitFaint: false).Contains("hint", StringComparison.Ordinal), "without omitFaint the hint stays in the read");
    }

    private static void CheckUrls()
    {
        using var vt = new Vt(16, 8, 100, _ => { });
        const string url = "https://example.com/long/path?q=1";
        vt.Write(Encoding.UTF8.GetBytes(url));
        Require(vt.Snapshot(out var frame), "URL snapshot failed.");
        var links = GhosttySkiaView.FindUrls(frame);
        Require(links.Count == url.Length && links.Values.All(value => value == url), "wrapped URL lost cells or changed its target.");
        vt.Resize(10, 8, 8, 16);
        Require(vt.Snapshot(out frame), "reflow snapshot failed.");
        links = GhosttySkiaView.FindUrls(frame);
        Require(links.Count == url.Length && links.Values.All(value => value == url), "resizing broke the wrapped URL.");
        vt.Resize(16, 8, 8, 16);
        vt.Write("\ec"u8);
        vt.Write("https://one.test\r\n/other"u8);
        Require(vt.Snapshot(out frame), "hard-break snapshot failed.");
        links = GhosttySkiaView.FindUrls(frame);
        Require(links.Count == "https://one.test".Length && links.Values.All(value => value == "https://one.test") && !links.ContainsKey(16), "hard line break joined unrelated text into a URL.");
        vt.Resize(80, 8, 8, 16);
        vt.Write("\ec(https://example.com/a_(b))."u8);
        Require(vt.Snapshot(out frame), "punctuation snapshot failed.");
        links = GhosttySkiaView.FindUrls(frame);
        Require(links.Values.All(value => value == "https://example.com/a_(b)") && links.Count == 25, "URL punctuation or balanced parentheses were not preserved.");
    }

    private static void CheckUrlHover()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 240, Height = 160, Content = view };
        window.Show(); Pump();
        try
        {
            view.Focus();
            view.Write("\e[?25lhttps://example.com/a/very/long/wrapped/path"u8); Pump();
            var cw = (view.Bounds.Width - view.Padding.Left - view.Padding.Right) / view.Size.Columns;
            var ch = (view.Bounds.Height - view.Padding.Top - view.Padding.Bottom) / view.Size.Rows;
            var point = new Point(view.Padding.Left + cw * 1.5, view.Padding.Top + ch * 1.5);
            window.MouseMove(point); Pump();
            using var plain = window.CaptureRenderedFrame()!;
            var before = view.RowsRecorded;
            window.KeyPress(Key.LWin, RawInputModifiers.Meta, PhysicalKey.MetaLeft, null); Pump();
            Require(view.Cursor?.ToString() == "Hand", "wrapped continuation did not show the link cursor.");
            Require(view.RowsRecorded >= before + 2, "hover did not repaint all wrapped URL rows.");
            using var underlined = window.CaptureRenderedFrame()!;
            Require(!Pixels(plain).SequenceEqual(Pixels(underlined)), "hover did not visibly underline the URL.");
            window.KeyRelease(Key.LWin, RawInputModifiers.None, PhysicalKey.MetaLeft, null); Pump();
            using var cleared = window.CaptureRenderedFrame()!;
            Require(Pixels(plain).SequenceEqual(Pixels(cleared)), "removing the modifier left stale underlines.");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckLinkContextMenu()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 240, Height = 160, Content = view };
        window.Show(); Pump();
        try
        {
            const string url = "https://example.com/a/very/long/wrapped/path";
            view.Write(Encoding.UTF8.GetBytes("\e[?25l" + url)); Pump();
            var cw = (view.Bounds.Width - view.Padding.Left - view.Padding.Right) / view.Size.Columns;
            var ch = (view.Bounds.Height - view.Padding.Top - view.Padding.Bottom) / view.Size.Rows;
            var point = new Point(view.Padding.Left + cw * 1.5, view.Padding.Top + ch * 1.5);
            void RightClick(RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.MouseDown(point, MouseButton.Right, modifiers); window.MouseUp(point, MouseButton.Right, modifiers); Pump();
            }
            RightClick();
            Require(view.ClipboardMenu?.IsOpen == true, "RMB on a wrapped URL without Command did not open its menu.");
            var open = view.ClipboardMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open link"));
            Require(Equals(open.Tag, url), "Open link changed the soft-wrapped URL target.");
            view.Write("\e[2J\e[Hdifferent output"u8); Pump();
            Require(Equals(open.Tag, url), "Output changed the target captured by the menu.");
            view.ClipboardMenu.Close(); Pump();
            point = new Point(view.Padding.Left + cw * 1.5, view.Padding.Top + ch * .5);
            RightClick();
            Require(view.ClipboardMenu!.Items.OfType<MenuItem>().All(item => !Equals(item.Header, "Open link")), "Plain terminal text offered Open link.");
            view.ClipboardMenu.Close(); Pump();
            view.Write("\e[?1000h\e[?1006h"u8); Pump();
            var output = new List<byte>();
            view.Input += (_, bytes) => output.AddRange(bytes.ToArray());
            RightClick();
            Require(!view.ClipboardMenu.IsOpen && Encoding.UTF8.GetString(output.ToArray()).Contains("\e[<2;", StringComparison.Ordinal),
                "RMB must reach programs tracking the mouse instead of opening a menu.");
            RightClick(RawInputModifiers.Shift);
            Require(view.ClipboardMenu.IsOpen, "Shift must bypass mouse reporting for the terminal menu.");
            view.ClipboardMenu.Close();
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckHighlightedLinkContextMenu()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 240, Height = 160, Content = view };
        var opened = new List<TerminalLink>();
        TerminalLink Link(string text) => new(text.IndexOf("abcdef12", StringComparison.Ordinal), 8, "sharprail-commit:abcdef12", 1, 1, false);
        Task<IReadOnlyList<TerminalLink>> Resolve(string text, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<TerminalLink>>(text.Contains("abcdef12", StringComparison.Ordinal) ? [Link(text)] : []);
        view.ResolveFileLinks = Resolve;
        view.OpenFileLink = link => { opened.Add(link); return Task.CompletedTask; };
        window.Show(); Pump();
        try
        {
            view.Write("\e[?25l\e[?1000h\e[?1006habcdef12 plain"u8); Pump();
            using var plain = window.CaptureRenderedFrame()!;
            view.ResolveHighlightedLinks = Resolve;
            view.Write("\r"u8);
            var output = new List<byte>();
            view.Input += (_, bytes) => output.AddRange(bytes.ToArray());
            var cw = (view.Bounds.Width - view.Padding.Left - view.Padding.Right) / view.Size.Columns;
            var ch = (view.Bounds.Height - view.Padding.Top - view.Padding.Bottom) / view.Size.Rows;
            Until(() =>
            {
                Pump();
                using var frame = window.CaptureRenderedFrame()!;
                return !Pixels(plain).SequenceEqual(Pixels(frame));
            }, "validated hash underline");
            void RightClick(double column)
            {
                var point = new Point(view.Padding.Left + cw * column, view.Padding.Top + ch * .5);
                window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Pump();
            }
            RightClick(2.5);
            Until(() => view.ClipboardMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Open link")) == true,
                "context menu on an underlined hash while the program tracks the mouse");
            Require(!Encoding.UTF8.GetString(output.ToArray()).Contains("\e[<2;", StringComparison.Ordinal), "Right-click on an underlined hash was also reported to the program.");
            view.ClipboardMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open link")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            view.ClipboardMenu.Close();
            Until(() => opened.Count == 1, "hash navigation from the menu");
            output.Clear();
            RightClick(12.5);
            Require(!view.ClipboardMenu.IsOpen && Encoding.UTF8.GetString(output.ToArray()).Contains("\e[<2;", StringComparison.Ordinal),
                "Right-click beside the underlined hash must still reach the tracking program.");
        }
        finally { window.Close(); Pump(); }
    }

    internal static void CheckPersistentHighlights()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 240, Height = 160, Content = view };
        window.Show(); Pump();
        try
        {
            view.Write("\e[?25lcommit abcdef12 and deadbeef"u8); Pump();
            using var plain = window.CaptureRenderedFrame()!;
            var calls = 0;
            view.ResolveHighlightedLinks = (text, token) =>
            {
                Require(!Dispatcher.UIThread.CheckAccess(), "visible-link resolution ran on the dispatcher.");
                Interlocked.Increment(ref calls);
                var start = text.IndexOf("abcdef12", StringComparison.Ordinal);
                return Task.FromResult<IReadOnlyList<TerminalLink>>(start < 0 ? [] : [new(start, 8, "validated-commit", 1, 1, false)]);
            };
            view.Write("\r"u8); Pump();
            Until(() =>
            {
                using var frame = window.CaptureRenderedFrame()!;
                return calls > 0 && !Pixels(plain).SequenceEqual(Pixels(frame));
            }, "persistent link highlight without modifier or pointer");
            using var underlined = window.CaptureRenderedFrame()!;
            Require(!Pixels(plain).SequenceEqual(Pixels(underlined)), "validated commit did not gain visible underlines without hover.");
            var resolvedCalls = calls;
            view.Write("\r"u8); Pump();
            Require(calls == resolvedCalls, "unchanged visible text bypassed the highlight cache.");
            view.ResolveHighlightedLinks = null;
            view.Write("\r"u8); Pump();
            using var cleared = window.CaptureRenderedFrame()!;
            Require(Pixels(plain).SequenceEqual(Pixels(cleared)), "removing resolved highlights left stale pixels.");
            Console.WriteLine("PASS visible terminal links: background lookup, no-modifier pixels, bounded cache reuse and stale underline removal");
        }
        finally { window.Close(); Pump(); }
    }

    internal static void CheckFileLinks()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 240, Height = 160, Content = view };
        var opened = new List<TerminalLink>();
        var calls = 0;
        const string path = "src/a/very/long/path/Main.cs:12:3";
        view.ResolveFileLinks = (text, token) =>
        {
            Interlocked.Increment(ref calls);
            Require(!Dispatcher.UIThread.CheckAccess(), "file lookup ran on the dispatcher.");
            IReadOnlyList<TerminalLink> links = text.TrimEnd() == path ? [new(0, path.Length, "/project/Main.cs", 12, 3, false)] : [];
            return Task.FromResult(links);
        };
        view.OpenFileLink = link => { opened.Add(link); return Task.CompletedTask; };
        window.Show(); Pump();
        try
        {
            view.Focus(); view.Write(Encoding.UTF8.GetBytes("\e[?25l" + path)); Pump();
            var cw = (view.Bounds.Width - view.Padding.Left - view.Padding.Right) / view.Size.Columns;
            var ch = (view.Bounds.Height - view.Padding.Top - view.Padding.Bottom) / view.Size.Rows;
            var point = new Point(view.Padding.Left + cw * 1.5, view.Padding.Top + ch * 1.5);
            window.MouseMove(point); Pump();
            Require(calls == 0, "ordinary pointer movement queried files.");
            window.KeyPress(Key.LWin, RawInputModifiers.Meta, PhysicalKey.MetaLeft, null);
            Until(() => view.Cursor?.ToString() == "Hand", "file link on wrapped continuation");
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.Meta);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.Meta);
            Until(() => opened.Count == 1, "file navigation");
            Require(opened[0].Line == 12 && opened[0].Column == 3, "file click lost source coordinates.");
            window.KeyRelease(Key.LWin, RawInputModifiers.None, PhysicalKey.MetaLeft, null); Pump();
            Require(view.Cursor?.ToString() != "Hand", "file hover survived modifier release.");
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            Until(() => view.ClipboardMenu?.IsOpen == true, "terminal context menu");
            var openedWith = MenuFillProbe.Snapshot(view.ClipboardMenu!.Items);
            Until(() => view.ClipboardMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Open link")) == true, "resolved target context menu without modifier");
            Require(openedWith == MenuFillProbe.Snapshot(view.ClipboardMenu!.Items),
                $"The terminal context menu opened with [{openedWith}] and its file link was added afterwards, so its native popup clipped it.");
            var contextOpen = view.ClipboardMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open link"));
            view.ClipboardMenu.Close(); Pump();
            contextOpen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Until(() => opened.Count == 2, "captured context target navigation after menu closes");
            Require(opened[1] == opened[0], "Context-menu navigation changed the resolved target.");
            view.Write("\r\e[2Kmissing.cs"u8); Pump();
            window.KeyPress(Key.LWin, RawInputModifiers.Meta, PhysicalKey.MetaLeft, null); Pump();
            Require(view.Cursor?.ToString() != "Hand", "changed output kept stale file link.");
        }
        finally { window.Close(); Pump(); }
    }

    // OSC 8 hyperlinks, read from libghostty-vt's cells: their whole range (wrapped too) underlines under Command-hover,
    // the target shows as the tooltip, and Command-click routes as Metal does: file URLs to the editor through the
    // resolver, never the system launcher; web URLs to the launcher. Text-detected URLs keep working beside them.
    internal static void CheckHyperlinks()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 240, Height = 160, Content = view };
        var launched = new List<Uri>();
        var previousLaunch = TerminalLinkLauncher.Launch;
        TerminalLinkLauncher.Launch = (_, uri) => { launched.Add(uri); return Task.CompletedTask; };
        var opened = new List<TerminalLink>();
        var resolved = new List<string>();
        view.ResolveFileLinks = (text, _) =>
        {
            lock (resolved) resolved.Add(text);
            return Task.FromResult<IReadOnlyList<TerminalLink>>(text == "file:///project/Main.cs" ? [new(0, text.Length, "/project/Main.cs", 1, 1, false)] : []);
        };
        view.OpenFileLink = link => { opened.Add(link); return Task.CompletedTask; };
        window.Show(); Pump();
        try
        {
            var columns = view.Size.Columns;
            var label = new string('w', columns + 4);
            view.Focus();
            view.Write(Encoding.UTF8.GetBytes("\e[?25l\e[H\e]8;;https://example.com/docs\e\\Docs label\e]8;;\e\\ https://p.io/x\r\n" +
                "\e]8;;file://some-host/project/Main.cs\e\\main file\e]8;;\e\\\r\n\e]8;id=w;https://example.com/wrapped\e\\" + label + "\e]8;;\e\\"));
            Until(() => view.ReadScreen().Contains("main file", StringComparison.Ordinal) && view.ReadScreen().Contains("wwww", StringComparison.Ordinal), "hyperlink output");
            Point Cell(int column, int row) => view.CellCenter(column, row) ?? throw new InvalidOperationException("Skia terminal: no cell geometry.");
            void Hover(int column, int row)
            {
                window.MouseMove(Cell(column, row), RawInputModifiers.Meta);
                window.KeyPress(Key.LWin, RawInputModifiers.Meta, PhysicalKey.MetaLeft, null); Pump();
            }
            void Click(int column, int row)
            {
                Hover(column, row);
                window.MouseDown(Cell(column, row), MouseButton.Left, RawInputModifiers.Meta);
                window.MouseUp(Cell(column, row), MouseButton.Left, RawInputModifiers.Meta);
                window.KeyRelease(Key.LWin, RawInputModifiers.None, PhysicalKey.MetaLeft, null); Pump();
            }
            Hover(3, 0);
            Require(view.HoveredLink == "https://example.com/docs" && view.Cursor?.ToString() == "Hand" && Equals(ToolTip.GetTip(view), "https://example.com/docs"),
                $"an OSC 8 label did not hover its target: {view.HoveredLink}, {view.Cursor}");
            Require(view.LinkCells.SequenceEqual(Enumerable.Range(0, "Docs label".Length)), "the OSC 8 underline range was not exactly its label: " + string.Join(",", view.LinkCells));
            Hover(columns - 1, 2);
            Require(view.HoveredLink == "https://example.com/wrapped" && view.LinkCells.SequenceEqual(Enumerable.Range(2 * columns, label.Length)),
                $"a soft-wrapped OSC 8 link did not underline across both rows ({view.HoveredLink}, {columns}x{view.Size.Rows}): " + string.Join(",", view.LinkCells) + " screen: " + view.ReadScreen());
            Hover("Docs label ".Length + 2, 0);
            Require(view.HoveredLink == "https://p.io/x", "text-detected URLs stopped working beside OSC 8 links.");
            Click(2, 0);
            Until(() => launched.Count == 1, "OSC 8 web link launch");
            Require(launched[0].AbsoluteUri == "https://example.com/docs", "Command-click opened " + launched[0]);
            Click(2, 1);
            Until(() => opened.Count == 1, "OSC 8 file link navigation");
            Require(launched.Count == 1 && opened[0].Path == "/project/Main.cs",
                "an OSC 8 file link must open in the application, never the launcher; resolved: " + string.Join(", ", resolved));
            window.MouseMove(new Point(1, 1)); Pump();
            Require(view.HoveredLink is null && ToolTip.GetTip(view) is null, "the link tooltip outlived the hover.");
            Console.WriteLine("PASS Skia OSC 8 hyperlinks: exact and wrapped ranges, tooltip target, Command-click to the editor (file) and the browser (web), beside text-detected URLs");
        }
        finally { TerminalLinkLauncher.Launch = previousLaunch; window.Close(); Pump(); }
    }

    // OSC 22 asks for a pointer shape; the view maps Ghostty's names to Avalonia cursors, and a hovered link still shows a hand.
    internal static void CheckPointerShapes()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 240, Height = 160, Content = view };
        window.Show(); Pump();
        try
        {
            Require(view.PointerShape == StandardCursorType.Ibeam, "the terminal pointer did not start as an I-beam.");
            foreach (var (name, expected) in new[] { ("pointer", StandardCursorType.Hand), ("wait", StandardCursorType.Wait), ("crosshair", StandardCursorType.Cross),
                ("ew-resize", StandardCursorType.SizeWestEast), ("not-allowed", StandardCursorType.No), ("default", StandardCursorType.Arrow), ("text", StandardCursorType.Ibeam) })
            {
                view.Write(Encoding.UTF8.GetBytes($"\e]22;{name}\e\\")); Pump();
                Require(view.PointerShape == expected && view.Cursor?.ToString() == expected.ToString(), $"OSC 22 {name} showed {view.PointerShape}, not {expected}.");
            }
            Console.WriteLine("PASS Skia OSC 22 pointer shapes map to Avalonia cursors");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckZoomedDensity()
    {
        static List<Color> Render(double fontSize, double zoom)
        {
            using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = fontSize, Padding = default };
            var window = new Window
            {
                Width = 320,
                Height = 160,
                Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(zoom, zoom), Child = view },
            };
            window.Show(); Pump();
            try
            {
                view.Write("\e[?25lZoom ┼ é"u8); Pump();
                using var frame = window.CaptureRenderedFrame()!;
                return Pixels(frame);
            }
            finally { window.Close(); Pump(); }
        }
        // Hinting at 14pt×2 and 28pt moves a few glyph-edge pixels; resampling a 1× framebuffer changes most of the ink.
        var (zoomed, plain) = (Render(14, 2), Render(28, 1));
        var ink = plain.Count(color => color != plain[0]);
        Require(zoomed.Count == plain.Count && zoomed.Zip(plain).Count(pair => pair.First != pair.Second) < ink / 10,
            "a terminal under a zoomed ancestor must rasterize cells at the displayed density instead of resampling them.");
    }

    private static void CheckClusters()
    {
        var replies = new List<byte>();
        using var vt = new Vt(80, 24, 100, bytes => replies.AddRange(bytes.ToArray()));
        foreach (var reset in new[] { "", "\ec" })
        {
            replies.Clear();
            vt.Write(Encoding.UTF8.GetBytes(reset + "é👍🏽\e[6n"));
            Require(Encoding.UTF8.GetString([.. replies]) == "\e[1;4R",
                "combining marks and emoji modifiers must stay with their base cell, including after terminal reset.");
        }
    }

    private static void CheckIncrementalRendering()
    {
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        var window = new Window { Width = 640, Height = 320, Content = view };
        window.Show(); Pump();
        try
        {
            view.Write("\e[?25l\e[Hfirst unchanged\r\nsecond row\r\nthird unchanged"u8); Pump();
            var recorded = view.RowsRecorded;
            view.Write("\e[2;1Hshort\e[K"u8); Pump();
            Require(view.RowsRecorded == recorded + 1, "a one-row edit rebuilt unchanged rows.");
            using var incremental = window.CaptureRenderedFrame()!;
            view.Typeface = view.Typeface; Pump();
            using var rebuilt = window.CaptureRenderedFrame()!;
            Require(Pixels(incremental).SequenceEqual(Pixels(rebuilt)), "incremental erasure differs from a complete repaint.");

            view.Write("\e[1;1H\e[?25h\e[2 q"u8); Pump();
            recorded = view.RowsRecorded;
            view.Write("\e[3;2H"u8); Pump();
            Require(view.RowsRecorded == recorded + 2, "moving the cursor must redraw only its old and new rows.");
            using var moved = window.CaptureRenderedFrame()!;
            view.Typeface = view.Typeface; Pump();
            using var cursorRebuilt = window.CaptureRenderedFrame()!;
            Require(Pixels(moved).SequenceEqual(Pixels(cursorRebuilt)), "cursor movement left stale pixels.");

            view.Write("\e[?25l\e[2;1H漢é\e[K"u8); Pump();
            recorded = view.RowsRecorded;
            view.Write("\e[2;1H\e[2K"u8); Pump();
            Require(view.RowsRecorded == recorded + 1, "erasing wide/combining text rebuilt unrelated rows.");
            using var erased = window.CaptureRenderedFrame()!;
            view.Typeface = view.Typeface; Pump();
            using var erasedRebuilt = window.CaptureRenderedFrame()!;
            Require(Pixels(erased).SequenceEqual(Pixels(erasedRebuilt)), "wide/combining text left stale pixels.");
        }
        finally { window.Close(); }
    }

    private static void CheckView()
    {
        var input = new List<byte>();
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14 };
        view.Input += (_, data) => input.AddRange(data.ToArray());
        var sizes = new List<TerminalSize>();
        view.GridResized += (_, size) => sizes.Add(size);
        var bubbled = new List<Key>();
        var window = new Window { Width = 640, Height = 320, Content = view };
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => bubbled.Add(e.Key));
        window.Show(); Pump(); view.Focus(); Pump();
        string Sent() { var text = Encoding.UTF8.GetString([.. input]); input.Clear(); return text; }
        try
        {
            Require(sizes.Count > 0 && view.Size.Columns > 60 && view.Size.Rows > 10, $"the grid did not fit the control ({view.Size}).");
            view.Write("\e[1;31mred\e[0m plain ╭─╮ 漢字 é 👍🏽\r\n"u8);
            view.Write("\e[48;2;0;0;255m  blue  \e[0m\r\n"u8);
            Pump();
            var screen = view.ReadScreen();
            Require(screen.Contains("red plain ╭─╮ 漢字 é 👍🏽", StringComparison.Ordinal), "the screen text was not kept: " + screen);
            using (var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Skia terminal did not render."))
            {
                frame.Save(CheckRoot.Artifact("ghostty-skia.png"), PngBitmapEncoderOptions.Default);
                var pixels = Pixels(frame);
                Require(pixels.Any(p => p.R > 0xb0 && p.G < 0x80 && p.B < 0x80), "SGR red text drew no red pixels.");
                Require(pixels.Count(p => p == Color.FromRgb(0, 0, 255)) > 100, "the 24-bit background drew no blue cells.");
                Require(pixels.Distinct().Count() > 8, "the frame is not varied enough to contain text.");
            }

            window.KeyTextInput("ls -l"); Pump();
            Require(Sent() == "ls -l", "typed text did not reach the program.");
            Press(window, Key.Enter, PhysicalKey.Enter, "\r");
            Require(Sent() == "\r", "Return did not send a carriage return.");
            Press(window, Key.C, PhysicalKey.C, "c", RawInputModifiers.Control);
            Require(Sent() == "\u0003", "Control-C did not send ETX.");
            Press(window, Key.Up, PhysicalKey.ArrowUp, null);
            Require(Sent() == "\e[A", "Up did not send the normal cursor sequence.");
            view.Write("\e[?1h"u8);
            Press(window, Key.Up, PhysicalKey.ArrowUp, null);
            Require(Sent() == "\eOA", "Up ignored application cursor mode.");
            Press(window, Key.B, PhysicalKey.B, "∫", RawInputModifiers.Alt);
            Require(Sent() == "\eb", "Option did not act as Alt.");
            Press(window, Key.Tab, PhysicalKey.Tab, "\t");
            Require(Sent() == "\t" && view.IsKeyboardFocusWithin, "Tab moved focus instead of reaching the program.");
            bubbled.Clear();
            Press(window, Key.J, PhysicalKey.J, "j", RawInputModifiers.Meta | RawInputModifiers.Shift);
            Require(Sent() == "" && bubbled.Contains(Key.J), "Command shortcuts did not reach the application.");
            view.Write("\e[6n"u8);
            Require(Sent().EndsWith('R'), "the cursor position report was not answered.");

            view.Write("\e[?2004h"u8);
            var set = window.Clipboard!.SetTextAsync("one\ntwo");
            while (!set.IsCompleted) Pump();
            Press(window, Key.V, PhysicalKey.V, "v", RawInputModifiers.Meta);
            Until(() => input.Count > 0, "bracketed paste");
            var pasted = Sent();
            Require(pasted.StartsWith("\e[200~", StringComparison.Ordinal) && pasted.EndsWith("\e[201~", StringComparison.Ordinal) && pasted.Contains("two"), "paste was not bracketed: " + pasted);

            // Drag across "red" on the first row.
            var cell = view.Bounds.Width / view.Size.Columns;
            window.MouseDown(new Point(view.Padding.Left + cell * 0.2, 12), MouseButton.Left);
            window.MouseMove(new Point(view.Padding.Left + cell * 2.8, 12));
            window.MouseUp(new Point(view.Padding.Left + cell * 2.8, 12), MouseButton.Left); Pump();
            Require(view.SelectedText == "red", $"dragging selected '{view.SelectedText}'.");
            Press(window, Key.C, PhysicalKey.C, "c", RawInputModifiers.Meta);
            var copied = window.Clipboard!.TryGetTextAsync();
            while (!copied.IsCompleted) Pump();
            Require(copied.Result == "red", "Command-C did not copy the selection.");

            view.Write("\e[?1000h\e[?1006h"u8);
            window.MouseDown(new Point(view.Padding.Left + cell * 4.5, 12), MouseButton.Left);
            window.MouseUp(new Point(view.Padding.Left + cell * 4.5, 12), MouseButton.Left); Pump();
            var mouse = Sent();
            Require(mouse == "\e[<0;5;1M\e[<0;5;1m", "a tracked press and release were not reported in SGR form: " + mouse);
            view.Write("\e[?1000l"u8);

            for (var i = 0; i < 200; i++) view.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));
            Pump();
            Require(view.IsAtBottom, "new output did not stay at the bottom.");
            window.MouseWheel(new Point(200, 100), new Vector(0, 5)); Pump();
            Require(!view.IsAtBottom, "the wheel did not scroll into history.");
            window.KeyTextInput("x"); Pump();
            Require(view.IsAtBottom && Sent() == "x", "typing did not return to the prompt.");

            var frames = view.Frames;
            view.Colors = TerminalColors.Default with { Background = Color.FromRgb(0x10, 0x80, 0x10) };
            Pump();
            using (var frame = window.CaptureRenderedFrame()!)
                Require(Pixels(frame).Count(p => p == Color.FromRgb(0x10, 0x80, 0x10)) > 1000 && view.Frames > frames, "a colour change did not repaint.");

            sizes.Clear();
            window.Width = 400; Pump();
            Require(sizes.Count == 1 && sizes[0].Columns < 60, "shrinking the control did not resize the grid.");
        }
        finally { window.Close(); }
    }

    // What an Android client drives: a finger taps, drags and holds, and Avalonia's input connection replaces what it
    // composes by selecting it, pressing a Delete that has no scan code and typing again.
    private static void CheckTouchAndSoftKeyboard()
    {
        var input = new List<byte>();
        using var view = new GhosttySkiaView { Typeface = EditorChecks.Font, FontSize = 14, InputField = true };
        view.Input += (_, data) => input.AddRange(data.ToArray());
        var window = new Window { Width = 640, Height = 320, Content = view };
        window.Show(); Pump();
        string Sent() { var text = Encoding.UTF8.GetString([.. input]); input.Clear(); return text; }
        void Touch(Point from, Point? to = null, bool held = false)
        {
            var touch = window.TouchBegin(from, RawInputModifiers.None);
            if (held)
            {
                using var wait = new CancellationTokenSource(Application.Current!.PlatformSettings!.HoldWaitDuration + TimeSpan.FromMilliseconds(250));
                Dispatcher.UIThread.MainLoop(wait.Token);
            }
            for (var step = 1; to is { } end && step <= 5; step++) window.TouchMove(touch, from + (end - from) * step / 5, RawInputModifiers.None);
            window.TouchEnd(touch, to ?? from, RawInputModifiers.None); Pump();
        }
        try
        {
            view.Write("alpha beta\r\n"u8); Pump();
            Touch(new Point(300, 100));
            Require(view.IsFocused && view.SelectedText == "" && view.ClipboardMenu is null, "a tap did not just focus the terminal.");
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            view.RaiseEvent(request);
            var ime = request.Client ?? throw new InvalidOperationException("The terminal offered the soft keyboard no input client.");

            window.KeyTextInput("h"); ime.Selection = new(1, 1);
            ime.Selection = new(0, 1);
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyTextInput("he"); ime.Selection = new(2, 2);
            Require(Sent() == "h\u007fhe" && ime.SurroundingText == "he", "replacing composed text did not erase and retype it.");
            window.KeyTextInput("\n");
            Require(Sent() == "\r" && ime.SurroundingText == "", "a committed line feed did not send Return and start a new field.");
            Press(window, Key.Back, PhysicalKey.None, null);
            Press(window, Key.Enter, PhysicalKey.None, null);
            Press(window, Key.Up, PhysicalKey.None, null);
            Require(Sent() == "\u007f\r\e[A", "soft keys without scan codes were not encoded.");

            var bar = new TerminalKeyBar(view);
            var up = bar.FindControl<Button>("UpKey")!;
            var ctrl = bar.FindControl<Avalonia.Controls.Primitives.ToggleButton>("CtrlKey")!;
            Require(bar.GetLogicalDescendants().OfType<Button>().Count() == 12 && !up.Focusable && !ctrl.Focusable,
                "the touch key bar did not offer twelve keys that preserve terminal focus.");
            Require(!bar.IsVisible, "the touch key bar showed without an open soft keyboard.");
            bar.KeyboardOpen = true;
            Require(bar.IsVisible == view.IsFocused, "the touch key bar did not follow terminal focus while the keyboard was open.");
            bar.Measure(new Size(2000, 2000));
            Require(bar.DesiredSize.Height <= 36,
                $"the touch key bar is {bar.DesiredSize.Height} tall, more than one compact row.");
            bar.KeyboardOpen = false;
            Require(!bar.IsVisible, "the touch key bar stayed after the soft keyboard closed.");
            up.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            ctrl.IsChecked = true;
            ctrl.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.KeyTextInput("c");
            Require(ctrl.IsChecked == false && view.IsFocused, "a consumed modifier stayed selected or took terminal focus.");
            window.KeyTextInput("x");
            view.PendingModifiers = KeyModifiers.Alt;
            window.KeyTextInput("b");
            Require(Sent() == "\e[A\u0003x\eb" && view.PendingModifiers == KeyModifiers.None,
                "on-screen keys or one-shot soft-keyboard modifiers were not encoded.");


            var cell = view.Bounds.Width / view.Size.Columns;
            Touch(new Point(view.Padding.Left + cell * 7.5, 12), held: true);
            Require(view.SelectedText == "beta", $"a long press selected '{view.SelectedText}'.");
            Require(view.ClipboardMenu?.IsOpen == true, "lifting a long press did not offer the clipboard.");
            view.ClipboardMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            view.ClipboardMenu.Close(); Pump();
            var copied = window.Clipboard!.TryGetTextAsync();
            while (!copied.IsCompleted) Pump();
            Require(copied.Result == "beta", "the long-press menu did not copy the selection.");

            for (var i = 0; i < 200; i++) view.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));
            Pump();
            Touch(new Point(250, 60), new Point(250, 250));
            Require(!view.IsAtBottom && view.SelectedText == "beta", $"a finger drag did not scroll into history instead of selecting ('{view.SelectedText}').");
        }
        finally { window.Close(); }
    }

    private static void CheckRendererSetting(string root)
    {
        var legacy = new ProfileStore(Path.Combine(root, "legacy-renderer"));
        Require(legacy.Data.Preferences.TerminalRenderer == TerminalRenderers.Texture, "new profiles did not default to texture.");
        legacy.Data.Preferences.TerminalRenderer = "native";
        legacy.Save();
        Require(new ProfileStore(Path.Combine(root, "legacy-renderer")).Data.Preferences.TerminalRenderer == TerminalRenderers.Texture,
            "legacy NSView profiles did not migrate to texture.");
        var directory = Path.Combine(root, "renderer-setting");
        using var app = new E2E.E2eWorkspace(directory, openFiles: false);
        var tab = new DockTab("terminal:renderer", "Renderer check", "terminal");
        app.Window.Layout.Open(tab, keep: true);
        var original = E2E.TerminalsE2E.Ready(app, tab);
        original.Run("RENDERER_CHECK=survived; printf 'RENDERER_%s\\n' $RENDERER_CHECK");
        E2E.TerminalsE2E.Expect(original, "RENDERER_survived");
        var starts = app.Terminals.Started.Count;
        var settings = new SettingsWindow(app.Window, () => { });
        settings.Show(app.Window);
        settings.ShowSection("Terminal");
        try
        {
            foreach (var renderer in new[] { TerminalRenderers.Skia, TerminalRenderers.Texture })
            {
                var previous = E2E.TerminalsE2E.View(app, tab).Backend;
                app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalRenderer_" + renderer));
                E2E.E2eWorkspace.Until(() => E2E.TerminalsE2E.View(app, tab).Backend != previous);
                var current = E2E.TerminalsE2E.Ready(app, tab);
                Require(new ProfileStore(directory + "-profile").Data.Preferences.TerminalRenderer == renderer,
                    "the Settings renderer choice was not persisted.");
                current.Run("printf 'STILL_%s\\n' $RENDERER_CHECK");
                E2E.TerminalsE2E.Expect(current, "STILL_survived");
                Require(app.Terminals.Started.Count == starts, "changing the Settings renderer started a new shell.");
            }
            var view = E2E.TerminalsE2E.View(app, tab);
            var other = Task.Run(async () => await app.Terminals.AttachAsync(new(view.SessionId, directory, "renderer-other-client", 80, 24))).GetAwaiter().GetResult();
            try
            {
                E2E.E2eWorkspace.Until(() => view.IsWatching);
                var displaced = view.Backend;
                app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalRenderer_skia"));
                E2E.E2eWorkspace.Settle();
                Require(view.IsWatching && view.Backend == displaced && !other.Detached.IsCompleted,
                    "changing the renderer took a displaced terminal back from another client.");
            }
            finally { Task.Run(async () => await other.DisposeAsync()).GetAwaiter().GetResult(); }
        }
        finally { settings.Close(); }
        Console.WriteLine("PASS terminal renderer Settings: pointer selection persists both choices, preserves shells and leaves displaced clients watching");
    }

    // A real host shell behind the Skia backend: output, input and grid size, then a restart keeps the shell.
    private static void CheckSession(string root)
    {
        var pty = new PtyTerminalService();
        var factory = TerminalBackends.Ghostty(new LocalTerminalAdapter(pty), () => TerminalRenderers.Skia);
        var launch = new TerminalLaunch(root, "skia-" + Guid.NewGuid().ToString("N"), Path.Combine(root, ".clipboard"), "skia-check");
        var terminal = new TerminalView(factory, launch);
        var window = new Window { Width = 700, Height = 360, Content = terminal };
        window.Show(); Pump();
        try
        {
            GhosttySkiaView View() => terminal.Backend!.View.GetVisualDescendants().OfType<GhosttySkiaView>().Single();
            Until(() => terminal.Backend?.Started.IsCompletedSuccessfully == true, "the shell attached");
            var first = terminal.Backend!;
            first.FocusTerminal(); Pump();
            window.KeyTextInput("printf 'SKIA_%s_%s_\\n' $((6*7)) \"$(stty size | tr ' ' x)\"; echo PID_$$"); Pump();
            Press(window, Key.Enter, PhysicalKey.Enter, "\r");
            var size = View().Size;
            Until(() => View().ReadScreen().Contains($"SKIA_42_{size.Rows}x{size.Columns}_", StringComparison.Ordinal), "shell output at the grid's size");
            var pid = PidOf(View().ReadScreen());

            terminal.Restart(); Pump();
            Until(() => terminal.Backend is { } next && next != first && next.Started.IsCompletedSuccessfully, "the restarted renderer attached");
            var second = terminal.Backend!;
            Require(View().ReadScreen().Contains("SKIA_42_", StringComparison.Ordinal), "the restarted view did not replay the screen.");
            second.FocusTerminal(); Pump();
            window.KeyTextInput("echo PID_$$; exit 7"); Pump();
            Press(window, Key.Enter, PhysicalKey.Enter, "\r");
            Until(() => terminal.IsExited, "the shell exit");
            Require(PidOf(View().ReadScreen()) == pid, "restarting the renderer started another shell.");
            Require(second.Exited.Result == 7, "the exit status was lost.");
        }
        finally
        {
            window.Close();
            terminal.Close();
            Task.Run(async () => await pty.DisposeAsync()).GetAwaiter().GetResult();
        }

        static string PidOf(string screen) =>
            screen.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.StartsWith("PID_", StringComparison.Ordinal) && line.Length > 4 && char.IsDigit(line[4]))
            ?? throw new InvalidOperationException("Skia terminal: no shell PID on screen: " + screen);
    }

    // The Skia view of a terminal another client holds: the holder's grid whatever the pane's size, its live
    // output, and nothing sent back, neither keys nor the reply to a terminal query; Enter on its offer takes over.
    private static void CheckWatching(string root)
    {
        var pty = new PtyTerminalService("/bin/sh");
        var terminals = new LocalTerminalAdapter(pty);
        var id = "skia-watch-" + Guid.NewGuid().ToString("N");
        var holder = Task.Run(async () => await terminals.AttachAsync(new(id, root, "skia-holder", 132, 43))).GetAwaiter().GetResult();
        var held = new StringBuilder();
        _ = Task.Run(async () => { await foreach (var chunk in holder.ReadAsync()) lock (held) held.Append(Encoding.UTF8.GetString(chunk.Span)); });
        void Run(string command) => Task.Run(async () => await holder.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"))).GetAwaiter().GetResult();
        var factory = TerminalBackends.Ghostty(terminals, () => TerminalRenderers.Skia);
        var terminal = new TerminalView(launch =>
        {
            var backend = factory(launch);
            // Reproduce Android's key-bar container before the watch attachment completes.
            var border = (Border)backend.View;
            var surface = border.Child!;
            border.Child = null;
            var panel = new Avalonia.Controls.DockPanel();
            panel.Children.Add(new TerminalKeyBar((GhosttySkiaView)surface));
            panel.Children.Add(surface);
            border.Child = panel;
            return backend;
        },
            new TerminalLaunch(root, id, Path.Combine(root, ".clipboard"), "skia-watcher") { Yield = true });
        var window = new Window { Width = 420, Height = 300, Content = terminal };
        window.Show(); Pump();
        try
        {
            GhosttySkiaView View() => terminal.Backend!.View.GetVisualDescendants().OfType<GhosttySkiaView>().Single();
            Until(() => terminal.IsWatching, "the watching view attached");
            Until(() => View().Size == new TerminalSize(132, 43), "the holder's grid on the watching view");
            var scroller = terminal.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.Name == "TerminalWatched");
            Require(View().Bounds.Width > scroller.Viewport.Width && scroller.Extent.Width >= View().Bounds.Width && !View().Focusable && !View().IsHitTestVisible,
                "a watched terminal larger than its pane must keep the holder's grid, scroll, and take no input.");
            Run("printf 'WATCH_%s_\\n' $((6*7)); printf '\\033[6n'");
            Until(() => View().ReadScreen().Contains("WATCH_42_", StringComparison.Ordinal), "live output on the watching view");
            terminal.FocusTerminal(); Pump();
            var offer = terminal.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalTakeBack");
            Require(offer.IsFocused, "a watched terminal's keyboard focus must be its offer.");
            window.KeyTextInput("echo GHOST_$((40+2))"); Pump();
            Task.Run(async () => await holder.ResizeAsync(100, 20)).GetAwaiter().GetResult();
            Until(() => View().Size == new TerminalSize(100, 20), "the holder's resize on the watching view");
            Run("printf 'AFTER_%s_\\n' \"$(stty size | tr ' ' x)\"");
            Until(() => View().ReadScreen().Contains("AFTER_20x100_", StringComparison.Ordinal), "the holder's size after watching");
            string Held() { lock (held) return held.ToString(); }
            Require(!holder.Detached.IsCompleted && !Held().Contains("GHOST", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(Held(), @"\[\d+;\d+R"),
                "a watching view typed into, resized or answered a query of the terminal it watches: " + Held());

            Press(window, Key.Enter, PhysicalKey.Enter, "\r");
            Until(() => terminal is { IsWatching: false, Backend: { Watching: false, Started.IsCompletedSuccessfully: true } }, "the take-over");
            Until(() => holder.Detached.IsCompleted, "the previous holder's detach");
            var size = View().Size;
            Require(size != new TerminalSize(100, 20) && View().IsFocused, "after a take-over the view must fit its own pane and take the keyboard.");
            window.KeyTextInput("printf 'TOOK_%s_\\n' \"$(stty size | tr ' ' x)\""); Pump();
            Press(window, Key.Enter, PhysicalKey.Enter, "\r");
            Until(() => View().ReadScreen().Contains($"TOOK_{size.Rows}x{size.Columns}_", StringComparison.Ordinal), "the shell at the new holder's grid");
            Console.WriteLine("PASS Skia watching: a held terminal shows live at its holder's grid in a scrolling pane, follows its resize, sends no keys or query replies, and Enter on the offer takes it over at the pane's size");
        }
        finally
        {
            window.Close();
            terminal.Close();
            Task.Run(async () => { await holder.DisposeAsync(); await pty.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }

    private static List<Color> Pixels(Bitmap frame)
    {
        var size = frame.PixelSize;
        var bytes = new byte[size.Width * size.Height * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), bytes.Length, size.Width * 4); }
        finally { pinned.Free(); }
        var bgra = frame.Format == Avalonia.Platform.PixelFormats.Bgra8888;
        var colors = new List<Color>(size.Width * size.Height);
        for (var i = 0; i < bytes.Length; i += 4)
            colors.Add(bgra ? Color.FromRgb(bytes[i + 2], bytes[i + 1], bytes[i]) : Color.FromRgb(bytes[i], bytes[i + 1], bytes[i + 2]));
        return colors;
    }
}