using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ghostty.Avalonia;
using SkiaSharp;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

internal static class NativeTextureChecks
{
    private static bool fallbackCheck;
    private const string Title = "SharpRail Ghostty texture check";

    public static void Run(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        fallbackCheck = args.Contains("--texture-fallback");
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
                    if (!fallbackCheck) await Check(window);
                    await CheckSessions(window);
                    if (!fallbackCheck) await CheckNativeLibrary(window);
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
        var clipboard = Path.GetFullPath(Path.Combine(".bench", "texture-paste-" + Guid.NewGuid().ToString("N")));
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
        var path = Path.GetFullPath(".bench/ghostty-texture-composited.png");
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

    private static void Type(nint view, string text)
    {
        Native.Activate(view);
        foreach (var c in text) Native.Key(view, c.ToString(), c == '\r' ? (ushort)36 : (ushort)0, false);
    }

    [SupportedOSPlatform("macos")]
    private static async Task CheckSessions(Window window)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".bench", "texture-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var pty = new PtyTerminalService())
        await using (var relay = new LocalTerminalRelay(pty))
            await Switching(await relay.ConnectAsync(), "local");
        const string token = "texture-check-token";
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, token);
        await server.StartAsync();
        var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var remote = new RemoteTerminalAdapter(address, token);
        await Switching(new RemoteTerminalConnection(address, token, remote), "remote");
        Console.WriteLine("PASS texture/Skia switching: local and authenticated remote host shells keep their PID and variables; Ctrl-C and real exit status work");

        async Task Switching(RemoteTerminalConnection connection, string name)
        {
            var renderer = TerminalRenderers.Texture;
            var launch = new TerminalLaunch(root, name + Guid.NewGuid().ToString("N"), Path.Combine(root, "clipboard"), "texture-client");
            using var tab = new TerminalView(TerminalBackends.Ghostty(connection, () => renderer), launch);
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
            foreach (var next in new[] { TerminalRenderers.Skia, TerminalRenderers.Texture })
            {
                renderer = next;
                tab.Restart();
                await Started();
                tab.FocusTerminal();
                Type(native, "printf 'AGAIN_%s_%s_\\n' $$ $RENDER_CHECK\r");
                await Until(() => Screen().Contains($"AGAIN_{pid}_stable_", StringComparison.Ordinal), "Switching renderer lost the host shell.");
                Require(!Native.HasNativeTerminal(native), "A renderer switch attached a raw terminal NSView.");
            }
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

            async Task Started()
            {
                await Until(() => tab.Backend is not null, "No renderer backend was created.");
                await tab.Backend!.Started.WaitAsync(TimeSpan.FromSeconds(20));
                if (fallbackCheck) Require(tab.GetVisualDescendants().OfType<GhosttySkiaView>().Any() && !tab.GetVisualDescendants().OfType<GhosttyTextureView>().Any(), "Software rendering did not fall back to Skia.");
            }
            string Screen() => tab.GetVisualDescendants().OfType<GhosttyTextureView>().SingleOrDefault()?.ReadScreen()
                ?? tab.GetVisualDescendants().OfType<GhosttySkiaView>().Single().ReadScreen();
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static class Native
    {
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
