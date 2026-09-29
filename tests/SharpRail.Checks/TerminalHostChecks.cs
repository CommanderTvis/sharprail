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
            await Exercise(new LocalTerminalAdapter(local), workspace, "local");

        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "terminal-token");
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using (var remote = new RemoteTerminalAdapter(address, "terminal-token"))
                await Exercise(remote, workspace, "remote");
            using var rejected = new RemoteTerminalAdapter(address, "wrong-token");
            try
            {
                await using var session = await rejected.StartAsync(new("rejected", workspace));
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
        Console.WriteLine("PASS host terminals: local/remote PTY echo, worktree root, controlling tty, UTF-8, resize, busy foreground, exit status, disposal, start failure and auth rejection");
    }

    private static async Task Exercise(ITerminalService terminals, string workspace, string mode)
    {
        try
        {
            await using var missing = await terminals.StartAsync(new(mode + "-missing", Path.Combine(workspace, "absent")));
            throw new InvalidOperationException($"{mode}: a terminal started in a missing folder.");
        }
        catch (IOException error) when (error.Message.Contains("absent", StringComparison.Ordinal)) { }

        var id = mode + "-" + Guid.NewGuid().ToString("N");
        var session = await terminals.StartAsync(new(id, workspace, 100, 30));
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
            await session.WriteAsync("\u0003"u8.ToArray());
            await Until(async () => !await terminals.IsBusyAsync(id), mode + " interrupted foreground");
            await screen.Run("printf 'PID_%s_\\n' \"$$\"");
            var pid = int.Parse(await screen.WaitForMatch(@"PID_(\d+)_", mode));
            await screen.Run("printf 'FINAL_%s\\n' OUTPUT; exit 7");
            Require(await session.Exit.WaitAsync(TimeSpan.FromSeconds(20)) == 7, $"{mode}: the exit status was not reported.");
            await screen.Completed;
            Require(screen.Text.Contains("FINAL_OUTPUT", StringComparison.Ordinal), $"{mode}: final output was lost before exit.");
            Require(!await terminals.IsBusyAsync(id), $"{mode}: an exited shell reported a busy foreground.");
            Require(!Running(pid), $"{mode}: the exited shell is still running.");
        }
        finally { await session.DisposeAsync(); }

        var disposed = await terminals.StartAsync(new(mode + "-dispose", workspace));
        var second = new Screen(disposed);
        await second.Run("printf 'PID_%s_\\n' \"$$\"");
        var shell = int.Parse(await second.WaitForMatch(@"PID_(\d+)_", mode));
        await disposed.DisposeAsync();
        await Until(() => Task.FromResult(!Running(shell)), mode + " disposal ends the shell");
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
            Completed = Task.Run(async () =>
            {
                try { await foreach (var chunk in session.ReadAsync()) lock (text) text.Append(Encoding.UTF8.GetString(chunk.Span)); }
                catch (Exception) when (session.Exit.IsFaulted || session.Exit.IsCanceled) { }
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
