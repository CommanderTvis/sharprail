using System.Net;
using System.Text;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// Host PTY sessions through the direct adapter and a real authenticated gRPC host.
internal static class TerminalHostChecks
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
