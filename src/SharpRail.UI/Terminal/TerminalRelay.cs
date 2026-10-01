using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

using Microsoft.Win32.SafeHandles;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;

namespace SharpRail.UI.Terminal;

// Where a relay reaches the host's terminal sessions, and the same sessions for busy checks and closing.
public sealed record RemoteTerminalConnection(Uri Endpoint, string Token, ITerminalService Terminals);

// Runs inside an embedded terminal as the child process of every tab, attaching it to a host PTY session.
public static class TerminalRelay
{
    public const string Argument = "--terminal-relay";
    // Names a private file with the endpoint and token; the token never appears in argv.
    public const string ConnectionVariable = "SHARPRAIL_TERMINAL_RELAY";
    // Prefixes the status file of a shell that exited; any other status is a start failure.
    internal const string ExitStatus = "exit:";
    // The status of a relay whose session another window or client took over.
    internal const string DetachedStatus = "detached";

    internal sealed record Connection(string Endpoint, string Token, string SessionId, string ClientId, string WorkspaceRoot, string StatusPath, string TabKey = "");

    internal static string Directory { get; } = Path.Combine(Path.GetTempPath(), "sharprail-relay-" + Environment.UserName);

    [UnsupportedOSPlatform("windows")]
    internal static string PrivateDirectory()
    {
        System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Directory;
    }

    [UnsupportedOSPlatform("windows")]
    internal static string Write(string name, Connection connection)
    {
        var path = Path.Combine(PrivateDirectory(), name + ".json");
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        JsonSerializer.Serialize(stream, connection);
        return path;
    }

    public static int Run()
    {
        var path = Environment.GetEnvironmentVariable(ConnectionVariable);
        using var output = new FileStream(new SafeFileHandle(1, false), FileAccess.Write, 1);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Report(output, "The remote terminal connection is missing.");
            return 1;
        }
        Connection connection;
        try { connection = JsonSerializer.Deserialize<Connection>(File.ReadAllBytes(path)) ?? throw new InvalidDataException(); }
        finally { File.Delete(path); }
        return RunAsync(connection, output).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(Connection connection, FileStream output)
    {
        using var terminals = new RemoteTerminalAdapter(new Uri(connection.Endpoint), connection.Token);
        if (!TerminalDevice.TryGetSize(1, out var columns, out var rows)) (columns, rows) = (80, 24);
        ITerminalSession session;
        try
        {
            session = await terminals.AttachAsync(new(connection.SessionId, connection.WorkspaceRoot, connection.ClientId, columns, rows) { TabKey = connection.TabKey });
        }
        catch (Exception error)
        {
            var message = error is Grpc.Core.RpcException rpc ? rpc.Status.Detail : error.Message;
            await File.WriteAllTextAsync(connection.StatusPath, message);
            Report(output, "Couldn’t start the shell: " + message);
            return 1;
        }
        await using (session)
        {
            using var raw = TerminalDevice.EnterRawMode(0);
            using var resize = PosixSignalRegistration.Create((PosixSignal)28, signal =>
            {
                if (TerminalDevice.TryGetSize(1, out var width, out var height)) _ = session.ResizeAsync(width, height).AsTask();
            });
            var input = new Thread(() => Pump(session)) { IsBackground = true, Name = "SharpRail relay input" };
            input.Start();
            try
            {
                output.Write(session.Replay.Span);
                output.Flush();
                // A revived agent's command is typed once the shell has printed something, so it never lands before the prompt.
                var prefill = session.Prefill;
                await foreach (var chunk in session.ReadAsync())
                {
                    output.Write(chunk.Span);
                    output.Flush();
                    if (prefill is not null && chunk.Length > 0)
                    {
                        await session.WriteAsync(Encoding.UTF8.GetBytes(prefill.Text + (prefill.Submit ? "\r" : "")));
                        prefill = null;
                    }
                }
                if (session.Detached.IsCompleted)
                {
                    await File.WriteAllTextAsync(connection.StatusPath, DetachedStatus);
                    return 0;
                }
                var code = await session.Exit;
                await File.WriteAllTextAsync(connection.StatusPath, ExitStatus + code);
                return code;
            }
            catch (Exception error)
            {
                raw?.Dispose();
                var message = error is Grpc.Core.RpcException rpc ? "The terminal connection was lost: " + rpc.Status.Detail : error.Message;
                await File.WriteAllTextAsync(connection.StatusPath, message);
                Report(output, message);
                return 1;
            }
        }
    }

    private static void Pump(ITerminalSession session)
    {
        using var input = new FileStream(new SafeFileHandle(0, false), FileAccess.Read, 1);
        var buffer = new byte[16 * 1024];
        try
        {
            int count;
            while ((count = input.Read(buffer)) > 0) session.WriteAsync(buffer.AsMemory(0, count).ToArray()).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private static void Report(FileStream output, string message)
    {
        output.Write(Encoding.UTF8.GetBytes("\r\n" + message + "\r\n"));
        output.Flush();
    }
}