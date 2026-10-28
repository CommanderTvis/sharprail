using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed class LanguageConnection : IAsyncDisposable
{
    private readonly Process process;
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
    internal Func<object[]>? WorkspaceFolders { get; set; }
    internal Func<string, JsonNode?, object?>? ClientRequest { get; set; }
    internal Func<string, object?>? Configuration { get; set; }
    internal int? ExitCode => process.HasExited ? process.ExitCode : null;
    internal async Task<int?> ObserveExitAsync()
    {
        await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(250));
        return ExitCode;
    }
    internal bool Exited => process.HasExited || reader.IsCompleted;

    internal LanguageConnection(ProcessStartInfo start, string workspace, LanguageLog log, string serverId = "", Action<LanguageExit>? onExit = null)
    {
        this.workspace = workspace; this.log = log; this.serverId = serverId; this.onExit = onExit;
        start.WorkingDirectory = workspace;
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        log.Add("launch", "Workspace: " + workspace);
        log.Add("launch", start.FileName + " " + JsonSerializer.Serialize(start.ArgumentList));
        process = Process.Start(start) ?? throw new IOException("The language server did not start.");
        LanguageProcesses.Started(process, start);
        errors = Task.Run(async () =>
        {
            var buffer = new char[2048];
            try
            {
                int read;
                while ((read = await process.StandardError.ReadAsync(buffer, lifetime.Token)) > 0)
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
            var footprint = LanguageMemory.Footprint(process);
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
            LanguageProcesses.Exited(process.Id);
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

    internal async Task<JsonNode?> RequestAsync(string method, object? parameters, CancellationToken token, TimeSpan? timeout = null)
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
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Trace($"!! {method} #{id} cancelled or timed out after {(DateTime.UtcNow - inflight.GetValueOrDefault(id).At).TotalSeconds:0.#}s");
            if (!lifetime.IsCancellationRequested && !Exited)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                cancel.CancelAfter(TimeSpan.FromSeconds(1));
                try { await NotifyAsync("$/cancelRequest", new { id }, cancel.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            throw;
        }
        finally { pending.TryRemove(id, out _); inflight.TryRemove(id, out _); }
    }

    internal Task NotifyAsync(string method, object? parameters, CancellationToken token)
        => Notify(method, parameters, token);

    private Task Notify(string method, object? parameters, CancellationToken token)
    {
        Trace($"-> notify {method}");
        return SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, token);
    }

    private async Task SendAsync(object message, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        // Header and body leave in one write, so a frame is never half in the pipe between two writes.
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        await writing.WaitAsync(token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(frame, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }
        catch (Exception error)
        {
            // A cancelled or failed write can leave half a frame in the pipe, so the stream cannot be reused.
            Terminate($"a write to its stdin failed or was cancelled ({error.GetType().Name}: {error.Message}), which leaves the JSON-RPC stream unusable", failure: true);
            try { if (!process.HasExited) process.Kill(true); }
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
            var stream = process.StandardOutput.BaseStream;
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
                        completion.TrySetException(new IOException(detail));
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
        object? result = method switch
        {
            "workspace/workspaceFolders" => WorkspaceFolders?.Invoke() ?? new[] { new { uri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, name = Path.GetFileName(workspace) } },
            "workspace/configuration" => (parameters?["items"] as JsonArray ?? []).Select(item => Configuration?.Invoke(item?["section"]?.ToString() ?? "")).ToArray(),
            "window/showMessageRequest" => ClientRequest?.Invoke(method, parameters?.DeepClone()),
            "workspace/applyEdit" => new { applied = false, failureReason = "Use the editor to apply changes." },
            _ => null
        };
        var known = method is "workspace/workspaceFolders" or "workspace/configuration" or "workspace/applyEdit"
            or "client/registerCapability" or "client/unregisterCapability" or "window/workDoneProgress/create"
            or "workspace/_roslyn_projectNeedsRestore" or "workspace/semanticTokens/refresh" or "workspace/diagnostic/refresh" or "window/showMessageRequest";
        return known ? SendAsync(new { jsonrpc = "2.0", id, result }, lifetime.Token)
            : SendAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported client method" } }, lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Terminate("the connection was disposed without a stated reason");
        lifetime.Cancel();
        // Helpers the server spawned (tsserver, indexers) outlive it and may still write into the workspace; wait for the whole tree.
        var tree = process.HasExited ? [] : await LanguageProcesses.DescendantsAsync(process.Id);
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        await Task.WhenAll(reader, errors);
        await process.WaitForExitAsync();
        await exitWatcher;
        await LanguageProcesses.WaitGoneAsync(tree, TimeSpan.FromSeconds(10));
        process.Dispose(); lifetime.Dispose(); writing.Dispose();
    }
}