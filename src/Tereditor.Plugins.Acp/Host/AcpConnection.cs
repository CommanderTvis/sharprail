using System.Text;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>A JSON-RPC error answer from the peer, or one this side sends back.</summary>
public sealed class AcpException(int code, string message) : Exception(message)
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int Internal = -32603;
    public const int AuthRequired = -32000;

    public int Code { get; } = code;
}

/// <summary>The agent went away (its output ended) while a request was waiting.</summary>
public sealed class AcpClosedException() : IOException("The ACP agent closed the connection.");

/// <summary>
/// One ACP transport: newline-delimited JSON-RPC 2.0 over a pair of streams (the agent's stdout and stdin). Either side may send
/// requests and notifications; incoming requests run concurrently so a long permission prompt never blocks the reader.
/// </summary>
internal sealed class AcpConnection : IAsyncDisposable
{
    private readonly StreamReader input;
    private readonly Stream output;
    private readonly Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> onRequest;
    private readonly Action<string, JsonNode?> onNotification;
    private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> pending = [];
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private long next;
    private readonly Task reader;

    public AcpConnection(Stream fromAgent, Stream toAgent, Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> onRequest, Action<string, JsonNode?> onNotification)
    {
        input = new StreamReader(fromAgent, new UTF8Encoding(false));
        output = toAgent;
        this.onRequest = onRequest;
        this.onNotification = onNotification;
        reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Completes when the agent's output has ended.</summary>
    public Task Closed => reader;

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref next);
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending)
        {
            if (reader.IsCompleted) throw new AcpClosedException();
            pending[id] = waiting;
        }
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken);
            return await waiting.Task.WaitAsync(cancellationToken);
        }
        finally { lock (pending) pending.Remove(id); }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        // ToJsonString escapes line breaks inside strings, so one message is exactly one line.
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writer.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(bytes, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await input.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (System.Text.Json.JsonException) { continue; }
                if (message is null) continue;
                Dispatch(message);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        List<TaskCompletionSource<JsonNode?>> waiting;
        lock (pending) waiting = [.. pending.Values];
        foreach (var request in waiting) request.TrySetException(new AcpClosedException());
    }

    private void Dispatch(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"];
        if (method is null)
        {
            if (id is null || !id.AsValue().TryGetValue<long>(out var key)) return;
            TaskCompletionSource<JsonNode?>? waiting;
            lock (pending) pending.TryGetValue(key, out waiting);
            if (waiting is null) return;
            if (message["error"] is JsonObject error)
                waiting.TrySetException(new AcpException(error["code"]?.GetValue<int>() ?? AcpException.Internal, error["message"]?.GetValue<string>() ?? "The agent refused the request."));
            else waiting.TrySetResult(message["result"]);
            return;
        }
        var parameters = message["params"];
        if (id is null)
        {
            try { onNotification(method, parameters); } catch (Exception error) when (error is not OutOfMemoryException) { }
            return;
        }
        var requestId = id.DeepClone();
        _ = Task.Run(async () =>
        {
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId };
            try { reply["result"] = await onRequest(method, parameters, lifetime.Token) ?? new JsonObject(); }
            catch (AcpException error) { reply["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message }; }
            catch (OperationCanceledException) { reply["error"] = new JsonObject { ["code"] = AcpException.Internal, ["message"] = "Cancelled." }; }
            catch (Exception error) when (error is not OutOfMemoryException) { reply["error"] = new JsonObject { ["code"] = AcpException.Internal, ["message"] = error.Message }; }
            try { await WriteAsync(reply, CancellationToken.None); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try { output.Close(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        try { await reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
    }
}