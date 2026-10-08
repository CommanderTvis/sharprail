using System.Net;
using System.Text;

using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// Host PTY sessions through the direct adapter and a real authenticated gRPC host.
internal static partial class TerminalHostChecks
{
    public static async Task Run(string root)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Console.WriteLine("SKIP host terminal checks: PTYs require macOS or Linux.");
            return;
        }
        var workspace = Path.Combine(root, "terminal host");
        Directory.CreateDirectory(workspace);
        // Keep the login shell but not the user's startup files, which can consume typed-ahead input.
        var startup = Path.Combine(root, "terminal-startup");
        Directory.CreateDirectory(startup);
        var previous = Environment.GetEnvironmentVariable("ZDOTDIR");
        Environment.SetEnvironmentVariable("ZDOTDIR", startup);
        try { await RunIsolated(root, workspace); }
        finally { Environment.SetEnvironmentVariable("ZDOTDIR", previous); }
    }

    private static async Task RunIsolated(string root, string workspace)
    {
        await using (var local = new PtyTerminalService())
        {
            await Exercise(new LocalTerminalAdapter(local), workspace, "local");
            await Sessions(new LocalTerminalAdapter(local), workspace, "local");
        }

        await RevivalLocal(root, workspace);
        await RevivalRemote(root, workspace);
        RevivalRecorder();
        await Limits(root, workspace);

        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "terminal-token");
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using (var remote = new RemoteTerminalAdapter(address, "terminal-token"))
            {
                await Exercise(remote, workspace, "remote");
                await Sessions(remote, workspace, "remote");
            }
            await Reconnects(address, workspace);
            using var rejected = new RemoteTerminalAdapter(address, "wrong-token");
            try
            {
                await using var session = await rejected.AttachAsync(new("rejected", workspace, "client"));
                throw new InvalidOperationException("The remote terminal accepted a bad token.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
            try
            {
                await rejected.IsBusyAsync("rejected");
                throw new InvalidOperationException("The remote terminal busy query accepted a bad token.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS host terminals: local/remote PTY echo, worktree root, controlling tty, UTF-8, resize, busy foreground, exit status, detach and close, start failure and auth rejection");
    }

    private static async Task<(ITerminalSession Session, Screen Screen)> Open(ITerminalService terminals, string id, string workspace)
    {
        var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
        return (session, new Screen(session));
    }

    private static async Task RevivalLocal(string root, string workspace)
    {
        var directory = Path.Combine(root, "revival-local");
        var id = "revive-" + Guid.NewGuid().ToString("N");
        var closed = "closed-" + Guid.NewGuid().ToString("N");
        var absent = Path.Combine(workspace, "revival-absent");
        await using (var first = new PtyTerminalService(recordingsDirectory: directory))
        {
            var (session, screen) = await Open(first, id, workspace);
            await screen.Run("printf 'MARK_%s\\n' ONE");
            await screen.WaitFor("MARK_ONE", "revival");
            await screen.Run("printf '\\033[?%s' 1049h; printf 'ALT_%s' HIDDEN; printf '\\033[?%s' 1000h; printf '\\033[?%s' 1049l; printf '\\033[?%s' 1000l; printf 'MARK_%s\\n' TWO");
            await screen.WaitFor("MARK_TWO", "revival");
            var (_, other) = await Open(first, closed, workspace);
            await other.Run("printf 'GONE_%s\\n' X");
            await other.WaitFor("GONE_X", "revival");
            await first.CloseAsync(closed);
            await session.DisposeAsync();
        }
        Require(!Directory.EnumerateFiles(directory).Any(file => Path.GetFileName(file).StartsWith(Convert.ToHexString(Encoding.UTF8.GetBytes(closed)), StringComparison.Ordinal)),
            "Closing a tab must remove its recording.");
        Require(Directory.EnumerateFiles(directory, "*.rec").Count() == 1, "Only the open tab should leave a recording.");

        // A failed spawn keeps the recording for the retry.
        await using (var second = new PtyTerminalService(recordingsDirectory: directory))
        {
            try { await second.AttachAsync(new(id, absent, "client")); throw new InvalidOperationException("A terminal started in a missing folder."); }
            catch (IOException) { }
            var (session, screen) = await Open(second, id, workspace);
            Require(session.Created, "A revived tab starts a new shell.");
            var text = Encoding.UTF8.GetString(session.Replay.Span);
            Require(text.Contains("MARK_ONE", StringComparison.Ordinal) && text.Contains("MARK_TWO", StringComparison.Ordinal), "The revived replay lacks the recorded output: " + text);
            Require(!text.Contains("ALT_HIDDEN", StringComparison.Ordinal) && !text.Contains("?1049", StringComparison.Ordinal) && !text.Contains("?1000", StringComparison.Ordinal),
                "Alternate screen output and mouse modes must not be revived: " + text);
            await screen.Run("printf 'NEW_%s\\n' SHELL");
            await screen.WaitFor("NEW_SHELL", "revival");
            await session.DisposeAsync();
        }

        // Corrupt, oversized and surplus files are ignored.
        var junk = Path.Combine(root, "revival-junk");
        Directory.CreateDirectory(junk);
        File.WriteAllBytes(Path.Combine(junk, "not-hex.rec"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(junk, Convert.ToHexString("big"u8) + ".rec"), new byte[TerminalRecordingStore.MaxBytes + 1]);
        var store = new TerminalRecordingStore(junk);
        for (var index = 0; index < TerminalRecordingStore.MaxEntries + 10; index++) store.Save("s" + index, "x"u8.ToArray());
        Require(store.LoadAll().Count == TerminalRecordingStore.MaxEntries, "The recording store must honour its entry cap.");
        Require(store.Load("big") is null, "An oversized recording must be ignored.");
        Require(new TerminalRecordingStore(Path.Combine(root, "revival-none")).LoadAll().Count == 0, "A missing store must be empty.");
        Console.WriteLine("PASS host terminal revival (local): recorded screen shown by a new shell, close removes it, failed start keeps it, alternate screen and mouse modes dropped, corrupt/oversized/surplus files ignored");
    }

    private static async Task RevivalRemote(string root, string workspace)
    {
        var state = Path.Combine(root, "revival-remote");
        var id = "revive-remote-" + Guid.NewGuid().ToString("N");
        async Task<WebApplication> Start() { var host = RemoteServer.Create(root, IPAddress.Loopback, 0, "revive-token", state); await host.StartAsync(); return host; }
        Uri Address(WebApplication host) => new(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var app = await Start();
        try
        {
            using var remote = new RemoteTerminalAdapter(Address(app), "revive-token");
            var (session, screen) = await Open(remote, id, workspace);
            await screen.Run("printf 'REMOTE_%s\\n' MARK");
            await screen.WaitFor("REMOTE_MARK", "remote revival");
            await session.DisposeAsync();
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
        app = await Start();
        try
        {
            using var remote = new RemoteTerminalAdapter(Address(app), "revive-token");
            var (session, screen) = await Open(remote, id, workspace);
            Require(session.Created && Encoding.UTF8.GetString(session.Replay.Span).Contains("REMOTE_MARK", StringComparison.Ordinal), "A restarted host must revive the recorded screen.");
            await screen.Run("printf 'AGAIN_%s\\n' OK");
            await screen.WaitFor("AGAIN_OK", "remote revival");
            await session.DisposeAsync();
            await remote.CloseAsync(id);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
        Console.WriteLine("PASS host terminal revival (remote): a stopped and restarted host over one state directory revives the tab with a fresh shell");
    }

    private static void RevivalRecorder()
    {
        var recorder = new TerminalRecorder();
        recorder.Push("\x1b[?25l\x1b[?2004hhello\r\nworld\r\n"u8);
        var snapshot = recorder.Snapshot();
        var restored = new TerminalRecorder();
        restored.Restore(snapshot);
        Require(restored.Snapshot().AsSpan().SequenceEqual(snapshot), "Restore then Snapshot must round-trip without duplicating the preamble.");
        restored.Restore(restored.Snapshot());
        Require(Count(Encoding.UTF8.GetString(restored.Snapshot()), "\x1b[?2004h") == 1, "The mode preamble must not be duplicated.");
        var big = new TerminalRecorder();
        big.Restore(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line of output\n", 20_000))));
        Require(big.Snapshot().Length <= TerminalRecorder.SnapshotBytes + 64, "Restored bytes must be capped.");
        Console.WriteLine("PASS terminal recorder restore: snapshot round-trips, modes re-parsed, size capped");
    }

    private static async Task Exercise(ITerminalService terminals, string workspace, string mode)
    {
        try
        {
            await using var missing = await terminals.AttachAsync(new(mode + "-missing", Path.Combine(workspace, "absent"), "client"));
            throw new InvalidOperationException($"{mode}: a terminal started in a missing folder.");
        }
        catch (IOException error) when (error.Message.Contains("absent", StringComparison.Ordinal)) { }

        var id = mode + "-" + Guid.NewGuid().ToString("N");
        var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
        var screen = new Screen(session);
        try
        {
            await screen.Run("printf 'TR_%s\\n' ECHO");
            await screen.WaitFor("TR_ECHO", mode);
            await screen.Run("printf 'PWD_%s\\n' \"$(pwd -P)\"");
            await screen.WaitFor("PWD_" + workspace, mode);
            await screen.Run("exec 3</dev/tty && printf 'CTTY_%s\\n' OK");
            await screen.WaitFor("CTTY_OK", mode);
            await screen.Run("printf 'LEN_%s\\n' \"$(printf %s привет | wc -m | tr -d ' ')\"");
            await screen.WaitFor("LEN_6", mode);
            await screen.Run("printf 'SIZE_%s\\n' \"$(stty size | tr ' ' x)\"");
            await screen.WaitFor("SIZE_30x100", mode);
            await session.ResizeAsync(120, 40);
            await screen.Run("printf 'RESIZED_%s\\n' \"$(stty size | tr ' ' x)\"");
            await screen.WaitFor("RESIZED_40x120", mode);
            await screen.Run("printf 'TOKEN_%s\\n' \"${SHARPRAIL_TOKEN-unset}\"");
            await screen.WaitFor("TOKEN_unset", mode);
            Require(!await terminals.IsBusyAsync(id), $"{mode}: an idle shell reported a busy foreground.");
            await screen.Run("sleep 30");
            await Until(async () => await terminals.IsBusyAsync(id), mode + " busy foreground");
            // An interrupt sent while the shell is still handing the terminal to its child can be lost; resend it.
            var interrupted = DateTime.MinValue;
            await Until(async () =>
            {
                if (!await terminals.IsBusyAsync(id)) return true;
                if (DateTime.UtcNow - interrupted > TimeSpan.FromMilliseconds(500))
                {
                    await session.WriteAsync("\u0003"u8.ToArray());
                    interrupted = DateTime.UtcNow;
                }
                return false;
            }, mode + " interrupted foreground");
            await screen.Run("printf 'PID_%s_\\n' \"$$\"");
            var pid = int.Parse(await screen.WaitForMatch(@"PID_(\d+)_", mode));
            await screen.Run("printf 'FINAL_%s\\n' OUTPUT; exit 7");
            Require(await session.Exit.WaitAsync(TimeSpan.FromSeconds(20)) == 7, $"{mode}: the exit status was not reported.");
            await screen.Completed;
            Require(screen.Text.Contains("FINAL_OUTPUT", StringComparison.Ordinal), $"{mode}: final output was lost before exit.");
            Require(!await terminals.IsBusyAsync(id), $"{mode}: an exited shell reported a busy foreground.");
            Require(!Running(pid), $"{mode}: the exited shell is still running.");
        }
        finally { await session.DisposeAsync(); await terminals.CloseAsync(id); }

        // Detaching leaves the shell running on the host; closing the session ends it.
        var closing = mode + "-close-" + Guid.NewGuid().ToString("N");
        var detached = await terminals.AttachAsync(new(closing, workspace, "client"));
        var second = new Screen(detached);
        await second.Run("printf 'PID_%s_\\n' \"$$\"");
        var shell = int.Parse(await second.WaitForMatch(@"PID_(\d+)_", mode));
        await detached.DisposeAsync();
        await Task.Delay(300);
        Require(Running(shell), $"{mode}: detaching ended the shell.");
        await terminals.CloseAsync(closing);
        await Until(() => Task.FromResult(!Running(shell)), mode + " closing ends the shell");
    }

    // Host-owned sessions: exactly-once replay, takeover, displaced input, resume and exit while detached.
    private static async Task Sessions(ITerminalService terminals, string workspace, string mode)
    {
        var id = mode + "-shared-" + Guid.NewGuid().ToString("N");
        var first = await terminals.AttachAsync(new(id, workspace, "window-a", 100, 30));
        Require(first.Created, $"{mode}: the first attach must start the shell.");
        var a = new Screen(first);
        await a.Run("printf 'ONCE_%s\\n' MARK");
        await a.WaitFor("ONCE_MARK", mode);
        await first.DisposeAsync();

        var second = await terminals.AttachAsync(new(id, workspace, "window-b", 100, 30));
        var b = new Screen(second);
        Require(!second.Created && Count(b.Text, "ONCE_MARK") == 1, $"{mode}: a reattach must replay earlier output exactly once, without a second shell.");

        var third = await terminals.AttachAsync(new(id, workspace, "window-a", 100, 30));
        var c = new Screen(third);
        await second.Detached.WaitAsync(TimeSpan.FromSeconds(10));
        await b.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        await b.Run("printf 'GHOST_%s\\n' INPUT");
        await c.Run("printf 'LIVE_%s\\n' INPUT");
        await c.WaitFor("LIVE_INPUT", mode);
        Require(!c.Text.Contains("GHOST_INPUT", StringComparison.Ordinal) && !b.Text.Contains("LIVE_INPUT", StringComparison.Ordinal),
            $"{mode}: a displaced client must neither drive the shell nor receive its output.");

        await using var displaced = await terminals.AttachAsync(new(id, workspace, "window-b", 100, 30, second.Position));
        _ = new Screen(displaced);
        await displaced.Detached.WaitAsync(TimeSpan.FromSeconds(10));
        await c.Run("printf 'STILL_%s\\n' OWNED");
        await c.WaitFor("STILL_OWNED", mode);
        Require(!third.Detached.IsCompleted, $"{mode}: a displaced client's resume must not take the session back.");

        var seen = third.Position;
        await c.Run("sleep 1; printf 'MISSED_%s\\n' WHILE_AWAY");
        await Task.Delay(300);
        await third.DisposeAsync();
        await Task.Delay(1500);
        var resumed = await terminals.AttachAsync(new(id, workspace, "window-a", 100, 30, seen));
        var r = new Screen(resumed);
        await r.WaitFor("MISSED_WHILE_AWAY", mode);
        Require(!r.Text.Contains("STILL_OWNED", StringComparison.Ordinal) && Count(r.Text, "MISSED_WHILE_AWAY") == 1,
            $"{mode}: a resume must replay only the output its client missed. Screen: {r.Text}");

        await r.Run("sleep 1; printf 'LAST_%s\\n' WORDS; exit 5");
        await Task.Delay(300);
        await resumed.DisposeAsync();
        await Task.Delay(2000);
        Require(!await terminals.IsBusyAsync(id), $"{mode}: an exited shell reported a busy foreground.");
        var late = await terminals.AttachAsync(new(id, workspace, "window-c", 100, 30));
        var l = new Screen(late);
        Require(await late.Exit.WaitAsync(TimeSpan.FromSeconds(10)) == 5 && !late.Created, $"{mode}: a shell that exited while detached must report its exit, not start again.");
        await l.Completed;
        Require(l.Text.Contains("LAST_WORDS", StringComparison.Ordinal), $"{mode}: final output of a shell that exited while detached was lost.");
        await late.DisposeAsync();
        await terminals.CloseAsync(id);
        await using var fresh = await terminals.AttachAsync(new(id, workspace, "window-a", 100, 30));
        Require(fresh.Created, $"{mode}: a closed session's id must start a new shell.");
        await terminals.CloseAsync(id);
        Console.WriteLine($"PASS host terminal sessions ({mode}): detach keeps the shell, exactly-once replay, takeover with notice, displaced input ignored, resume without duplicates and exit while detached");
    }

    // A remote session reconnects through a severed connection and resumes without duplicating output,
    // and final output still precedes the exit status after the host's exit went unseen.
    private static async Task Reconnects(Uri address, string workspace)
    {
        await using var proxy = new TcpProxy(address);
        using var terminals = new RemoteTerminalAdapter(proxy.Address, "terminal-token");
        var id = "reconnect-" + Guid.NewGuid().ToString("N");
        var session = await terminals.AttachAsync(new(id, workspace, "window-a", 100, 30));
        var screen = new Screen(session);
        await screen.Run("printf 'BEFORE_%s\\n' DROP");
        await screen.WaitFor("BEFORE_DROP", "reconnect");
        proxy.Sever();
        // Input sent before the client notices the drop can be lost with the connection, as with ssh.
        await Until(() => Task.FromResult(proxy.Connections > 1), "reconnect");
        await Task.Delay(500);
        await screen.Run("printf 'AFTER_%s\\n' DROP");
        await screen.WaitFor("AFTER_DROP", "reconnect");
        Require(proxy.Connections > 1 && Count(screen.Text, "BEFORE_DROP") == 1, "A reconnect must resume without replaying output twice.");
        await screen.Run("sleep 1; printf 'FINAL_%s\\n' DURING_DROP; exit 7");
        await Task.Delay(200);
        var release = proxy.Hold();
        proxy.Sever();
        await Task.Delay(2000);
        release.SetResult();
        Require(await session.Exit.WaitAsync(TimeSpan.FromSeconds(20)) == 7, "The exit status after a reconnect was not reported.");
        await screen.Completed;
        Require(Count(screen.Text, "FINAL_DURING_DROP") == 1, "Final output must be delivered once before the exit after a reconnect.");
        await session.DisposeAsync();
        await terminals.CloseAsync(id);
        Console.WriteLine("PASS host terminal reconnects: a severed gRPC connection resumes the same shell without duplicates, and output written while disconnected precedes the exit status");
    }

    private static int Count(string text, string marker)
    {
        var count = 0;
        for (var index = text.IndexOf(marker, StringComparison.Ordinal); index >= 0; index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static bool Running(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<Task<bool>> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Terminal condition timed out: " + description);
            await Task.Delay(50);
        }
    }

    private sealed class Screen
    {
        private readonly StringBuilder text = new();
        private readonly ITerminalSession session;
        public Task Completed { get; }
        public string Text { get { lock (text) return text.ToString(); } }

        public Screen(ITerminalSession session)
        {
            this.session = session;
            text.Append(Encoding.UTF8.GetString(session.Replay.Span));
            Completed = Task.Run(async () =>
            {
                try { await foreach (var chunk in session.ReadAsync()) lock (text) text.Append(Encoding.UTF8.GetString(chunk.Span)); }
                catch (Exception) when (session.Exit.IsFaulted || session.Exit.IsCanceled || session.Detached.IsCompleted) { }
            });
        }

        public async Task Run(string command) => await session.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"));

        public async Task WaitFor(string marker, string mode) =>
            await Until(() => Task.FromResult(Text.Contains(marker, StringComparison.Ordinal)), $"{mode} output {marker}; screen: {Text}");

        public async Task<string> WaitForMatch(string pattern, string mode)
        {
            await Until(() => Task.FromResult(System.Text.RegularExpressions.Regex.IsMatch(Text, pattern)), $"{mode} output {pattern}");
            return System.Text.RegularExpressions.Regex.Match(Text, pattern).Groups[1].Value;
        }
    }
}