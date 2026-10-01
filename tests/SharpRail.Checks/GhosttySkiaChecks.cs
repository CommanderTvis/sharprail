using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ghostty.Avalonia;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI.Terminal;
using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

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
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Skia terminal condition timed out: " + description);
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
        CheckView();
        CheckSession(root);
        CheckRendererSetting(root);
        Console.WriteLine("PASS Skia terminal renderer: libghostty-vt cells, colours, wide/combining text, box drawing, cursor, keyboard and mouse encoding, paste, selection, scrollback, resize and a host PTY session that survives a renderer restart");
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
        var factory = TerminalBackends.Ghostty(new LocalTerminalAdapter(pty), () => throw new InvalidOperationException("The Skia renderer needs no relay."), () => TerminalRenderers.Skia);
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
