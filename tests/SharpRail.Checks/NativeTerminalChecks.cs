using System.Runtime.Versioning;
using System.Net;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.LogicalTree;
using Ghostty.Avalonia;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.UI;
using SharpRail.UI.Terminal;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks;

[SupportedOSPlatform("macos")]
internal static class NativeTerminalChecks
{
    private const string ProfileDirectory = "profile 'images";
    public static void Run(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Native terminal checks require macOS.");
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), ".bench", "native-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, ".git"), "gitdir: " + Path.Combine(fixture, "absent"));
        var profile = new ProfileStore(Path.Combine(fixture, ProfileDirectory));
        var layout = new LayoutSession(profile.Data.Windows[0].Layout);
        // Opening the workspace provisions its initial terminal in the bottom group.
        layout.SwitchWorkspace(fixture);
        profile.Data.Windows[0].Layout = layout.State;
        profile.Save();
        Environment.SetEnvironmentVariable("SHARPRAIL_ROOT", fixture);
        Environment.SetEnvironmentVariable("SHARPRAIL_PROFILE", Path.Combine(fixture, ProfileDirectory));
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Metal] }).AfterSetup(_ =>
            Dispatcher.UIThread.Post(async () =>
            {
                var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                // Keyboard encoding depends on the layout, and Ghostty reloads it asynchronously; use U.S. for the whole
                // run rather than switching mid-check, and give the user's layout back at the end.
                var previousLayout = Native.SelectLayout("com.apple.keylayout.US");
                try
                {
                    await Check((WorkbenchWindow)desktop.MainWindow!, fixture);
                    Console.WriteLine("PASS native Avalonia embedded terminal: keyboard routing, Mod+Shift+J from the terminal, image/text paste, theme propagation, Metal rendering, shell, session retention, workspace isolation and close disposal");
                    await CheckLocalSessions(fixture);
                    Console.WriteLine("PASS native local terminal sessions: Ghostty relay to the app's host PTY with input latency, resize, reattach after its window closes, and the real exit status");
                    await CheckRemote(fixture);
                    Console.WriteLine("PASS native remote terminal: Ghostty relay to an authenticated gRPC host PTY with I/O, worktree root, resize, busy foreground and exit status");
                    desktop.Shutdown(0);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    foreach (var view in desktop.Windows.SelectMany(window => window.GetVisualDescendants().OfType<GhosttyTextureView>()))
                        Console.Error.WriteLine(view.ReadScreen());
                    desktop.Shutdown(1);
                }
                finally { Native.SelectLayout(previousLayout); }
            })).StartWithClassicDesktopLifetime(args);
    }

    private static async Task Until(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Native terminal condition timed out: " + description);
            await Task.Delay(50);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Native terminal condition timed out: " + description);
            await Task.Delay(50);
        }
    }

    private static string Read(GhosttyTextureView view) => view.ReadScreen();

    private static async Task Check(WorkbenchWindow window, string root)
    {
        await Until(() => window.WorkspaceMounted);
        var bottom = window.Layout.State.Groups.Single(group => group.Region == "bottom").Id;
        GhosttyTextureView? host = null;
        await Until(() => (host = window.GetVisualDescendants().OfType<GhosttyTextureView>().SingleOrDefault()) is not null && host.RetainedTextureCount != 0);
        var original = host!;
        Native.Activate(original);
        await Until(() => Native.Ready(original));
        await Task.Delay(1000);
        var tab = window.Layout.Tabs(bottom).Single();
        var button = window.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "Tab_" + tab.Id.Replace(':', '_'));
        ((ISelectionItemProvider)ControlAutomationPeer.CreatePeerForElement(button)!).Select();
        await Until(() => Native.Focused(original));
        Native.ToggleBottom(original);
        await Until(() => !window.Layout.State.BottomVisible && !window.GetVisualDescendants().Contains(host));
        window.Layout.Visible("bottom", true);
        await Until(() => window.GetVisualDescendants().Contains(host));
        if (host! != original) throw new InvalidOperationException("Hiding the bottom panel replaced the shell.");
        Button? shown = null;
        await Until(() => (shown = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(item => item.Name == "Tab_" + tab.Id.Replace(':', '_'))) is not null);
        ((ISelectionItemProvider)ControlAutomationPeer.CreatePeerForElement(shown!)!).Select();
        await Until(() => Native.Focused(original));
        await Until(() => Native.Background(original) == 0x18181b);
        SharpRail.UI.Rendering.Ui.Apply(SharpRail.UI.Rendering.Themes.Resolve("light"));
        await Until(() => Native.Background(original) == 0xe4e4e7);
        foreach (var theme in SharpRail.UI.Rendering.Themes.All)
        {
            SharpRail.UI.Rendering.Ui.Apply(theme);
            await Until(() => Native.Background(original) == (theme["content"].ToUInt32() & 0xffffff));
        }
        SharpRail.UI.Rendering.Ui.Apply(SharpRail.UI.Rendering.Themes.Resolve("dark"));
        await Until(() => Native.Background(original) == 0x18181b);
        await Type(original, "printf 'TAB_%s_OK\\n' KEYBOARD\r");
        await Until(() => Read(original).Contains("TAB_KEYBOARD_OK", StringComparison.Ordinal));
        for (var format = 1; format <= 2; format++)
        {
            Native.Input(original, "image=");
            Native.Paste(original, format);
            Native.Input(original, $"; test -s \"$image\" && printf 'IMAGE_%s_OK\\n' {format}\r");
            try { await Until(() => Read(original).Contains($"IMAGE_{format}_OK", StringComparison.Ordinal)); }
            catch (InvalidOperationException error) { throw new InvalidOperationException(Read(original), error); }
        }
        var images = Directory.GetFiles(Path.Combine(root, ProfileDirectory, "clipboard"), "*.png");
        if (images.Length != 2 || images.Any(path => !File.ReadAllBytes(path).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new InvalidOperationException("Clipboard images were not saved as distinct PNG files.");
        Native.Input(original, "printf '%s\\n' ");
        Native.Paste(original, 0);
        Native.Input(original, "\r");
        await Until(() => Read(original).Contains("TEXT_PASTE_OK", StringComparison.Ordinal));
        Native.Click(original, 30, 30);
        await Task.Delay(100);
        await Until(() => Native.Focused(original));
        await Type(original, "printf 'CLICK_%s_OK\\n' KEYBOARD\r");
        await Until(() => Read(original).Contains("CLICK_KEYBOARD_OK", StringComparison.Ordinal));
        await Type(original, "printf 'EDIT_%s_OK\\n' KEYBOARX");
        Native.Key(original, "\x7f", 51, false);
        await Type(original, "D\r");
        await Until(() => Read(original).Contains("EDIT_KEYBOARD_OK", StringComparison.Ordinal));
        // Option acts as Alt on U.S. layouts, as in Ghostty's app: Option+Backspace deletes the previous word.
        await Type(original, "printf 'WORD_%s_OK\\n' KEYBOARD extra");
        Native.OptionBackspace(original);
        await Type(original, "\r");
        await Until(() => Read(original).Contains("WORD_KEYBOARD_OK", StringComparison.Ordinal));
        if (Read(original).Contains("WORD_ext", StringComparison.Ordinal))
            throw new InvalidOperationException("Option+Backspace did not delete the previous word.");
        // Programs that enable the kitty keyboard protocol (for example Claude Code) must still see Alt on Option+Backspace.
        await Type(original, "printf '\\033[>1u'; stty raw -echo; printf 'KITTY_%s' READY; dd bs=1 count=8 2>/dev/null | od -An -tx1 | tr -d ' \\n' | sed 's/^/KITTY_/;s/$/_END/'; stty sane; printf '\\033[<u\\n'\r");
        // The marker follows the protocol request on the same output stream, so once it shows, Ghostty has applied it.
        await Until(() => Read(original).Contains("KITTY_READY", StringComparison.Ordinal));
        Native.OptionBackspace(original);
        await Type(original, "ZZZZZZZZ");
        await Until(() => Read(original).Contains("_END", StringComparison.Ordinal) && Read(original).Contains("KITTY_", StringComparison.Ordinal));
        if (!Read(original).Contains("KITTY_1b5b3132373b3375", StringComparison.Ordinal))
            throw new InvalidOperationException("Option+Backspace lost Alt under the kitty keyboard protocol: " +
                Read(original)[Read(original).LastIndexOf("KITTY_", StringComparison.Ordinal)..]);
        await Type(original, "discard_me");
        Native.Key(original, "\x03", 8, true);
        await Type(original, "printf 'CTRL_%s_OK\\n' KEYBOARD\r");
        await Until(() => Read(original).Contains("CTRL_KEYBOARD_OK", StringComparison.Ordinal));
        Native.Input(original, $"export SHARPRAIL_SESSION=original; test \"$PWD\" = '{root.Replace("'", "'\\''")}' && printf '\\nNATIVE_%s_OK\\n' SHELL\r");
        await Until(() => Read(original).Contains("NATIVE_SHELL_OK", StringComparison.Ordinal) && Native.Rendered(original));
        Native.Input(original, "printf '\\nNATIVE_PID_%s\\n' \"$$\"\r");
        await Until(() => System.Text.RegularExpressions.Regex.IsMatch(Read(original), @"NATIVE_PID_\d+"));
        var processId = int.Parse(System.Text.RegularExpressions.Regex.Match(Read(original), @"NATIVE_PID_(\d+)").Groups[1].Value);
        var side = window.Layout.State.Groups.First(group => group.Region == "left").Id;
        if (!window.Layout.Move(tab.Id, bottom, side, 0)) throw new InvalidOperationException("Terminal move refused.");
        await Until(() => window.GetVisualDescendants().Contains(host));
        if (host! != original) throw new InvalidOperationException("Moving a tab replaced the shell.");
        window.Layout.Fold(side);
        await Task.Delay(150);
        window.Layout.Fold(side);
        await Until(() => window.GetVisualDescendants().Contains(host));
        Native.Input(original, "printf '\\nRETAINED_%s\\n' \"$SHARPRAIL_SESSION\"\r");
        await Until(() => Read(original).Contains("RETAINED_original", StringComparison.Ordinal));
        var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, ".git"), "gitdir: " + Path.Combine(other, "absent"));
        await window.OpenProjectAsync(other);
        GhosttyTextureView? initial = null;
        await Until(() => (initial = window.GetVisualDescendants().OfType<GhosttyTextureView>().SingleOrDefault()) is not null && initial.RetainedTextureCount != 0);
        window.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "NewTerminal_" + bottom)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        GhosttyTextureView? second = null;
        await Until(() => (second = window.GetVisualDescendants().OfType<GhosttyTextureView>().SingleOrDefault()) is not null &&
            !ReferenceEquals(second, initial) && second.RetainedTextureCount != 0);
        await Until(() => Native.Focused(second!));
        await Type(second!, "printf 'NEW_%s_OK\\n' KEYBOARD\r");
        await Until(() => Read(second!).Contains("NEW_KEYBOARD_OK", StringComparison.Ordinal));
        Native.Input(second!, $"test \"$PWD\" = '{other.Replace("'", "'\\''")}' && printf '\\nISOLATED_%s\\n' \"${{SHARPRAIL_SESSION-unset}}\"\r");
        await Until(() => Read(second!).Contains("ISOLATED_unset", StringComparison.Ordinal));
        await window.OpenProjectAsync(root);
        window.Layout.Select(side, tab.Id);
        await Until(() => window.GetVisualDescendants().Contains(host));
        if (host! != original) throw new InvalidOperationException("Workspace switching replaced the shell.");
        window.Width -= 100;
        await Until(() => Native.Rendered(original));
        window.Layout.Close(side, tab.Id);
        if (host!.RetainedTextureCount != 0) throw new InvalidOperationException("Closing a terminal did not dispose its native session.");
        await Until(() => Native.Kill(processId, 0) == -1 && Marshal.GetLastPInvokeError() == 3);
    }

    // Local tabs relay to the app's own host, so a shell outlives its window and reports its real exit code.
    private static async Task CheckLocalSessions(string fixture)
    {
        var root = Path.Combine(fixture, "local sessions");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + Path.Combine(root, "absent"));
        var profile = Path.Combine(fixture, "local-sessions-profile");
        var terminals = ((App)Application.Current!).Terminals;
        WorkbenchWindow Open() => new(new LocalProjectAdapter(new SharpRail.Host.Core.ProjectServices(root)), root, new ProfileStore(profile), terminals) { Width = 1000, Height = 700 };
        async Task<GhosttyTextureView> Surface(WorkbenchWindow window)
        {
            await Until(() => window.WorkspaceMounted);
            GhosttyTextureView? host = null;
            await Until(() => (host = window.GetVisualDescendants().OfType<GhosttyTextureView>().SingleOrDefault()) is not null && host.RetainedTextureCount != 0);
            var view = host!;
            Native.Activate(view);
            await Until(() => Native.Ready(view));
            await Task.Delay(1000);
            return view;
        }
        var first = Open();
        first.Show();
        var view = await Surface(first);
        Native.Input(view, "export TR_RELOAD=survived; printf 'LOCAL_PID_%s_\\n' \"$$\"\r");
        await Until(() => System.Text.RegularExpressions.Regex.IsMatch(Read(view), @"LOCAL_PID_\d+_"));
        var pid = System.Text.RegularExpressions.Regex.Match(Read(view), @"LOCAL_PID_(\d+)_").Groups[1].Value;
        first.GetVisualDescendants().OfType<TerminalView>().Single().FocusTerminal();
        await Until(() => Native.Focused(view));
        var delays = new List<double>();
        for (var sample = 0; sample < 10; sample++)
        {
            var marker = "LAT" + sample + "X";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Native.Key(view, marker[..1], 0, false);
            foreach (var character in marker[1..]) Native.Key(view, character.ToString(), 0, false);
            await Until(() => Read(view).Contains(marker, StringComparison.Ordinal));
            delays.Add(clock.Elapsed.TotalMilliseconds);
            Native.Key(view, "\x15", 32, true);
        }
        delays.Sort();
        Console.WriteLine($"SHARPRAIL_TERMINAL_ECHO median={delays[5]:F1}ms max={delays[^1]:F1}ms");
        if (delays[5] > 100) throw new InvalidOperationException($"Local relay input echo is too slow: median {delays[5]:F1} ms.");
        Native.Input(view, "printf 'SIZE_%s_\\n' \"$(stty size | tr ' ' x)\"\r");
        await Until(() => System.Text.RegularExpressions.Regex.IsMatch(Read(view), @"SIZE_\d+x\d+_"));
        var before = System.Text.RegularExpressions.Regex.Match(Read(view), @"SIZE_(\d+x\d+)_").Groups[1].Value;
        first.Width -= 300;
        await Task.Delay(500);
        Native.Input(view, "printf 'RESIZED_%s_\\n' \"$(stty size | tr ' ' x)\"\r");
        await Until(() => System.Text.RegularExpressions.Regex.Match(Read(view), @"RESIZED_(\d+x\d+)_") is { Success: true } match && match.Groups[1].Value != before);
        first.Close();
        await Task.Delay(500);
        if (Native.Kill(int.Parse(pid), 0) != 0) throw new InvalidOperationException("Closing the window ended the local shell.");
        var second = Open();
        second.Show();
        try
        {
            var reopened = await Surface(second);
            Native.Input(reopened, "printf 'AGAIN_%s_%s_\\n' \"$$\" \"$TR_RELOAD\"\r");
            await Until(() => Read(reopened).Contains($"AGAIN_{pid}_survived_", StringComparison.Ordinal));
            var terminal = second.GetVisualDescendants().OfType<TerminalView>().Single();
            Native.Input(reopened, "exit 3\r");
            await Until(() => terminal.IsExited);
            var notice = terminal.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "TerminalExited").Text;
            if (notice != "[process exited with code 3]") throw new InvalidOperationException("The local exit status did not reach the tab: " + notice);
        }
        finally { second.Close(); }
    }

    // A remote workspace's Ghostty tab runs this executable's relay against a real gRPC host PTY.
    private static async Task CheckRemote(string fixture)
    {
        const string token = "native-remote-token";
        var root = Path.Combine(fixture, "remote workspace");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + Path.Combine(root, "absent"));
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, token);
        await server.StartAsync();
        var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var terminals = new RemoteTerminalAdapter(address, token);
        using var projects = new RemoteProjectAdapter(address, token);
        var window = new WorkbenchWindow(projects, root, new ProfileStore(Path.Combine(fixture, "remote-profile")),
            TerminalBackends.Ghostty(new RemoteTerminalConnection(address, token, terminals)), remote: true)
        { Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await Until(() => window.WorkspaceMounted);
            GhosttyTextureView? host = null;
            await Until(() => (host = window.GetVisualDescendants().OfType<GhosttyTextureView>().SingleOrDefault()) is not null && host.RetainedTextureCount != 0);
            var view = host!;
            var terminal = window.GetVisualDescendants().OfType<TerminalView>().Single();
            Native.Activate(view);
            await Until(() => Native.Ready(view));
            await Task.Delay(1000);
            Native.Input(view, "printf 'REMOTE_%s_\\n' \"$(pwd -P)\"\r");
            await Until(() => Read(view).Contains("REMOTE_" + root + "_", StringComparison.Ordinal));
            Native.Input(view, "printf 'SIZE_%s_\\n' \"$(stty size | tr ' ' x)\"\r");
            await Until(() => System.Text.RegularExpressions.Regex.IsMatch(Read(view), @"SIZE_\d+x\d+_"));
            var before = System.Text.RegularExpressions.Regex.Match(Read(view), @"SIZE_(\d+x\d+)_").Groups[1].Value;
            window.Width -= 300;
            await Task.Delay(500);
            Native.Input(view, "printf 'RESIZED_%s_\\n' \"$(stty size | tr ' ' x)\"\r");
            await Until(() => System.Text.RegularExpressions.Regex.Match(Read(view), @"RESIZED_(\d+x\d+)_") is { Success: true } match && match.Groups[1].Value != before);
            if (await terminal.IsBusyAsync()) throw new InvalidOperationException("An idle remote shell reported a busy foreground.");
            Native.Input(view, "sleep 30\r");
            await UntilAsync(async () => await terminal.IsBusyAsync(), "remote busy foreground");
            terminal.FocusTerminal();
            await Until(() => Native.Focused(view));
            Native.Key(view, "\x03", 8, true);
            await UntilAsync(async () => !await terminal.IsBusyAsync(), "remote interrupted foreground");
            Native.Input(view, "exit 3\r");
            await Until(() => terminal.IsExited);
            var notice = terminal.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "TerminalExited").Text;
            if (notice != "[process exited with code 3]") throw new InvalidOperationException("The remote exit status did not reach the tab: " + notice);
        }
        finally { window.Close(); await server.StopAsync(); }
    }

    private static async Task Type(GhosttyTextureView view, string text)
    {
        Native.Activate(view);
        foreach (var character in text)
        {
            await Until(() => Native.Ready(view) && Native.Focused(view));
            Native.Key(view, character.ToString(), character == '\r' ? (ushort)36 : (ushort)0, false);
            await Task.Delay(5);
        }
    }

    private static class Native
    {
        private static nint Content(GhosttyTextureView view) => WindowContent(((Window)TopLevel.GetTopLevel(view)!).Title!);
        internal static void Paste(GhosttyTextureView view, int format) => PasteNative(Content(view), format);
        internal static uint Background(GhosttyTextureView view) => view.Colors.Background.ToUInt32() & 0xffffff;
        internal static void Activate(GhosttyTextureView view) => ActivateNative(Content(view));
        internal static bool Ready(GhosttyTextureView view) => ReadyNative(Content(view));
        internal static void OptionBackspace(GhosttyTextureView view) => OptionBackspaceNative(Content(view));
        internal static void ToggleBottom(GhosttyTextureView view) => ToggleBottomNative(Content(view));
        internal static void Click(GhosttyTextureView view, double x, double y)
        {
            var point = view.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(view)!)!.Value;
            ClickNative(Content(view), point.X, point.Y);
        }
        internal static bool Focused(GhosttyTextureView view) => view.IsKeyboardFocusWithin;
        internal static void Key(GhosttyTextureView view, string text, ushort keyCode, bool control) => KeyNative(Content(view), text, keyCode, control);
        internal static void Input(GhosttyTextureView view, string text)
        {
            foreach (var character in text) Key(view, character.ToString(), character == '\r' ? (ushort)36 : (ushort)0, false);
        }
        internal static bool Rendered(GhosttyTextureView view) => view.Frames > 0;
        [DllImport("TerminalEvents", EntryPoint = "sr_texture_content")]
        private static extern nint WindowContent([MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_paste")]
        private static extern void PasteNative(nint view, int format);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_activate")]
        private static extern void ActivateNative(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_ready")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool ReadyNative(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_select_layout")]
        [return: MarshalAs(UnmanagedType.LPUTF8Str)]
        internal static extern string SelectLayout([MarshalAs(UnmanagedType.LPUTF8Str)] string identifier);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_option_backspace")]
        private static extern void OptionBackspaceNative(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_toggle_bottom")]
        private static extern void ToggleBottomNative(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_click")]
        private static extern void ClickNative(nint view, double x, double y);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_key")]
        private static extern void KeyNative(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, ushort keyCode, [MarshalAs(UnmanagedType.I1)] bool control);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int processId, int signal);
    }
}
