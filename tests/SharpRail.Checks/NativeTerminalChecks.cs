using System.Net;
using System.Reflection;
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
using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.UI;
using SharpRail.UI.Terminal;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks;

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
        var layout = new LayoutSession(profile.Data.Layout);
        // Opening the workspace provisions its initial terminal in the bottom group.
        layout.SwitchWorkspace(fixture);
        profile.Data.Layout = layout.State;
        profile.Save();
        Environment.SetEnvironmentVariable("SHARPRAIL_ROOT", fixture);
        Environment.SetEnvironmentVariable("SHARPRAIL_PROFILE", Path.Combine(fixture, ProfileDirectory));
        AppBuilder.Configure<App>().UsePlatformDetect().AfterSetup(_ =>
            Dispatcher.UIThread.Post(async () =>
            {
                var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                try
                {
                    await Check((WorkbenchWindow)desktop.MainWindow!, fixture);
                    Console.WriteLine("PASS native Avalonia embedded terminal: mutable input, keyboard routing, Mod+Shift+J from the terminal, image/text paste, theme pixels, Metal rendering, shell, session retention, workspace isolation and close disposal");
                    await CheckRemote(fixture);
                    Console.WriteLine("PASS native remote terminal: Ghostty relay to an authenticated gRPC host PTY with I/O, worktree root, resize, busy foreground and exit status");
                    desktop.Shutdown(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
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

    private static nint Handle(NativeControlHost host) => (nint)host.GetType().GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static string Read(nint view)
    {
        var buffer = new byte[65536];
        var count = Native.Read(view, buffer, (nuint)buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)count);
    }

    private static async Task Check(WorkbenchWindow window, string root)
    {
        await Until(() => window.WorkspaceMounted);
        var bottom = window.Layout.State.Groups.Single(group => group.Region == "bottom").Id;
        NativeControlHost? host = null;
        await Until(() => (host = window.GetVisualDescendants().OfType<NativeControlHost>().SingleOrDefault()) is not null && Handle(host) != 0);
        var original = Handle(host!);
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
        if (Handle(host!) != original) throw new InvalidOperationException("Hiding the bottom panel replaced the shell.");
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
        Native.MutableInput(original);
        await Type(original, "\r");
        await Until(() => Read(original).Contains("MUTABLE_INPUT_OK", StringComparison.Ordinal));
        for (var format = 1; format <= 2; format++)
        {
            Native.Input(original, "image=");
            Native.Paste(original, format);
            Native.Input(original, $"; test -s \"$image\" && printf 'IMAGE_%s_OK\\n' {format}\r");
            await Until(() => Read(original).Contains($"IMAGE_{format}_OK", StringComparison.Ordinal));
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
        if (Handle(host!) != original) throw new InvalidOperationException("Moving a tab replaced the shell.");
        window.Layout.Fold(side);
        await Task.Delay(150);
        window.Layout.Fold(side);
        await Until(() => window.GetVisualDescendants().Contains(host));
        Native.Input(original, "printf '\\nRETAINED_%s\\n' \"$SHARPRAIL_SESSION\"\r");
        await Until(() => Read(original).Contains("RETAINED_original", StringComparison.Ordinal));
        var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, ".git"), "gitdir: " + Path.Combine(other, "absent"));
        await window.OpenProjectAsync(other);
        NativeControlHost? initial = null;
        await Until(() => (initial = window.GetVisualDescendants().OfType<NativeControlHost>().SingleOrDefault()) is not null && Handle(initial) != 0);
        var add = window.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "AddToGroup_" + bottom);
        add.ContextMenu!.Items.OfType<MenuItem>().First().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        NativeControlHost? second = null;
        await Until(() => (second = window.GetVisualDescendants().OfType<NativeControlHost>().SingleOrDefault()) is not null &&
            !ReferenceEquals(second, initial) && Handle(second) != 0);
        await Until(() => Native.Focused(Handle(second!)));
        await Type(Handle(second!), "printf 'NEW_%s_OK\\n' KEYBOARD\r");
        await Until(() => Read(Handle(second!)).Contains("NEW_KEYBOARD_OK", StringComparison.Ordinal));
        Native.Input(Handle(second!), $"test \"$PWD\" = '{other.Replace("'", "'\\''")}' && printf '\\nISOLATED_%s\\n' \"${{SHARPRAIL_SESSION-unset}}\"\r");
        await Until(() => Read(Handle(second!)).Contains("ISOLATED_unset", StringComparison.Ordinal));
        await window.OpenProjectAsync(root);
        window.Layout.Select(side, tab.Id);
        await Until(() => window.GetVisualDescendants().Contains(host));
        if (Handle(host!) != original) throw new InvalidOperationException("Workspace switching replaced the shell.");
        window.Width -= 100;
        await Until(() => Native.Rendered(original));
        window.Layout.Close(side, tab.Id);
        if (Handle(host!) != 0) throw new InvalidOperationException("Closing a terminal did not dispose its native session.");
        await Until(() => Native.Kill(processId, 0) == -1 && Marshal.GetLastPInvokeError() == 3);
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
            NativeControlHost? host = null;
            await Until(() => (host = window.GetVisualDescendants().OfType<NativeControlHost>().SingleOrDefault()) is not null && Handle(host) != 0);
            var view = Handle(host!);
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

    private static async Task Type(nint view, string text)
    {
        foreach (var character in text)
        {
            Native.Key(view, character.ToString(), character == '\r' ? (ushort)36 : (ushort)0, false);
            await Task.Delay(5);
        }
    }

    private static class Native
    {
        [DllImport("TerminalEvents", EntryPoint = "sr_check_paste")]
        internal static extern void Paste(nint view, int format);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_background")]
        internal static extern uint Background(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_mutable_input")]
        internal static extern void MutableInput(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_activate")]
        internal static extern void Activate(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_ready")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Ready(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_toggle_bottom")]
        internal static extern void ToggleBottom(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_click")]
        internal static extern void Click(nint view, double x, double y);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_focused")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Focused(nint view);
        [DllImport("TerminalEvents", EntryPoint = "sr_check_key")]
        internal static extern void Key(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, ushort keyCode, [MarshalAs(UnmanagedType.I1)] bool control);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int processId, int signal);
        [DllImport("SharpRailGhostty", EntryPoint = "sr_terminal_input")]
        internal static extern void Input(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
        [DllImport("SharpRailGhostty", EntryPoint = "sr_terminal_read")]
        internal static extern nuint Read(nint view, byte[] buffer, nuint capacity);
        [DllImport("SharpRailGhostty", EntryPoint = "sr_terminal_rendered")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Rendered(nint view);
    }
}
