using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Ghostty.Avalonia;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI.Terminal;

using SkiaSharp;

namespace SharpRail.Checks;

internal static class NativeTextureChecks
{
    private static bool fallbackCheck;
    private static bool skiaCheck;
    private static bool clipboardCheck;
    private static bool directCheck;
    private static bool fileLinkCheck;
    private static bool scrollCheck;
    private static bool terminalLinkCheck;
    private const string Title = "SharpRail Ghostty texture check";

    public static void Run(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        fallbackCheck = args.Contains("--texture-fallback");
        skiaCheck = args.Contains("--native-skia");
        clipboardCheck = args.Contains("--native-osc52");
        directCheck = args.Contains("--native-direct");
        fileLinkCheck = args.Contains("--native-file-links");
        scrollCheck = args.Contains("--native-scroll");
        terminalLinkCheck = args.Contains("--native-terminal-links");
        AppBuilder.Configure<TextureApplication>().UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [fallbackCheck ? AvaloniaNativeRenderingMode.Software : AvaloniaNativeRenderingMode.Metal] })
            .StartWithClassicDesktopLifetime(args);
    }

    private sealed class TextureApplication : Application
    {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new Window { Title = Title, Width = 640, Height = 360, WindowDecorations = WindowDecorations.None };
            desktop.MainWindow = window;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
                    if (terminalLinkCheck) await TerminalFileLinkChecks.RunRenderers(window);
                    else if (scrollCheck) await CheckScrollBoundary(window);
                    else if (fileLinkCheck) await CheckFileLinks(window);
                    else if (directCheck) await CheckSessions(window);
                    else if (clipboardCheck) { Osc52Checks.RunSkia(); await CheckOsc52(window); await CheckSessions(window); }
                    else if (skiaCheck) await CheckSkia(window);
                    else
                    {
                        if (!fallbackCheck) { await CheckScrollBoundary(window); await CheckOsc52(window); await Check(window); }
                        await CheckSessions(window);
                        if (!fallbackCheck) await CheckNativeLibrary(window);
                    }
                    desktop.Shutdown(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
            });
            base.OnFrameworkInitializationCompleted();
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task CheckFileLinks(Window window)
    {
        using var terminal = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), ExternalIo: new GhosttyExternalIo()));
        window.Content = terminal;
        await terminal.Ready;
        var path = "src/" + new string('a', terminal.Size.Columns) + "/界é.cs:12:3";
        var opened = new List<TerminalLink>();
        string? observed = null;
        terminal.ResolveFileLinks = (text, token) =>
        {
            Require(!Dispatcher.UIThread.CheckAccess(), "Metal file lookup ran on the UI thread.");
            observed = text.TrimEnd();
            IReadOnlyList<TerminalLink> links = observed == "界 " + path ? [new(2, path.Length, "/project/界é.cs", 12, 3, false)] : [];
            return Task.FromResult(links);
        };
        terminal.OpenFileLink = link => { opened.Add(link); return Task.CompletedTask; };
        terminal.WriteOutput(System.Text.Encoding.UTF8.GetBytes("\e[2J\e[H\e[?25l界 " + path));
        await Task.Delay(150);
        var pointer = new Pointer(91, PointerType.Mouse, true);
        var point = new Point(terminal.Bounds.Width / terminal.Size.Columns * 2.5, terminal.Bounds.Height / terminal.Size.Rows * 1.5);
        void Move(KeyModifiers modifiers) => terminal.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, terminal,
            pointer, terminal, point, 1, new PointerPointProperties(), modifiers));
        Move(KeyModifiers.Meta);
        await Until(() => terminal.Cursor?.ToString() == "Hand", "Metal wrapped file link was not found: " + observed);
        Require(observed == "界 " + path, "Metal changed Unicode text or inserted a newline at a soft wrap: [" + observed + "] expected [界 " + path + "]");
        terminal.RaiseEvent(new PointerPressedEventArgs(terminal, pointer, terminal, point, 2,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton | RawInputModifiers.Meta, PointerUpdateKind.LeftButtonPressed), KeyModifiers.Meta, 1));
        terminal.RaiseEvent(new PointerReleasedEventArgs(terminal, pointer, terminal, point, 3,
            new PointerPointProperties(RawInputModifiers.Meta, PointerUpdateKind.LeftButtonReleased), KeyModifiers.Meta, MouseButton.Left));
        await Until(() => opened.Count == 1, "Metal modifier click did not open the file.");
        Require(opened[0].Line == 12 && opened[0].Column == 3, "Metal lost source coordinates.");
        Move(KeyModifiers.None);
        Require(terminal.Cursor?.ToString() != "Hand", "Metal retained a file link after modifier release.");
        terminal.WriteOutput("\e[2J\e[Hmissing.cs"u8);
        await Task.Delay(150);
        Move(KeyModifiers.Meta);
        await Task.Delay(150);
        Require(terminal.Cursor?.ToString() != "Hand", "Metal kept a stale file link after output changed.");
        terminal.WriteOutput("\e[2J\e[Hhttps://example.com/path"u8);
        await Task.Delay(150);
        point = new Point(point.X, terminal.Bounds.Height / terminal.Size.Rows * .5);
        Move(KeyModifiers.Meta);
        await Until(() => terminal.Cursor?.ToString() == "Hand", "Metal lost ordinary HTTP URL hover.");
        void RightClick(KeyModifiers modifiers = KeyModifiers.None)
        {
            terminal.RaiseEvent(new PointerPressedEventArgs(terminal, pointer, terminal, point, 4,
                new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), modifiers, 1));
            terminal.RaiseEvent(new PointerReleasedEventArgs(terminal, pointer, terminal, point, 5,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased), modifiers, MouseButton.Right));
        }
        RightClick();
        Require(terminal.ClipboardMenu?.IsOpen == true, "Metal RMB did not open the URL menu without Command.");
        var open = terminal.ClipboardMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open link"));
        Require(Equals(open.Tag, "https://example.com/path"), "Metal menu lost the URI target.");
        terminal.WriteOutput("\e[2J\e[H\e]8;;https://example.com/hidden-target\e\\Label\e]8;;\e\\"u8);
        await Task.Delay(150);
        Require(Equals(open.Tag, "https://example.com/path"), "Metal menu target changed after output.");
        terminal.ClipboardMenu.Close();
        RightClick();
        Require(Equals(terminal.ClipboardMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open link")).Tag, "https://example.com/hidden-target"),
            "Metal OSC8 label must open its URI, not its label text.");
        terminal.ClipboardMenu.Close();
        terminal.WriteOutput("\e[2J\e[Hplain\e[?1000h"u8);
        await Task.Delay(150);
        RightClick();
        Require(!terminal.ClipboardMenu.IsOpen, "Metal RMB must stay with mouse-tracking applications.");
        RightClick(KeyModifiers.Shift);
        Require(terminal.ClipboardMenu.IsOpen && terminal.ClipboardMenu.Items.OfType<MenuItem>().All(item => !Equals(item.Header, "Open link")),
            "Shift bypass must offer clipboard actions without a link on plain text.");
        terminal.ClipboardMenu.Close();
        Move(KeyModifiers.None);
        terminal.WriteOutput("\e[2J\e[H\e[?25lcommit abcdef12 and deadbeef"u8);
        await Task.Delay(300);
        var plain = await CaptureHighlights();
        terminal.ResolveHighlightedLinks = (text, token) =>
        {
            Require(!Dispatcher.UIThread.CheckAccess(), "Metal visible-link lookup ran on the dispatcher.");
            var start = text.IndexOf("abcdef12", StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyList<TerminalLink>>(start < 0 ? [] : [new(start, 8, "validated-commit", 1, 1, false)]);
        };
        var highlighted = false;
        for (var attempt = 0; attempt < 30 && !highlighted; attempt++)
        {
            await Task.Delay(100);
            highlighted = !(await CaptureHighlights()).SequenceEqual(plain);
        }
        Require(highlighted, "Metal must underline resolved visible commits without modifier-hover.");
        terminal.ResolveHighlightedLinks = null;
        await Task.Delay(300);
        Require((await CaptureHighlights()).SequenceEqual(plain), "Disabling Metal highlights left stale underlines.");
        window.Content = null;
        Console.WriteLine("PASS native Metal file hover, Unicode, soft wrapping, click coordinates, persistent highlight pixels and stale-link clearing");

        async Task<SKColor[]> CaptureHighlights()
        {
            var directory = CheckRoot.Create("native-commit-highlight");
            var capturePath = Path.GetFullPath(Path.Combine(directory, "frame.png"));
            try
            {
                var capture = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
                foreach (var argument in new[] { "-x", "-o", "-l", Native.WindowNumber(Native.Content(Title)).ToString(), capturePath }) capture.ArgumentList.Add(argument);
                using var process = Process.Start(capture)!;
                await process.WaitForExitAsync();
                Require(process.ExitCode == 0, "Own-window commit highlight capture failed.");
                using var bitmap = SKBitmap.Decode(capturePath);
                return bitmap.Pixels;
            }
            finally { Directory.Delete(directory, true); }
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task CheckOsc52(Window window)
    {
        using var terminal = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), "/bin/sh"));
        window.Content = terminal;
        window.Show();
        var native = Native.Content(Title);
        Native.Activate(native);
        await terminal.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        terminal.FocusTerminal();
        await Until(() => Native.Ready(native) && terminal.IsKeyboardFocusWithin, "OSC 52 terminal did not receive keyboard focus.");
        Osc52Checks.Begin(false);
        try
        {
            var index = 0;
            async Task Output(string sequence)
            {
                var marker = "OSC_DONE_" + ++index;
                // POSIX printf interprets octal escapes; the shell never sees the
                // actual control bytes until it writes them into Ghostty's PTY.
                var octal = string.Concat(System.Text.Encoding.UTF8.GetBytes(sequence).Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
                await Command(native, terminal, "printf '" + octal + "'; printf '\\n" + marker + "\\n'");
                await Until(() => terminal.ReadScreen().Split('\n').Any(line => line.Trim() == marker), "OSC 52 shell output did not complete.");
            }
            foreach (var (text, selector, end) in new[] { ("Grüße\n世界 🐈", "c", "\e\\"), ("BEL", "c", "\a"), ("selection", "s", "\a"), ("primary", "p", "\e\\"), ("empty selector", "", "\a"), ("", "c", "\a") })
            {
                Osc52Checks.Set("sentinel");
                await Output(Osc52Checks.Sequence(text, selector, end));
                await Until(() => Osc52Checks.Get() == text, "Metal OSC 52 clipboard write: " + selector);
            }
            Osc52Checks.Set("sentinel");
            await Output("\e]52;c;!!!\a");
            Require(Osc52Checks.Get() == "sentinel" && Osc52Checks.Prompts() == 0, "Metal malformed OSC 52 changed the clipboard or writes prompted.");
            await Output("\e]52;c;/w==\a");
            Require(Osc52Checks.Get() == "sentinel", "Metal invalid UTF-8 changed the clipboard.");

            foreach (var (allow, text) in new[] { (false, "private clipboard"), (true, "Grüße\n世界"), (true, "") })
            {
                Osc52Checks.Allow(allow);
                Osc52Checks.Set(text);
                var marker = "OSC_READ_" + ++index;
                var response = System.Text.Encoding.UTF8.GetBytes(Osc52Checks.Sequence(allow ? text : ""));
                await Command(native, terminal, "stty -echo -icanon min 0 time 20; printf '\\n" + marker + "_BEGIN\\n\\033]52;c;?\\007'; dd bs=1 count=" + response.Length + " 2>/dev/null | od -An -tx1; stty sane; printf '\\n" + marker + "\\n'");
                try { await Until(() => terminal.ReadScreen().Split('\n').Any(line => line.Trim() == marker), "Metal clipboard query did not finish."); }
                catch { File.WriteAllText(CheckRoot.Artifact("osc52-query-screen.txt"), terminal.ReadScreen()); throw; }
                var hex = Osc52Checks.ReadHex(terminal.ReadScreen(), marker);
                var expected = Convert.ToHexString(response).ToLowerInvariant();
                if (hex != expected) File.WriteAllText(CheckRoot.Artifact("osc52-reply-screen.txt"), terminal.ReadScreen());
                Require(hex == expected, "Metal clipboard read permission or encoded response: " + hex);
            }
            Require(Osc52Checks.Prompts() == 3, "Metal clipboard reads did not each request permission.");
            Console.WriteLine("PASS OSC 52 Metal: shell-to-clipboard ST/BEL, Unicode, selectors, empty/malformed writes and denied/approved read replies");
        }
        finally { Osc52Checks.End(); window.Content = null; }
    }

    private static async Task CheckSkia(Window window)
    {
        using var terminal = new GhosttySkiaView();
        var theme = Color.FromRgb(18, 52, 86);
        var swatch = new Border
        {
            Width = 20,
            Height = 20,
            Background = new SolidColorBrush(theme),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        window.Content = new Grid { Children = { terminal, swatch } };
        window.Show();
        var native = Native.Content(Title);
        Native.Activate(native);
        await Until(() => Native.Ready(native) && terminal.IsGpuBacked, "The Skia framebuffer did not use the GPU.");
        terminal.Write("\e[?25l\e[H\e[48;2;255;0;0mRED\e[K\e[0m\r\nunchanged"u8);
        await Capture(pixel => pixel.Red > 150 && pixel.Green < 70 && pixel.Blue < 70);
        var rows = terminal.RowsRecorded;
        terminal.Write("\e[H\e[48;2;0;0;255mBLUE\e[K\e[0m"u8);
        await Capture(pixel => pixel.Blue > 150 && pixel.Red < 70 && pixel.Green < 100);
        Require(terminal.RowsRecorded == rows + 1, "Native Skia update rebuilt unrelated rows.");
        terminal.Colors = terminal.Colors with { Background = theme };
        window.Width = 500;
        await Capture(_ => true, 200, 150, compareTheme: true);
        window.Content = null;
        terminal.Dispose();
        Console.WriteLine("PASS native Skia: GPU framebuffer, incremental ANSI pixels, theme, Retina resize and disposal");

        async Task Capture(Func<SKColor, bool> matches, int x = 200, int y = 10, bool compareTheme = false)
        {
            var path = CheckRoot.Artifact("ghostty-skia-native.png");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                await Task.Delay(100);
                var capture = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
                foreach (var argument in new[] { "-x", "-o", "-l", Native.WindowNumber(native).ToString(), path }) capture.ArgumentList.Add(argument);
                using var process = Process.Start(capture)!;
                await process.WaitForExitAsync();
                Require(process.ExitCode == 0, "Own-window Skia capture failed.");
                using var codec = SKCodec.Create(path);
                using var colorSpace = SKColorSpace.CreateSrgb();
                using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height,
                    SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace));
                var scale = bitmap.Width / window.Bounds.Width;
                var pixel = bitmap.GetPixel((int)(x * scale), (int)(y * scale));
                var reference = bitmap.GetPixel(bitmap.Width - (int)(10 * scale), bitmap.Height - (int)(10 * scale));
                if (matches(pixel) && (!compareTheme || Math.Abs(pixel.Red - reference.Red) < 3 &&
                    Math.Abs(pixel.Green - reference.Green) < 3 && Math.Abs(pixel.Blue - reference.Blue) < 3)) return;
            }
            while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("Native Skia framebuffer pixels were not presented.");
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task CheckNativeLibrary(Window window)
    {
        using var view = new GhosttyView(new GhosttyLaunch(Directory.GetCurrentDirectory(), "/bin/sh"));
        window.Content = view;
        window.Show();
        Native.Activate(view.Handle);
        await Until(() => Native.Ready(view.Handle), "Library NSView did not activate.");
        view.FocusTerminal();
        await Task.Delay(250);
        Native.MutableInput(view.Handle);
        Native.Key(view.Handle, "\r", 36, false);
        await Until(() => view.ReadScreen().Contains("MUTABLE_INPUT_OK", StringComparison.Ordinal), "Library NSView lost mutable input.");
        window.Content = null;
        Console.WriteLine("PASS library NSView: hosted input and mutable AppKit text buffers");
    }

    [SupportedOSPlatform("macos")]
    private static async Task Check(Window window)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var clipboard = CheckRoot.Create("texture-paste");
        using var terminal = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), "/bin/sh", ClipboardImageDirectory: clipboard))
        { Width = 640, Height = 360, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var clip = new Border
        {
            Width = 500,
            Height = 280,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = terminal
        };
        var overlay = new Border
        {
            Background = Brushes.Lime,
            Width = 80,
            Height = 50,
            Margin = new Thickness(32, 32, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        window.Content = new Grid { Background = Brushes.Magenta, Children = { clip, overlay } };
        window.Show();
        await terminal.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        var native = Native.Content(Title);
        Require(native != 0 && !Native.HasNativeTerminal(native), "The texture path attached a raw Ghostty NSView.");
        Require(!window.GetVisualDescendants().OfType<NativeControlHost>().Any(), "A native host entered the Avalonia visual tree.");
        Native.Activate(native);
        await Until(() => Native.Ready(native) && ReferenceEquals(window.InputHitTest(new Point(20, 20)), terminal), "Texture window did not become ready for pointer input.");
        Native.Click(native, 20, 20);
        await Until(() => terminal.IsKeyboardFocusWithin, "Clicking the texture did not focus it.");
        Type(native, "printf 'KEYPAD_%s_END\\n' ");
        Native.Key(native, "1", 83, false);
        Native.Key(native, "\r", 76, false);
        await Until(() => terminal.ReadScreen().Contains("KEYPAD_1_END", StringComparison.Ordinal), "The numeric keypad did not reach the texture shell.");
        terminal.Colors = terminal.Colors with { Background = Color.FromRgb(18, 52, 86) };
        Type(native, "printf 'PASTE_%s_END\\n' \"");
        Native.Paste(native, 0);
        Type(native, "\"\r");
        await Until(() => terminal.ReadScreen().Contains("PASTE_TEXT_PASTE_OK_END", StringComparison.Ordinal), "Texture clipboard text did not reach the shell.");
        Type(native, "printf 'IMAGE_%s_END\\n' ");
        Native.Paste(native, 1);
        Type(native, "\r");
        await Until(() => Directory.Exists(clipboard) && Directory.GetFiles(clipboard, "*.png").Length == 1,
            "Texture image paste did not save a PNG.");
        var shortcut = false;
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.J && e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift)) { shortcut = true; e.Handled = true; }
        };
        Native.ToggleBottom(native);
        Require(shortcut, "The texture consumed an application Command shortcut.");
        Type(native, "printf '\\033[48;2;255;0;0mTEXTURE_%s\\033[0m\\n' GPU\r");
        await Until(() => terminal.ReadScreen().Contains("TEXTURE_GPU", StringComparison.Ordinal), "AppKit input did not reach the texture terminal's shell.");
        await Task.Delay(250);
        var path = CheckRoot.Artifact("ghostty-texture-composited.png");
        var capture = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
        foreach (var argument in new[] { "-x", "-o", "-l", Native.WindowNumber(native).ToString(), path }) capture.ArgumentList.Add(argument);
        using (var process = Process.Start(capture)!) { await process.WaitForExitAsync(); Require(process.ExitCode == 0, "Own-window capture failed."); }
        using (var codec = SKCodec.Create(path))
        using (var colorSpace = SKColorSpace.CreateSrgb())
        using (var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace)))
        {
            var scale = bitmap.Width / window.Bounds.Width;
            SKColor Pixel(int x, int y) => bitmap.GetPixel((int)(x * scale), (int)(y * scale));
            var green = Pixel(60, 60);
            var clipped = Pixel(550, 100);
            var background = Pixel(450, 240);
            Require(green.Green > 220 && green.Red < 30 && green.Blue < 30, "An Avalonia overlay did not cover the Ghostty texture.");
            Require(clipped.Red > 220 && clipped.Blue > 220 && clipped.Green < 30, "The texture escaped its Avalonia parent clip.");
            Require(Math.Abs(background.Red - 18) < 3 && Math.Abs(background.Green - 52) < 3 && Math.Abs(background.Blue - 86) < 3,
                "The texture did not repaint its changed theme background.");
            Require(bitmap.Pixels.Count(pixel => pixel.Red > 100 && pixel.Red > pixel.Green * 2 && pixel.Red > pixel.Blue * 2) > 40,
                "The composed screenshot contains no terminal ANSI red pixels.");
        }
        terminal.Width = 400; terminal.Height = 200;
        await Until(() => terminal.TextureSize == PixelSize.FromSize(new Size(400, 200), window.RenderScaling), "Texture did not resize at the window's backing scale.");
        for (var i = 0; i < 16; i++)
        {
            var size = new Size(i % 2 == 0 ? 440 : 400, i % 2 == 0 ? 240 : 200);
            terminal.Width = size.Width; terminal.Height = size.Height;
            await Until(() => terminal.TextureSize == PixelSize.FromSize(size, window.RenderScaling), "A recycled texture did not resize.");
            Require(terminal.RetainedTextureCount is > 0 and <= 3, "Resizing retained obsolete source textures.");
        }
        var frames = terminal.Frames;
        clip.Child = null;
        await Task.Delay(80);
        clip.Child = terminal;
        await Until(() => terminal.Frames > frames, "Texture did not resume after reattachment.");
        Require(terminal.ReadScreen().Contains("TEXTURE_GPU", StringComparison.Ordinal), "Reattachment replaced the shell.");
        for (var i = 0; i < 12; i++)
        {
            frames = terminal.Frames;
            terminal.Colors = terminal.Colors with { Background = i % 2 == 0 ? Colors.Red : Colors.Blue };
            await Until(() => terminal.Frames > frames, "A frame-ready notification lost the last repaint.");
            Require(terminal.RetainedTextureCount <= 3, "Repainting accumulated source textures.");
        }
        await Task.Delay(250);
        using (var process = Process.Start(capture)!) { await process.WaitForExitAsync(); Require(process.ExitCode == 0, "Recycled texture capture failed."); }
        using (var codec = SKCodec.Create(path))
        using (var colorSpace = SKColorSpace.CreateSrgb())
        using (var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace)))
        {
            var scale = bitmap.Width / window.Bounds.Width;
            var pixel = bitmap.GetPixel((int)(380 * scale), (int)(180 * scale));
            Require(pixel.Blue > 220 && pixel.Red < 30 && pixel.Green < 30, "A reused texture displayed stale or overwritten pixels.");
        }
        Native.Activate(native);
        await Until(() => Native.Ready(native), "Opacity capture requires an active window.");
        frames = terminal.Frames;
        terminal.Opacity = 0.5;
        await Until(() => terminal.Frames > frames, "Opacity did not produce a GPU draw.");
        var opacityDeadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            await Task.Delay(100);
            using (var process = Process.Start(capture)!) { await process.WaitForExitAsync(); Require(process.ExitCode == 0, "Opacity capture failed."); }
            using var codec = SKCodec.Create(path);
            using var colorSpace = SKColorSpace.CreateSrgb();
            using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height,
                SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace));
            var scale = bitmap.Width / window.Bounds.Width;
            var pixel = bitmap.GetPixel((int)(380 * scale), (int)(180 * scale));
            if (pixel.Red is > 110 and < 145 && pixel.Blue > 220 && pixel.Green < 30) break;
            Require(DateTime.UtcNow < opacityDeadline, $"Direct texture opacity was not presented: {pixel}.");
        }
        terminal.Opacity = 1;
        frames = terminal.Frames;
        terminal.Colors = terminal.Colors with { Background = Colors.Red };
        terminal.Dispose();
        await Task.Delay(250);
        Require(terminal.RetainedTextureCount == 0 && terminal.Frames == frames, "Disposal retained sources or presented a queued frame.");
        Console.WriteLine("PASS Ghostty texture: native input, text/image paste, app shortcuts, GPU import, ANSI/theme pixels, Avalonia overlay and clipping, no hosted NSView, Retina resize and retained session");
        Console.WriteLine("PASS direct texture: bounded sources through repeated resize and repaint, final-frame pixels, remount and disposal with a repaint queued");
    }

    [SupportedOSPlatform("macos")]
    private static async Task CheckScrollBoundary(Window window)
    {
        using var terminal = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), ExternalIo: new GhosttyExternalIo()));
        window.Content = terminal;
        window.Show();
        await terminal.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        var handoffError = await Task.Run(() =>
        {
            var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libGhosttyAvaloniaView.dylib"));
            try { return Native.Handoff(library); }
            finally { NativeLibrary.Free(library); }
        });
        Require(handoffError == 0, Marshal.PtrToStringUTF8(handoffError) ?? "Texture handoff failed.");
        Console.WriteLine("PASS native texture handoff: completed frame remains readable during target reuse; replacement, GPU failure and stop release the producer");
        terminal.WriteOutput(System.Text.Encoding.UTF8.GetBytes("\e[?25l" + string.Concat(Enumerable.Range(0, 200).Select(i => $"SCROLL_LINE_{i}\r\n"))));
        await Until(() => terminal.ReadScreen().Contains("SCROLL_LINE_199", StringComparison.Ordinal), "Scrollback fixture did not reach its bottom.");
        await Task.Delay(250);
        var bottom = terminal.ReadScreen();
        var native = Native.Content(Title);
        async Task<SKColor[]> Capture()
        {
            var directory = CheckRoot.Create("native-scroll");
            var capturePath = Path.Combine(directory, "frame.png");
            try
            {
                var capture = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
                foreach (var argument in new[] { "-x", "-o", "-l", Native.WindowNumber(native).ToString(), capturePath }) capture.ArgumentList.Add(argument);
                using var process = Process.Start(capture)!;
                await process.WaitForExitAsync();
                Require(process.ExitCode == 0, "Scroll viewport capture failed.");
                using var bitmap = SKBitmap.Decode(capturePath);
                return bitmap.Pixels;
            }
            finally { Directory.Delete(directory, true); }
        }
        var bottomPixels = await Capture();
        var unavailable = terminal.UnavailableFrames;
        var frames = terminal.Frames;
        var pointer = new Pointer(93, PointerType.Mouse, true);
        for (var i = 0; i < 300; i++)
        {
            terminal.RaiseEvent(new PointerWheelEventArgs(terminal, pointer, terminal, new Point(100, 100),
                (ulong)i, new PointerPointProperties(), KeyModifiers.None, new Vector(0, -.2)));
            await Task.Delay(2);
        }
        await Until(() => terminal.Frames > frames, "Bottom-edge wheel input did not exercise texture composition.");
        Require(terminal.ReadScreen() == bottom, "Downward scrolling at the bottom changed terminal contents.");
        Require(terminal.UnavailableFrames == unavailable, "Bottom-edge scrolling exposed a draw without a completed texture.");
        Require(terminal.RetainedTextureCount is > 0 and <= 3, "Bottom-edge scrolling accumulated source textures.");
        Require((await Capture()).SequenceEqual(bottomPixels), "Bottom-edge scrolling changed the rendered viewport.");
        terminal.RaiseEvent(new PointerWheelEventArgs(terminal, pointer, terminal, new Point(100, 100),
            301, new PointerPointProperties(), KeyModifiers.None, new Vector(0, 3)));
        await Task.Delay(250);
        Require(!(await Capture()).SequenceEqual(bottomPixels), "Ordinary upward scrolling stopped working.");
        terminal.RaiseEvent(new PointerWheelEventArgs(terminal, pointer, terminal, new Point(100, 100),
            302, new PointerPointProperties(), KeyModifiers.None, new Vector(0, -100)));
        await Task.Delay(250);
        Require((await Capture()).SequenceEqual(bottomPixels), "Downward scrolling did not return to the bottom.");
        Console.WriteLine("PASS bottom-edge scrolling: fractional wheel input preserves screen contents and completed texture availability");
    }

    private static void Type(nint view, string text)
    {
        Native.Activate(view);
        foreach (var c in text) Native.Key(view, c.ToString(), c == '\r' ? (ushort)36 : (ushort)0, false);
    }

    private static async Task Command(nint native, Control target, string command)
    {
        var path = CheckRoot.Artifact("osc52-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, command + "\n");
        Native.Activate(native);
        target.Focus();
        await Until(() => Native.Ready(native) && target.IsKeyboardFocusWithin, "OSC 52 command target did not receive focus.");
        target.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "/bin/sh " + path });
        Native.Key(native, "\r", 36, false);
    }

    [SupportedOSPlatform("macos")]
    private static async Task CheckSessions(Window window)
    {
        var root = CheckRoot.Create("texture-sessions");
        Directory.CreateDirectory(root);
        using var traffic = new HttpRequestProbe();
        await using (var pty = new PtyTerminalService())
            await Switching(renderer => TerminalBackends.Ghostty(new LocalTerminalAdapter(pty), renderer), "local");
        Require(traffic.Requests == 0, "Local terminals issued HTTP/RPC requests.");
        Console.WriteLine("PASS local Ghostty: zero HTTP/RPC requests through launch, input, resize, takeover, renderer switching and close");
        const string token = "texture-check-token";
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, token);
        await server.StartAsync();
        var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var remote = new RemoteTerminalAdapter(address, token);
        await Switching(renderer => TerminalBackends.Ghostty(new RemoteTerminalConnection(address, token, remote), renderer), "remote");
        Require(traffic.Requests > 0, "The HTTP probe did not observe the remote control case.");
        Console.WriteLine("PASS OSC 52 transport: local and authenticated remote clipboard writes and read replies in Metal and Skia, including writes after renderer switches");
        Console.WriteLine("PASS texture/Skia switching: local and authenticated remote host shells keep their PID and variables; Ctrl-C and real exit status work");

        async Task Switching(Func<Func<string>, TerminalFactory> factory, string name)
        {
            var renderer = TerminalRenderers.Texture;
            var launch = new TerminalLaunch(root, name + Guid.NewGuid().ToString("N"), Path.Combine(root, "clipboard"), "texture-client");
            using var clipboardScope = new Osc52Checks.ClipboardScope();
            using var tab = new TerminalView(factory(() => renderer), launch);
            window.Content = tab;
            window.Show();
            await Started();
            var native = Native.Content(Title);
            Native.Activate(native);
            await Until(() => Native.Ready(native), "The session window did not activate.");
            tab.FocusTerminal();
            Type(native, "RENDER_CHECK=stable; printf 'RENDER_%s_%s_\\n' $$ $RENDER_CHECK\r");
            await Until(() => Regex.IsMatch(Screen(), @"RENDER_(\d+)_stable_"), "The initial texture session did not answer.");
            var pid = Regex.Match(Screen(), @"RENDER_(\d+)_stable_").Groups[1].Value;
            var clipboardRound = 0;
            await Clipboard(false);
            foreach (var next in new[] { TerminalRenderers.Skia, TerminalRenderers.Texture })
            {
                renderer = next;
                tab.Restart();
                await Started();
                tab.FocusTerminal();
                Type(native, "printf 'AGAIN_%s_%s_\\n' $$ $RENDER_CHECK\r");
                await Until(() => Screen().Contains($"AGAIN_{pid}_stable_", StringComparison.Ordinal), "Switching renderer lost the host shell.");
                Require(!Native.HasNativeTerminal(native), "A renderer switch attached a raw terminal NSView.");
                await Clipboard(false);
            }
            var control = tab.GetVisualDescendants().OfType<Control>().Single(value => value is GhosttyTextureView or GhosttySkiaView);
            TerminalSize Size() => control is GhosttyTextureView metal ? metal.Size : ((GhosttySkiaView)control).Size;
            var previousSize = Size();
            window.Width += 96;
            await Until(() => Size() != previousSize, "The terminal grid did not resize.");
            var size = Size();
            await Command(native, control, "printf 'GRID_'; stty size");
            await Until(() => Screen().Contains($"GRID_{size.Rows} {size.Columns}", StringComparison.Ordinal), "The host PTY did not receive the resized grid.");
            var held = tab.Backend;
            using (var other = new TerminalView(factory(() => renderer), launch with { ClientId = "takeover-client" }))
            {
                window.Content = other;
                await other.Backend!.Started.WaitAsync(TimeSpan.FromSeconds(20));
                var taker = other.Backend;
                await Until(() => tab.Backend != held, "Takeover was not reported to the displaced view.");
                window.Content = tab;
                await Until(() => tab.IsWatching, "The displaced view did not watch the terminal it lost.");
                Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(tab).OfType<Button>().Single(button => button.Name == "TerminalTakeBack").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Started();
                await Until(() => other.Backend != taker, "Taking back did not displace the second client.");
            }
            tab.FocusTerminal();
            Type(native, "printf 'TAKEBACK_%s_%s_\\n' $$ $RENDER_CHECK\r");
            await Until(() => Screen().Contains($"TAKEBACK_{pid}_stable_", StringComparison.Ordinal), "Takeover lost the shell state.");
            await Clipboard(true);
            Type(native, "sleep 30\r");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!await tab.IsBusyAsync())
            {
                Require(DateTime.UtcNow < deadline, "The shell never reported a foreground process.");
                await Task.Delay(20);
            }
            Native.Key(native, "\x03", 8, true);
            while (await tab.IsBusyAsync())
            {
                Require(DateTime.UtcNow < deadline, "Control-C did not interrupt the foreground process.");
                await Task.Delay(20);
            }
            Type(native, "exit 7\r");
            await Until(() => tab.IsExited, "The shell exit was not shown.");
            Require(await tab.Backend!.Exited == 7, "The renderer lost the exit status.");
            window.Content = null;

            using var freshSkia = new TerminalView(factory(() => TerminalRenderers.Skia),
                launch with { SessionId = launch.SessionId + "-clipboard" });
            window.Content = freshSkia;
            await Until(() => freshSkia.Backend is not null, "No fresh Skia backend was created.");
            await freshSkia.Backend!.Started.WaitAsync(TimeSpan.FromSeconds(20));
            freshSkia.FocusTerminal();
            var skiaView = freshSkia.GetVisualDescendants().OfType<GhosttySkiaView>().Single();
            await Clipboard(true, skiaView.ReadScreen, TerminalRenderers.Skia, skiaView);
            await freshSkia.Backend.CloseAsync();
            window.Content = null;

            async Task Clipboard(bool read, Func<string>? readScreen = null, string? mode = null, Control? target = null)
            {
                var screen = readScreen ?? Screen;
                var selected = mode ?? renderer;
                target ??= tab.GetVisualDescendants().OfType<Control>().Single(control => control is GhosttyTextureView or GhosttySkiaView);
                Osc52Checks.Set("sentinel");
                var text = name + "-" + selected;
                var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                await Command(native, target, "printf '\\033]52;c;" + encoded + "\\007'");
                await Until(() => Osc52Checks.Get() == text, "OSC 52 did not reach the client clipboard over " + text);
                var prompts = Osc52Checks.Prompts();
                if (!read) return;
                Osc52Checks.Allow(true);
                Osc52Checks.Set("host read");
                var marker = "HOST_READ_" + name + ++clipboardRound;
                var end = selected == TerminalRenderers.Skia || fallbackCheck ? "\a" : "\e\\";
                var response = System.Text.Encoding.UTF8.GetBytes(Osc52Checks.Sequence("host read", terminator: end));
                await Command(native, target, "stty -echo -icanon min 0 time 20; printf '\\n" + marker + "_BEGIN\\n\\033]52;c;?\\007'; dd bs=1 count=" + response.Length + " 2>/dev/null | od -An -tx1; stty sane; printf '\\n" + marker + "\\n'");
                try { await Until(() => screen().Split('\n').Any(line => line.Trim() == marker), "OSC 52 read did not return to " + text); }
                catch { File.WriteAllText(CheckRoot.Artifact("osc52-host-read-screen.txt"), screen()); throw; }
                var hex = Osc52Checks.ReadHex(screen(), marker);
                var expected = Convert.ToHexString(response).ToLowerInvariant();
                Require(hex == expected && Osc52Checks.Prompts() == prompts + 1, "Host OSC 52 read response or permission: " + text);
            }

            async Task Started()
            {
                await Until(() => tab.Backend is not null, "No renderer backend was created.");
                await tab.Backend!.Started.WaitAsync(TimeSpan.FromSeconds(20));
                if (fallbackCheck) Require(tab.GetVisualDescendants().OfType<GhosttySkiaView>().Any() && !tab.GetVisualDescendants().OfType<GhosttyTextureView>().Any(), "Software rendering did not fall back to Skia.");
                else if (renderer == TerminalRenderers.Texture) Require(tab.GetVisualDescendants().OfType<GhosttyTextureView>().Any(), "The Metal execution check silently fell back to Skia.");
            }
            string Screen() => tab.GetVisualDescendants().OfType<GhosttyTextureView>().SingleOrDefault()?.ReadScreen()
                ?? tab.GetVisualDescendants().OfType<GhosttySkiaView>().Single().ReadScreen();
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static class Native
    {
        [DllImport("TerminalEvents", EntryPoint = "sr_texture_handoff")] internal static extern nint Handoff(nint library);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_mutable_input")] internal static extern void MutableInput(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_paste")] internal static extern void Paste(nint view, int format);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_toggle_bottom")] internal static extern void ToggleBottom(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_texture_content")] internal static extern nint Content([MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        [DllImport("TerminalEvents", EntryPoint = "sr_texture_window_number")] internal static extern int WindowNumber(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_texture_has_native_terminal")][return: MarshalAs(UnmanagedType.I1)] internal static extern bool HasNativeTerminal(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_activate")] internal static extern void Activate(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_ready")][return: MarshalAs(UnmanagedType.I1)] internal static extern bool Ready(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_click")] internal static extern void Click(nint view, double x, double y);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_key")] internal static extern void Key(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, ushort code, [MarshalAs(UnmanagedType.I1)] bool control);
    }
}