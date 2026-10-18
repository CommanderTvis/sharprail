using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Host.Core.Context;

internal sealed class ContextMcpConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly Action<string>? log;
    private readonly string workspace;
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task errors;
    private int nextId;
    private bool disposed;

    internal ContextMcpConnection(ProcessStartInfo start, string workspace, Action<string>? log = null)
    {
        this.workspace = Path.GetFullPath(workspace); this.log = log;
        start.WorkingDirectory = this.workspace;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        process = Process.Start(start) ?? throw new IOException("JetBrains Context did not start.");
        errors = DrainErrorsAsync();
    }

    internal async Task<JsonObject> InitializeAsync(CancellationToken token)
    {
        var initialized = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject { ["roots"] = new JsonObject { ["listChanged"] = false } },
            ["clientInfo"] = new JsonObject { ["name"] = "SharpRail", ["version"] = "1" }
        }, token);
        if (initialized["protocolVersion"]?.GetValue<string>() is not ("2024-11-05" or "2025-03-26" or "2025-06-18"))
            throw new IOException("Unsupported JetBrains Context MCP protocol.");
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, token);
        return await RequestAsync("tools/list", new JsonObject(), token);
    }

    internal Task<JsonObject> SearchAsync(JsonObject arguments, CancellationToken token)
        => RequestAsync("tools/call", new JsonObject { ["name"] = "code_search", ["arguments"] = arguments.DeepClone() }, token);

    private async Task<JsonObject> RequestAsync(string method, JsonObject arguments, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await serial.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var id = ++nextId;
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = arguments }, deadline.Token);
            while (true)
            {
                var message = await ReadAsync(deadline.Token);
                if (message["method"] is JsonValue requested && requested.TryGetValue<string>(out var name))
                {
                    if (!message.ContainsKey("id")) continue;
                    var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]?.DeepClone() };
                    if (name == "roots/list") reply["result"] = new JsonObject
                    {
                        ["roots"] = new JsonArray(new JsonObject { ["uri"] = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, ["name"] = Path.GetFileName(workspace) })
                    };
                    else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unsupported client request." };
                    await WriteAsync(reply, deadline.Token);
                    continue;
                }
                if (message["id"]?.GetValue<int>() != id) continue;
                if (message["error"] is not null) throw new IOException("JetBrains Context rejected the MCP request.");
                return message["result"] as JsonObject ?? throw new IOException("JetBrains Context returned an invalid MCP result.");
            }
        }
        catch
        {
            Stop();
            throw;
        }
        finally { serial.Release(); }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken token)
    {
        var json = message.ToJsonString();
        if (json.Length > 1_048_576) throw new IOException("JetBrains Context request exceeds the size limit.");
        await process.StandardInput.WriteLineAsync(json.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }

    private async Task<JsonObject> ReadAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            if (await process.StandardOutput.ReadAsync(character, token) == 0) throw new IOException("JetBrains Context stopped unexpectedly; view this workspace's Context logs.");
            if (character[0] == '\n') break;
            line.Append(character[0]);
            if (line.Length > 1_048_576) throw new IOException("JetBrains Context response exceeds the size limit.");
        }
        try { return JsonNode.Parse(line.ToString()) as JsonObject ?? throw new JsonException(); }
        catch (JsonException)
        {
            var text = line.ToString();
            if (ContextOutput.RequiresLogin(text)) throw new ContextLoginRequiredException();
            throw new IOException("JetBrains Context did not return valid MCP JSON. View this workspace's Context logs.");
        }
    }

    private async Task DrainErrorsAsync()
    {
        var buffer = new char[2048];
        try
        {
            var pending = new StringBuilder(); int count;
            while ((count = await process.StandardError.ReadAsync(buffer, lifetime.Token)) > 0)
            {
                pending.Append(buffer, 0, count);
                if (pending.Length > 32_768) { pending.Clear(); log?.Invoke("[Oversized Context log line omitted.]"); continue; }
                var lines = pending.ToString().Split('\n');
                foreach (var line in lines.SkipLast(1)) log?.Invoke(ContextOutput.Sanitize(line));
                pending.Clear(); pending.Append(lines[^1]);
            }
            if (pending.Length > 0) log?.Invoke(ContextOutput.Sanitize(pending.ToString()));
        }
        catch (OperationCanceledException) { }
    }

    private void Stop()
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        Stop();
        await process.WaitForExitAsync();
        await errors;
        process.Dispose();
        lifetime.Dispose();
    }
}