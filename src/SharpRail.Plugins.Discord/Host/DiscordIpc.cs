using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Discord.Host;

/// <param name="Details">The line above the project; absent when there is no file name to show.</param>
internal sealed record DiscordActivity(string? Details, string State, long StartedAt);

/// <summary>Discord's local IPC protocol: a handshake, then length-prefixed JSON frames over a Unix socket.</summary>
internal sealed class DiscordIpc : IDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private const int HeaderBytes = 8;
    private const int MaxFrameBytes = 64 * 1024;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(4);
    private static readonly Lazy<string?> DarwinUserTempDir = new(ResolveDarwinUserTempDir);

    private readonly Lock gate = new();
    private Socket? socket;
    private Action? onClose;

    public string? LastError { get; set; }
    public bool Connected { get { lock (gate) return socket is not null; } }

    // Discord's socket lives under the per-user temp directory, which the host process may not have in its
    // environment (an app launched outside a login shell), so the OS is asked as well before falling back to /tmp.
    private static IEnumerable<string> CandidateDirectories()
    {
        if (Environment.GetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR") is { Length: > 0 } overridden) return [overridden];
        string?[] bases = [Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Environment.GetEnvironmentVariable("TMPDIR"),
            Environment.GetEnvironmentVariable("TMP"), Environment.GetEnvironmentVariable("TEMP"), DarwinUserTempDir.Value, "/tmp"];
        return bases.OfType<string>().Where(dir => dir.Length > 0)
            .SelectMany(dir => new[] { dir, Path.Combine(dir, "app", "com.discordapp.Discord"), Path.Combine(dir, "snap.discord") });
    }

    private static string? ResolveDarwinUserTempDir()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("/usr/bin/getconf", "DARWIN_USER_TEMP_DIR") { RedirectStandardOutput = true, UseShellExecute = false });
            if (probe is null) return null;
            if (!probe.WaitForExit(2000)) { probe.Kill(); return null; }
            if (probe.ExitCode != 0) return null;
            var output = probe.StandardOutput.ReadToEnd().Trim();
            return output.Length > 0 ? output : null;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { return null; }
    }

    public static IReadOnlyList<string> SocketCandidates() =>
        [.. CandidateDirectories().SelectMany(dir => Enumerable.Range(0, 10).Select(index => Path.Combine(dir, $"discord-ipc-{index}"))).Distinct()];

    public async Task ConnectAsync(string applicationId, Action closed)
    {
        var paths = SocketCandidates().Where(File.Exists).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("Discord is not running on this machine.");
        Exception? last = null;
        foreach (var path in paths)
        {
            try
            {
                await HandshakeAsync(path, applicationId);
                lock (gate) onClose = closed;
                return;
            }
            catch (InvalidOperationException error)
            {
                last = error;
                Close();
            }
        }
        throw last ?? new InvalidOperationException("Could not reach Discord.");
    }

    private async Task HandshakeAsync(string path, string applicationId)
    {
        var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        lock (gate) socket = connection;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await connection.ConnectAsync(new UnixDomainSocketEndPoint(path)).WaitAsync(ReadyTimeout); }
        catch (Exception error) when (error is SocketException or TimeoutException) { throw new InvalidOperationException("Could not reach Discord.", error); }
        _ = ReadAsync(connection, ready);
        Send(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = applicationId });
        try { await ready.Task.WaitAsync(ReadyTimeout); }
        catch (TimeoutException) { throw new InvalidOperationException("Discord did not answer the handshake."); }
    }

    private async Task ReadAsync(Socket connection, TaskCompletionSource ready)
    {
        var pending = new List<byte>();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await connection.ReceiveAsync(buffer, SocketFlags.None);
                if (read == 0) break;
                pending.AddRange(buffer.AsSpan(0, read));
                foreach (var (op, payload) in Drain(pending))
                {
                    if (op == OpPing) Send(OpPong, payload);
                    if (op == OpClose)
                    {
                        ready.TrySetException(new InvalidOperationException("Discord closed the connection."));
                        Close();
                        return;
                    }
                    if (payload is not JsonObject message) continue;
                    var evt = message["evt"] is JsonValue name && name.TryGetValue<string>(out var value) ? value : null;
                    if (evt == "ERROR")
                        LastError = message["data"]?["message"] is JsonValue text && text.TryGetValue<string>(out var reason) ? reason : "Discord rejected the request.";
                    if (evt == "READY") ready.TrySetResult();
                }
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        ready.TrySetException(new InvalidOperationException("Discord closed the connection."));
        Action? notify;
        lock (gate)
        {
            if (!ReferenceEquals(socket, connection)) return;
            notify = onClose; onClose = null; socket = null;
        }
        connection.Dispose();
        notify?.Invoke();
    }

    private static List<(int Op, JsonNode? Payload)> Drain(List<byte> pending)
    {
        var messages = new List<(int, JsonNode?)>();
        while (pending.Count >= HeaderBytes)
        {
            var header = pending.GetRange(0, HeaderBytes).ToArray();
            var op = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (length is < 0 or > MaxFrameBytes) { pending.Clear(); break; }
            if (pending.Count < HeaderBytes + length) break;
            var body = Encoding.UTF8.GetString(pending.GetRange(HeaderBytes, length).ToArray());
            pending.RemoveRange(0, HeaderBytes + length);
            try { messages.Add((op, JsonNode.Parse(body))); }
            catch (JsonException) { break; }
        }
        return messages;
    }

    private void Send(int op, JsonNode? payload)
    {
        var body = Encoding.UTF8.GetBytes(payload?.ToJsonString() ?? "null");
        var frame = new byte[HeaderBytes + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, HeaderBytes);
        lock (gate)
        {
            try
            {
                if (socket is not { } connection) return;
                var sent = 0;
                while (sent < frame.Length)
                {
                    var count = connection.Send(frame.AsSpan(sent));
                    if (count == 0) return;
                    sent += count;
                }
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }
    }

    public void SetActivity(DiscordActivity? activity)
    {
        if (!Connected) return;
        JsonObject? shown = null;
        if (activity is not null)
        {
            shown = new JsonObject { ["type"] = 0 };
            // A withheld file name publishes no line at all, rather than a false one.
            if (activity.Details is not null) shown["details"] = activity.Details;
            shown["state"] = activity.State;
            shown["timestamps"] = new JsonObject { ["start"] = activity.StartedAt };
        }
        Send(OpFrame, new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = shown }
        });
    }

    public void Close()
    {
        Socket? closing;
        lock (gate) { closing = socket; socket = null; onClose = null; }
        closing?.Dispose();
    }

    public void Dispose() => Close();
}