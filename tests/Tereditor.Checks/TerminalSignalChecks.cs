using System.Collections.Concurrent;
using System.Net;
using System.Text;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// Programs' signals in a terminal's live output (OSC 9;4 progress, OSC 9 and OSC 777 notifications, BEL) reach every
// client of the host, locally and over RPC, and a shell's exit removes its progress.
internal static partial class TerminalHostChecks
{
    /// <summary>The signal checks alone, in a shell without the user's startup files.</summary>
    internal static async Task Signals(string root)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) { Console.WriteLine("SKIP terminal signals: PTYs require macOS or Linux."); return; }
        var workspace = Directory.CreateDirectory(Path.Combine(root, "terminal signals")).FullName;
        var previous = Environment.GetEnvironmentVariable("ZDOTDIR");
        Environment.SetEnvironmentVariable("ZDOTDIR", Directory.CreateDirectory(Path.Combine(root, "terminal-signals-startup")).FullName);
        try { await Signals(root, workspace); }
        finally { Environment.SetEnvironmentVariable("ZDOTDIR", previous); }
    }

    private static async Task Signals(string root, string workspace)
    {
        SignalParsing();
        await SignalTimeout();
        await using var local = new PtyTerminalService();
        await SignalStream(new LocalTerminalCatalogAdapter(local), new LocalTerminalAdapter(local), workspace, "local");
        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "signal-token", terminals: local);
        await app.StartAsync();
        try
        {
            using var catalog = new RemoteTerminalCatalogAdapter(Address(app), "signal-token");
            using var terminals = new RemoteTerminalAdapter(Address(app), "signal-token");
            await SignalStream(catalog, terminals, workspace, "remote");
        }
        finally { await app.StopAsync(); }
        try
        {
            await foreach (var _ in ((ITerminalCatalogService)new MemoryTerminalCatalog()).WatchSignalsAsync()) break;
            throw new InvalidOperationException("A catalog without shells reported signals.");
        }
        catch (NotSupportedException) { }
        Console.WriteLine("PASS terminal signals: OSC 9;4 progress, OSC 9/777 notifications and bells, local and remote, cleared on exit and timeout");
    }

    private static void SignalParsing()
    {
        var now = TimeSpan.Zero;
        var parser = new TerminalSignalParser(() => now);
        var seen = new List<TerminalSignal>();
        void Feed(string text) => parser.Scan(Encoding.UTF8.GetBytes(text), "s", seen.Add);
        TerminalSignal Single(string text, string what)
        {
            seen.Clear(); Feed(text);
            Require(seen.Count == 1, $"{what}: expected one signal, saw {seen.Count}.");
            return seen[0];
        }
        void None(string text, string what) { seen.Clear(); Feed(text); Require(seen.Count == 0, $"{what}: expected no signal, saw {string.Join(", ", seen)}."); }

        Require(Single("\x1b]9;4;1;42\x07", "normal") is { Kind: TerminalSignalKind.Progress, Progress: TerminalProgressState.Normal, Percent: 42 }, "OSC 9;4;1 carries its percent.");
        Require(Single("\x1b]9;4;2;250\x1b\\", "error") is { Progress: TerminalProgressState.Error, Percent: 100 }, "A percent is clamped and ST terminates.");
        Require(Single("\x1b]9;4;3\x07", "indeterminate") is { Progress: TerminalProgressState.Indeterminate, Percent: -1 }, "Indeterminate has no percent.");
        Require(Single("\x1b]9;4;4;60\x07", "paused") is { Progress: TerminalProgressState.Paused, Percent: 60 }, "Paused keeps its percent.");
        Require(Single("\x1b]9;4;0;30\x07", "remove") is { Progress: TerminalProgressState.None, Percent: -1 }, "State 0 removes the bar.");
        None("\x1b]9;4;7;1\x07", "an unknown state");
        seen.Clear();
        foreach (var piece in new[] { "\x1b", "]9;4", ";1;", "7", "\x1b", "\\" }) Feed(piece);
        Require(seen is [{ Progress: TerminalProgressState.Normal, Percent: 7 }], "A sequence split across reads still parses.");
        Require(Single("\x1b]9;Build finished\x07", "osc 9") is { Kind: TerminalSignalKind.Notification, Title: "", Body: "Build finished" }, "OSC 9 is an iTerm2 notification.");
        None("\x1b]9;1;500\x07", "ConEmu sleep");
        None("\x1b]9;12\x07", "ConEmu prompt mark");
        Require(Single("\x1b]777;notify;Tests;All 12 passed; 0 failed\x1b\\", "osc 777") is { Title: "Tests", Body: "All 12 passed; 0 failed" }, "OSC 777 notify carries a title and the rest as body.");
        None("\x1b]777;other;x\x07\x1b]0;title\x07", "another OSC");
        Require(Single("\x1b]99;;Hello\x1b\\", "osc 99") is { Kind: TerminalSignalKind.Notification, Title: "Hello", Body: "" }, "OSC 99 defaults to a title.");
        None("\x1b]99;i=7:d=0;Build\x1b\\", "an unfinished OSC 99");
        Require(Single("\x1b]99;i=7:p=body:e=1;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("ok ✓")) + "\x1b\\", "osc 99 body") is { Title: "Build", Body: "ok ✓" },
            "OSC 99 chunks join by identifier and base64 payloads decode.");
        None("\x1b]99;i=8:p=?;\x1b\\\x1b]99;p=icon;x\x1b\\", "OSC 99 queries and icons");
        Require(Single("plain\x07text", "bell") is { Kind: TerminalSignalKind.Bell }, "A lone BEL rings.");
        None("\x07", "a bell within 100 ms of the last");
        now = TimeSpan.FromSeconds(1);
        None("\x1bPq#0;2;0;0;0\x07#1\x1b\\\x1b_Gi=1\x07\x1b\\", "BEL inside DCS and APC strings");
        Require(Single("\x07", "bell later") is { Kind: TerminalSignalKind.Bell }, "A bell after the interval rings again.");
        Require(Single("\x1b" + "c", "reset") is { Kind: TerminalSignalKind.Progress, Progress: TerminalProgressState.None }, "A full reset removes the progress, as in Ghostty.");
        None("\x1b]9;" + new string('x', 5000) + "\x07", "an oversized OSC");
    }

    private static async Task SignalTimeout()
    {
        using var hub = new TerminalSignalHub(TimeSpan.FromMilliseconds(300));
        var seen = new ConcurrentQueue<TerminalSignal>();
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            try { await foreach (var signal in hub.WatchAsync(watching.Token)) seen.Enqueue(signal); }
            catch (OperationCanceledException) { }
        });
        await Task.Delay(50);
        hub.Publish(new("t", TerminalSignalKind.Progress) { Progress = TerminalProgressState.Indeterminate });
        await Until(() => Task.FromResult(seen.Any(signal => signal.Progress == TerminalProgressState.None)), "a progress without a further report is removed after its timeout");
        hub.End("t");
        await Task.Delay(100);
        Require(seen.Count(signal => signal.Progress == TerminalProgressState.None) == 1, "Ending a session without progress broadcasts nothing.");
        watching.Cancel();
        await watcher;
    }

    private static async Task SignalStream(ITerminalCatalogService catalog, ITerminalService terminals, string workspace, string mode)
    {
        var id = mode + "-signals-" + Guid.NewGuid().ToString("N");
        var seen = new ConcurrentQueue<TerminalSignal>();
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            try { await foreach (var signal in catalog.WatchSignalsAsync(watching.Token)) if (signal.SessionId == id) seen.Enqueue(signal); }
            catch (Exception) when (watching.IsCancellationRequested) { }
        });
        var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
        var screen = new Screen(session);
        async Task<TerminalSignal> Expect(string command, Func<TerminalSignal, bool> wanted, string what)
        {
            var before = seen.Count;
            await session.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"));
            TerminalSignal? found = null;
            await Until(() => Task.FromResult((found = seen.Skip(before).FirstOrDefault(wanted)) is not null), $"{mode}: {what}");
            return found!;
        }
        await Expect("printf '\\033]9;4;1;42\\007'", signal => signal is { Progress: TerminalProgressState.Normal, Percent: 42 }, "normal progress with a percent");
        await Expect("printf '\\033]9;4;2;80\\033\\\\'", signal => signal is { Progress: TerminalProgressState.Error, Percent: 80 }, "error progress");
        await Expect("printf '\\033]9;4;4\\007'", signal => signal is { Progress: TerminalProgressState.Paused, Percent: -1 }, "paused progress");
        await Expect("printf '\\033]9;4;3\\007'", signal => signal is { Progress: TerminalProgressState.Indeterminate }, "indeterminate progress");

        // A client that subscribes later learns the progress showing now before anything else.
        using (var late = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            await foreach (var signal in catalog.WatchSignalsAsync(late.Token))
                if (signal.SessionId == id)
                {
                    Require(signal is { Kind: TerminalSignalKind.Progress, Progress: TerminalProgressState.Indeterminate }, $"{mode}: a new watcher first receives the current progress, not {signal}.");
                    break;
                }

        await Expect("printf '\\033]9;4;0\\007'", signal => signal is { Kind: TerminalSignalKind.Progress, Progress: TerminalProgressState.None }, "removed progress");
        await Expect("printf '\\033]9;Deploy done\\007'", signal => signal is { Kind: TerminalSignalKind.Notification, Body: "Deploy done" }, "an OSC 9 notification");
        await Expect("printf '\\033]777;notify;Tests;All passed\\033\\\\'", signal => signal is { Kind: TerminalSignalKind.Notification, Title: "Tests", Body: "All passed" }, "an OSC 777 notification");
        await Expect("printf '\\a'", signal => signal.Kind == TerminalSignalKind.Bell, "a bell");
        await Until(() => Task.FromResult(screen.Text.Contains('\a')), $"{mode}: output still carries the bell; signals are read beside it");

        await Expect("printf '\\033]9;4;1;5\\007'", signal => signal is { Progress: TerminalProgressState.Normal, Percent: 5 }, "progress before exit");
        var before = seen.Count;
        await session.WriteAsync("exit\n"u8.ToArray());
        await session.Exit;
        await Until(() => Task.FromResult(seen.Skip(before).Any(signal => signal is { Kind: TerminalSignalKind.Progress, Progress: TerminalProgressState.None })), $"{mode}: the shell's exit removes its progress");
        await session.DisposeAsync();
        await terminals.CloseAsync(id);
        watching.Cancel();
        await watcher;
    }
}