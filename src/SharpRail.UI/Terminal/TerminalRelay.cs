using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;

namespace SharpRail.UI.Terminal;

public sealed record RemoteTerminalConnection(Uri Endpoint, string Token, ITerminalService Terminals);

// Runs inside an embedded terminal as the child process of a remote tab, piping it to a host PTY.
public static class TerminalRelay
{
    public const string Argument = "--terminal-relay";
    // Names a private file with the endpoint and token; the token never appears in argv.
    public const string ConnectionVariable = "SHARPRAIL_TERMINAL_RELAY";
    // Prefixes the status file of a shell that exited; any other status is a start failure.
    internal const string ExitStatus = "exit:";

    internal sealed record Connection(string Endpoint, string Token, string SessionId, string WorkspaceRoot, string StatusPath);

    [UnsupportedOSPlatform("windows")]
    internal static string Write(Connection connection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharprail-relay-" + Environment.UserName);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, connection.SessionId + ".json");
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
        try { session = await terminals.StartAsync(new(connection.SessionId, connection.WorkspaceRoot, columns, rows)); }
        catch (Exception error)
        {
            var message = error is Grpc.Core.RpcException rpc ? rpc.Status.Detail : error.Message;
            await File.WriteAllTextAsync(connection.StatusPath, message);
            Report(output, "Couldn’t start the remote shell: " + message);
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
                await foreach (var chunk in session.ReadAsync())
                {
                    output.Write(chunk.Span);
                    output.Flush();
                }
                var code = await session.Exit;
                await File.WriteAllTextAsync(connection.StatusPath, ExitStatus + code);
                return code;
            }
            catch (Exception error)
            {
                raw?.Dispose();
                Report(output, "The remote terminal connection was lost: " + (error is Grpc.Core.RpcException rpc ? rpc.Status.Detail : error.Message));
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
