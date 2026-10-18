using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    internal LanguageConnection(ProcessStartInfo start, string workspace, LanguageLog log)
    {
        this.workspace = workspace; this.log = log;
        start.WorkingDirectory = workspace;
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        log.Add("launch", "Workspace: " + workspace);
        log.Add("launch", start.FileName + " " + JsonSerializer.Serialize(start.ArgumentList));
        process = Process.Start(start) ?? throw new IOException("The language server did not start.");
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
                }
                await process.WaitForExitAsync(lifetime.Token);
                log.Add("exit", "Code " + process.ExitCode);
            }
            catch (OperationCanceledException) { }
        });
        reader = Task.Run(ReadAsync);
    }

    internal async Task<JsonNode?> RequestAsync(string method, object? parameters, CancellationToken token, TimeSpan? timeout = null)
    {
        if (Exited) throw new IOException("Language server stopped. " + errorTail);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested && !Exited)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                cancel.CancelAfter(TimeSpan.FromSeconds(1));
                try { await NotifyAsync("$/cancelRequest", new { id }, cancel.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            throw;
        }
        finally { pending.TryRemove(id, out _); }
    }

    internal Task NotifyAsync(string method, object? parameters, CancellationToken token)
        => SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, token);

    private async Task SendAsync(object message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await writing.WaitAsync(token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), token);
            await process.StandardInput.BaseStream.WriteAsync(bytes, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }
        catch
        {
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
        lifetime.Cancel();
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        await Task.WhenAll(reader, errors);
        await process.WaitForExitAsync();
        process.Dispose(); lifetime.Dispose(); writing.Dispose();
    }
}