using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Core;

namespace SharpRail.Host.Headless;

/// <summary>MCP over stdio: one JSON-RPC message per line in, one per line out. Requests run concurrently, so responses may
/// arrive in any order, and each is written as a single line under a lock. Diagnostics go to <paramref name="log"/>, never to the output.</summary>
internal sealed class StdioMcp(Stream input, Stream output, Func<IReadOnlyList<McpServer.McpTool>> tools, string instructions, TextWriter log, ClientChannel? client = null)
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new();

    /// <summary>Serves until the input ends or <paramref name="stop"/> is cancelled, then lets running calls finish (up to a grace period, after which they are cancelled).</summary>
    public async Task RunAsync(CancellationTokenSource stop)
    {
        using var drain = new CancellationTokenSource();
        client?.Attach(body => WriteAsync(body, stop));
        var running = new ConcurrentDictionary<Task, byte>();
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        while (!stop.IsCancellationRequested)
        {
            // A blocked console read cannot be cancelled, so a stop abandons it and the process exits.
            var read = reader.ReadLineAsync();
            var next = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, stop.Token));
            if (next != read) break;
            string? line;
            try { line = await read; }
            catch (IOException) { break; }
            if (line is null) break;
            if (line.AsSpan().Trim().IsEmpty) continue;
            var task = Task.Run(() => HandleAsync(line, drain.Token, stop));
            running[task] = 0;
            _ = task.ContinueWith(done => running.TryRemove(done, out _), TaskScheduler.Default);
        }
        var all = Task.WhenAll(running.Keys);
        if (await Task.WhenAny(all, Task.Delay(Grace)) != all)
        {
            log.WriteLine($"stopping: cancelling {running.Count} call(s) still running after {Grace.TotalSeconds:0} s");
            await drain.CancelAsync();
            await Task.WhenAny(all, Task.Delay(Grace));
        }
    }

    private async Task HandleAsync(string line, CancellationToken drain, CancellationTokenSource stop)
    {
        string? key = null;
        try
        {
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await WriteAsync(Failure(null, -32700, "Parse error"), stop);
                return;
            }
            if (message is JsonObject frame)
            {
                // A reply to a request this server sent (a question to the user), else to one it never sent.
                if (!frame.ContainsKey("method") && (frame.ContainsKey("result") || frame.ContainsKey("error")))
                {
                    client?.TryComplete(frame);
                    return;
                }
                if (client is not null && frame["method"] is JsonValue init && init.TryGetValue<string>(out var initName) && initName == "initialize" && frame["params"] is JsonObject initParameters)
                    client.Initialized(initParameters, ClientChannel.Negotiated(initParameters));
                if (frame["method"] is JsonValue { } method && method.TryGetValue<string>(out var name) && name == "notifications/cancelled")
                {
                    if (frame["params"]?["requestId"] is { } target && pending.TryGetValue(target.ToJsonString(), out var victim)) await victim.CancelAsync();
                    return;
                }
                if (frame.ContainsKey("id") && frame["id"] is { } id) key = id.ToJsonString();
            }
            using var call = CancellationTokenSource.CreateLinkedTokenSource(drain);
            if (key is not null) pending[key] = call;
            JsonNode? body;
            try { (_, body) = await McpServer.HandleAsync(message, tools(), call.Token, instructions); }
            catch (OperationCanceledException) { return; }
            // A cancelled request gets no answer.
            if (body is not null && !call.IsCancellationRequested) await WriteAsync(body, stop);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.WriteLine("request failed: " + error.Message);
            try { await WriteAsync(Failure(null, -32603, "Internal error"), stop); } catch (Exception) { }
        }
        finally
        {
            if (key is not null) pending.TryRemove(key, out _);
        }
    }

    private async Task WriteAsync(JsonNode body, CancellationTokenSource stop)
    {
        // ToJsonString is compact and escapes control characters, so a message never spans lines.
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString() + "\n");
        await writing.WaitAsync();
        try
        {
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        catch (IOException error)
        {
            log.WriteLine("output closed: " + error.Message);
            await stop.CancelAsync();
        }
        finally { writing.Release(); }
    }

    private static JsonObject Failure(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}