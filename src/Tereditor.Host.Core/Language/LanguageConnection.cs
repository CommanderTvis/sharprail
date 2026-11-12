using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed class LanguageConnection : IAsyncDisposable
{
    private readonly ILanguageProcess process;
    private Process? Os => (process as OsLanguageProcess)?.Process;
    private readonly SemaphoreSlim writing = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reader, errors;
    private readonly string workspace;
    private int nextId;
    private int disposed;
    private string errorTail = "";
    private readonly LanguageLog log;
    private readonly DateTime started = DateTime.UtcNow;
    private readonly string serverId;
    private readonly Action<LanguageExit>? onExit;
    private readonly object trace = new();
    private readonly Queue<string> stderrLines = new(), messages = new();
    private readonly ConcurrentDictionary<int, (string Method, DateTime At)> inflight = new();
    private readonly Task exitWatcher;
    private string stderrPartial = "";
    private string[]? inflightAtClose;
    private string? stopReason;
    private bool stopFailure;
    private long memoryBytes;
    /// <summary>The last sampled resident memory, in bytes.</summary>
    internal long MemoryBytes { get { lock (trace) return memoryBytes; } }
    private DateTime memoryAt;
    internal const int StderrLines = 200, MessageCapacity = 60;
    internal LanguageExit? ExitRecord { get; private set; }
    internal Action<string, JsonNode?>? Notification { get; set; }
    internal Func<JsonArray>? WorkspaceFolders { get; set; }
    internal Func<string, JsonNode?, JsonNode?>? ClientRequest { get; set; }
    /// <summary>Whether the server's <c>window/showDocument</c> requests are answered (the provider opted in).</summary>
    internal bool AcceptsShowDocument { get; init; }

    /// <summary>Answers a server's <c>workspace/applyEdit</c>; without it, or when it returns null, the edit is refused.</summary>
    internal Func<JsonNode?, JsonNode?>? ApplyEdit { get; set; }
    internal Func<string, JsonNode?>? Configuration { get; set; }
    /// <summary>The server process's id, or null once it has exited.</summary>
    internal int? ProcessId { get { try { return process.HasExited ? null : process.Id; } catch (InvalidOperationException) { return null; } } }
    internal bool Exited => process.HasExited || reader.IsCompleted;

    internal LanguageConnection(ProcessStartInfo start, string workspace, LanguageLog log, string serverId = "", Action<LanguageExit>? onExit = null, ILanguageProcess? link = null)
    {
        this.workspace = workspace; this.log = log; this.serverId = serverId; this.onExit = onExit;
        log.Add("launch", "Workspace: " + workspace);
        if (link is not null) { process = link; log.Add("launch", "Running in this host's page."); }
        else
        {
            start.WorkingDirectory = workspace;
            start.UseShellExecute = false;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.CreateNoWindow = true;
            log.Add("launch", start.FileName + " " + JsonSerializer.Serialize(start.ArgumentList, HostJson.Default.CollectionString));
            var started = Process.Start(start) ?? throw new IOException("The language server did not start.");
            process = new OsLanguageProcess(started);
            LanguageProcesses.Started(started, start);
        }
        errors = Task.Run(async () =>
        {
            var buffer = new char[2048];
            try
            {
                int read;
                while ((read = await process.Errors.ReadAsync(buffer.AsMemory(), lifetime.Token)) > 0)
                {
                    var output = new string(buffer, 0, read);
                    log.Add("stderr", output);
                    var value = errorTail + output;
                    errorTail = value.Length > 4096 ? value[^4096..] : value;
                    KeepStderr(output);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        reader = Task.Run(ReadAsync);
        exitWatcher = Task.Run(WatchExitAsync);
    }

    /// <summary>Names why SharpRail itself ends this process; the first reason wins. A failure is a stop the user did not ask for.</summary>
    internal void Terminate(string reason, bool failure = false)
    {
        if (Interlocked.CompareExchange(ref stopReason, reason, null) is null) stopFailure = failure;
    }

    /// <summary>Samples resident memory while the process runs; the last sample before a death is its only memory evidence.</summary>
    internal void SampleMemory()
    {
        try
        {
            if (process.HasExited) return;
            if (Os is not { } os) return;
            var footprint = LanguageMemory.Footprint(os);
            lock (trace) { memoryBytes = footprint; memoryAt = DateTime.UtcNow; }
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
    }

    private string[] Inflight() => inflight.Select(item => $"{item.Value.Method} #{item.Key} for {(DateTime.UtcNow - item.Value.At).TotalSeconds:0.#}s").ToArray();

    private void KeepStderr(string output)
    {
        lock (trace)
        {
            var parts = (stderrPartial + output).Split('\n');
            stderrPartial = parts[^1].Length > 2000 ? parts[^1][^2000..] : parts[^1];
            foreach (var line in parts[..^1])
            {
                stderrLines.Enqueue(line.TrimEnd('\r').Length > 2000 ? line.TrimEnd('\r')[..2000] + "…" : line.TrimEnd('\r'));
                while (stderrLines.Count > StderrLines) stderrLines.Dequeue();
            }
        }
    }

    private void Trace(string text)
    {
        lock (trace)
        {
            messages.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {text}");
            while (messages.Count > MessageCapacity) messages.Dequeue();
        }
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await process.WaitForExitAsync();
            if (Os is not null) LanguageProcesses.Exited(process.Id);
            // Output written just before the exit may still be in the pipe; the grace period also covers grandchildren that keep it open.
            await Task.WhenAny(errors, Task.Delay(TimeSpan.FromSeconds(2)));
            var code = process.ExitCode;
            string[] stderr; string[] recent; long memory; DateTime sampled;
            lock (trace)
            {
                stderr = stderrPartial.Length > 0 ? [.. stderrLines, stderrPartial] : [.. stderrLines];
                recent = [.. messages]; memory = memoryBytes; sampled = memoryAt;
            }
            var pending = inflightAtClose ?? Inflight();
            var record = LanguageExits.Build(serverId, workspace, started, DateTime.UtcNow, code, stopReason, stopFailure, stderr, recent, pending, memory, sampled);
            ExitRecord = record;
            log.Add("exit", record.Reason);
            try { onExit?.Invoke(record); }
            catch (Exception error) { log.Add("exit", "Recording the exit failed: " + error.Message); }
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            log.Add("exit", "The exit status could not be read: " + error.Message);
        }
    }

    internal async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken token, TimeSpan? timeout = null)
    {
        if (Exited) throw new IOException("Language server stopped. " + errorTail);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        inflight[id] = (method, DateTime.UtcNow);
        Trace($"-> request {method} #{id}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        try
        {
            await SendAsync(writer => { writer.WriteNumber("id", id); writer.WriteString("method", method); Write(writer, "params", parameters); }, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Trace($"!! {method} #{id} cancelled or timed out after {(DateTime.UtcNow - inflight.GetValueOrDefault(id).At).TotalSeconds:0.#}s");
            if (!lifetime.IsCancellationRequested && !Exited)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                cancel.CancelAfter(TimeSpan.FromSeconds(1));
                try { await NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id }, cancel.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            throw;
        }
        finally { pending.TryRemove(id, out _); inflight.TryRemove(id, out _); }
    }

    internal Task NotifyAsync(string method, JsonNode? parameters, CancellationToken token)
        => Notify(method, parameters, token);

    private Task Notify(string method, JsonNode? parameters, CancellationToken token)
    {
        Trace($"-> notify {method}");
        return SendAsync(writer => { writer.WriteString("method", method); Write(writer, "params", parameters); }, token);
    }

    private static void Write(Utf8JsonWriter writer, string name, JsonNode? value)
    {
        writer.WritePropertyName(name);
        if (value is null) writer.WriteNullValue();
        else value.WriteTo(writer);
    }

    private async Task SendAsync(Action<Utf8JsonWriter> members, CancellationToken token)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            members(writer);
            writer.WriteEndObject();
        }
        var body = buffer.WrittenMemory;
        // Header and body leave in one write, so a frame is never half in the pipe between two writes.
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.Span.CopyTo(frame.AsSpan(header.Length));
        await writing.WaitAsync(token);
        try
        {
            await process.Input.WriteAsync(frame, token);
            await process.Input.FlushAsync(token);
        }
        catch (Exception error)
        {
            // A cancelled or failed write can leave half a frame in the pipe, so the stream cannot be reused.
            Terminate($"a write to its stdin failed or was cancelled ({error.GetType().Name}: {error.Message}), which leaves the JSON-RPC stream unusable", failure: true);
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            throw;
        }
        finally { writing.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("Language server closed its output.");
        try
        {
            var stream = process.Output;
            var single = new byte[1];
            while (!lifetime.IsCancellationRequested)
            {
                var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(single, lifetime.Token) == 0) throw new EndOfStreamException();
                    header.Append((char)single[0]);
                    if (header.Length > 8192) throw new IOException("Language server header exceeded 8 KiB.");
                }
                var lengthLine = header.ToString().Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (lengthLine is null || !int.TryParse(lengthLine[15..].Trim(), out var length) || length is < 0 or > 32 * 1024 * 1024)
                    throw new IOException("Invalid language server message length.");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, lifetime.Token);
                var message = JsonNode.Parse(body) ?? throw new IOException("Empty language server message.");
                if (message["method"] is { } methodNode)
                {
                    var method = methodNode.GetValue<string>();
                    Trace($"<- {(message["id"] is null ? "notify" : "request")} {method}");
                    if (message["id"] is { } serverId)
                        await AnswerAsync(serverId, method, message["params"]);
                    else
                    {
                        if (method is "window/logMessage" or "window/showMessage")
                            log.Add(method, message["params"]?["message"]?.ToString() ?? "");
                        Notification?.Invoke(method, message["params"]);
                    }
                }
                else if (message["id"] is JsonValue value && value.TryGetValue<int>(out var id) && pending.TryRemove(id, out var completion))
                {
                    Trace($"<- response #{id} {(inflight.TryRemove(id, out var asked) ? asked.Method : "")} {(message["error"] is null ? "ok" : "error")}");
                    if (message["error"] is { } error)
                    {
                        var detail = error["message"]?.ToString() ?? error.ToJsonString();
                        log.Add("response error", detail);
                        completion.TrySetException(new LanguageRequestException(detail, error["code"]?.GetValue<int>() ?? 0));
                    }
                    else completion.TrySetResult(message["result"]?.DeepClone());
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (!lifetime.IsCancellationRequested) log.Add("connection", error.Message);
        }
        finally
        {
            // Requests fail below and leave the in-flight set; the exit record needs what the server never answered.
            inflightAtClose = Inflight();
            foreach (var completion in pending.Values) completion.TrySetException(new IOException("Language server disconnected. " + errorTail, failure));
            pending.Clear();
        }
    }

    private Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
    {
        if (method is "workspace/_roslyn_projectNeedsRestore" or "client/registerCapability" or "client/unregisterCapability") ClientRequest?.Invoke(method, parameters?.DeepClone());
        JsonNode? result = method switch
        {
            "workspace/workspaceFolders" => WorkspaceFolders?.Invoke() ?? new JsonArray(new JsonObject { ["uri"] = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, ["name"] = Path.GetFileName(workspace) }),
            "workspace/configuration" => new JsonArray([.. (parameters?["items"] as JsonArray ?? []).Select(item => Configuration?.Invoke(item?["section"]?.ToString() ?? "")?.DeepClone())]),
            "window/showMessageRequest" => ClientRequest?.Invoke(method, parameters?.DeepClone()),
            "window/showDocument" when AcceptsShowDocument => ClientRequest?.Invoke(method, parameters?.DeepClone()),
            "workspace/applyEdit" => ApplyEdit?.Invoke(parameters?.DeepClone()) ?? new JsonObject { ["applied"] = false, ["failureReason"] = "SharpRail applies server edits only inside a code action the user or agent chose." },
            _ => null
        };
        var known = method is "workspace/workspaceFolders" or "workspace/configuration" or "workspace/applyEdit"
            or "client/registerCapability" or "client/unregisterCapability" or "window/workDoneProgress/create"
            or "workspace/_roslyn_projectNeedsRestore" or "workspace/semanticTokens/refresh" or "workspace/diagnostic/refresh" or "window/showMessageRequest" || method == "window/showDocument" && AcceptsShowDocument;
        return known ? SendAsync(writer => { Write(writer, "id", id); Write(writer, "result", result); }, lifetime.Token)
            : SendAsync(writer =>
            {
                Write(writer, "id", id);
                writer.WriteStartObject("error");
                writer.WriteNumber("code", -32601);
                writer.WriteString("message", "Unsupported client method");
                writer.WriteEndObject();
            }, lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Terminate("the connection was disposed without a stated reason");
        lifetime.Cancel();
        // Helpers the server spawned (tsserver, indexers) outlive it and may still write into the workspace; wait for the whole tree.
        var tree = process.HasExited || Os is null ? [] : await LanguageProcesses.DescendantsAsync(process.Id);
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        await Task.WhenAll(reader, errors);
        await process.WaitForExitAsync();
        await exitWatcher;
        await LanguageProcesses.WaitGoneAsync(tree, TimeSpan.FromSeconds(10));
        process.Dispose(); lifetime.Dispose(); writing.Dispose();
    }
}

/// <summary>A JSON-RPC error answer; it is an <see cref="IOException"/> so every caller that handles a refused request keeps working.</summary>
internal sealed class LanguageRequestException(string message, int code) : IOException(message)
{
    internal const int MethodNotFound = -32601;
    internal int Code { get; } = code;
}