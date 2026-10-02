using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Codex.Host.IdeBridge;

/// <summary>
/// One connection speaking Codex's IPC framing: JSON objects behind a four-byte little-endian length. A frame over the
/// size cap, a malformed message or a partial frame left for five seconds closes this connection and no other.
/// </summary>
internal sealed class Peer : IDisposable
{
    public const int MaxFrame = 8 * 1024 * 1024;
    private static readonly TimeSpan PartialFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly Lock writing = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Peer(Socket socket)
    {
        this.socket = socket;
        stream = new NetworkStream(socket, ownsSocket: true);
    }

    public Task Closed => closed.Task;
    public bool IsClosed => closed.Task.IsCompleted;

    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static JsonObject? Record(JsonNode? node) => node as JsonObject;

    public void Send(JsonObject message)
    {
        if (IsClosed) return;
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxFrame)
        {
            Dispose();
            return;
        }
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        try
        {
            lock (writing) stream.Write(frame);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException) { Dispose(); }
    }

    public void Failure(string requestId, string error) =>
        Send(new JsonObject { ["type"] = "response", ["requestId"] = requestId, ["resultType"] = "error", ["error"] = error });

    /// <summary>Reads frames until the connection closes, handing each valid message to <paramref name="receive"/>.</summary>
    public async Task ReadAsync(Action<JsonObject> receive)
    {
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                int read;
                if (pending.Length == 0) read = await stream.ReadAsync(buffer, lifetime.Token);
                else
                {
                    using var partial = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    partial.CancelAfter(PartialFrameTimeout);
                    read = await stream.ReadAsync(buffer, partial.Token);
                }
                if (read == 0) break;
                pending.Write(buffer, 0, read);
                var data = pending.GetBuffer().AsMemory(0, (int)pending.Length);
                var consumed = 0;
                while (data.Length - consumed >= 4)
                {
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[consumed..]);
                    if (size > MaxFrame) return;
                    if (data.Length - consumed < size + 4) break;
                    JsonObject? message;
                    try { message = JsonNode.Parse(data.Span.Slice(consumed + 4, (int)size)) as JsonObject; }
                    catch (JsonException) { return; }
                    consumed += (int)size + 4;
                    if (message is null || Text(message["type"]) is null || message.ContainsKey("requestId") && Text(message["requestId"]) is null) return;
                    receive(message);
                }
                var rest = data[consumed..].ToArray();
                pending.SetLength(0);
                pending.Write(rest);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (!closed.TrySetResult()) return;
        lifetime.Cancel();
        try { socket.Shutdown(SocketShutdown.Both); } catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        stream.Dispose();
    }
}