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
        CheckUrls();
        CheckUrlHover();
        CheckZoomedDensity();
        Osc52Checks.RunSkia();
        CheckView();
        CheckTouchAndSoftKeyboard();
        CheckIncrementalRendering();
        SkiaOutputChecks.Run(root);
        CheckSession(root);
        CheckRendererSetting(root);
        TerminalHostChecks.ReplaySetting(root);
        Console.WriteLine("PASS Skia terminal renderer: libghostty-vt cells, colours, wide/combining text, box drawing, cursor, keyboard and mouse encoding, paste, selection, scrollback, resize, touch and soft-keyboard input, and a host PTY session that survives a renderer restart");
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
                Directory.CreateDirectory(".bench");
                frame.Save(".bench/ghostty-skia.png", PngBitmapEncoderOptions.Default);
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
                E2E.E2eWorkspace.Until(() => view.IsDetached);
                var displaced = view.Backend;
                app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalRenderer_skia"));
                E2E.E2eWorkspace.Settle();
                Require(view.IsDetached && view.Backend == displaced && !other.Detached.IsCompleted,
                    "changing the renderer took a displaced terminal back from another client.");
            }
            finally { Task.Run(async () => await other.DisposeAsync()).GetAwaiter().GetResult(); }
        }
        finally { settings.Close(); }
        Console.WriteLine("PASS terminal renderer Settings: pointer selection persists both choices, preserves shells and leaves displaced clients detached");
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