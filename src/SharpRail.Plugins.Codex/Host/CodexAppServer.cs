using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// One <c>codex app-server</c> stdio connection: initialization, request ids, line framing, bounded requests and
/// process cleanup. Any failure (exit, a malformed line, a timeout) invalidates it and rejects every pending request;
/// the next reader starts a fresh one.
/// </summary>
public sealed partial class CodexAppServer
{
    private const int MaxLine = 1_048_576;
    private const int SigTerm = 15;
    private const int SigKill = 9;

    private readonly Process? child;
    private readonly TimeSpan timeout;
    private readonly Lock gate = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> pending = [];
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task ready;
    private Exception? failure;
    private long sequence;

    /// <summary>The executable a configured launch command starts with, quoted or bare; never a shell or interactive arguments.</summary>
    public static string Executable(string command)
    {
        var match = LeadingWord().Match(command.Trim());
        var executable = match.Success ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value : "";
        if (executable.Length == 0 || executable.StartsWith('-') || EnvironmentAssignment().IsMatch(executable))
            throw new InvalidOperationException("The Codex command must start with an executable path.");
        return executable;
    }

    public CodexAppServer(string executable, TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
        var start = new ProcessStartInfo(executable, ["app-server"])
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                start.FileName = Path.Combine(Path.GetDirectoryName(typeof(CodexAppServer).Assembly.Location)!, "sharprail-codex-process-group");
                start.ArgumentList.Insert(0, ResolveUnixExecutable(executable, start.WorkingDirectory));
            }
            child = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex app-server.");
            child.EnableRaisingEvents = true;
            child.Exited += (_, _) => Fail(new InvalidOperationException("Codex app-server exited."));
            child.ErrorDataReceived += (_, _) => { };
            child.BeginErrorReadLine();
            _ = Task.Run(ReadAsync);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Fail(new InvalidOperationException($"Could not start Codex app-server: {error.Message}"));
            closed.TrySetResult();
        }
        ready = InitializeAsync();
    }

    public bool Alive
    {
        get { lock (gate) return failure is null; }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await SendRequestAsync("initialize", new { clientInfo = new { name = "sharprail", title = "SharpRail", version = "0.1.0" } });
            Send(new { method = "initialized", @params = new { } });
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>A request once initialization has completed; rejects with the connection's failure.</summary>
    public async Task<JsonElement> RequestAsync(string method, object? parameters = null)
    {
        await ready;
        return await SendRequestAsync(method, parameters ?? new { });
    }

    private Task<JsonElement> SendRequestAsync(string method, object parameters)
    {
        TaskCompletionSource<JsonElement> request;
        long id;
        lock (gate)
        {
            if (failure is not null) return Task.FromException<JsonElement>(failure);
            id = ++sequence;
            request = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = request;
        }
        var timer = new CancellationTokenSource(timeout);
        timer.Token.Register(() => Fail(new TimeoutException($"Codex {method} timed out.")));
        _ = request.Task.ContinueWith(_ => timer.Dispose(), TaskScheduler.Default);
        try { Send(new { id, method, @params = parameters }); }
        catch (Exception error) { Fail(error); }
        return request.Task;
    }

    private void Send(object message)
    {
        Exception? failed;
        lock (gate) failed = failure;
        if (failed is not null) throw failed;
        if (child is null) throw new InvalidOperationException("Codex app-server is not running.");
        var line = JsonSerializer.Serialize(message) + "\n";
        lock (child) { child.StandardInput.Write(line); child.StandardInput.Flush(); }
    }

    private async Task ReadAsync()
    {
        var reader = child!.StandardOutput;
        var line = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer);
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] != '\n') { line.Append(buffer[index]); continue; }
                    Receive(line.ToString());
                    line.Clear();
                }
                if (line.Length > MaxLine) { Fail(new InvalidOperationException("Codex app-server response exceeded the size limit.")); return; }
                if (!Alive) return;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        Fail(new InvalidOperationException("Codex app-server exited."));
    }

    private void Receive(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject message) throw new InvalidOperationException("Invalid Codex app-server response.");
            if (message["method"]?.GetValueKind() == JsonValueKind.String)
            {
                // A request from the server (it has an id) is answered; a notification needs nothing.
                if (message.TryGetPropertyValue("id", out var serverId))
                    Send(new { id = serverId?.DeepClone(), error = new { code = -32601, message = "Method not supported" } });
                return;
            }
            if (message["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id)) return;
            TaskCompletionSource<JsonElement>? call;
            lock (gate) call = pending.GetValueOrDefault(id);
            if (call is null) return;
            var hasResult = message.ContainsKey("result");
            if (!hasResult && message["error"] is not JsonObject) throw new InvalidOperationException("Invalid Codex app-server response.");
            lock (gate) pending.Remove(id);
            if (message["error"] is JsonObject error)
                call.TrySetException(new InvalidOperationException(error["message"]?.ToString() ?? "Codex request failed."));
            else call.TrySetResult(JsonSerializer.SerializeToElement(message["result"]));
        }
        catch (JsonException) { Fail(new InvalidOperationException("Invalid Codex app-server response.")); }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        TaskCompletionSource<JsonElement>[] rejected;
        lock (gate)
        {
            if (failure is not null) return;
            failure = error;
            rejected = [.. pending.Values];
            pending.Clear();
        }
        foreach (var request in rejected) request.TrySetException(error);
        if (child is null) return;
        _ = Task.Run(async () => { await TerminateAsync(); closed.TrySetResult(); });
    }

    private async Task TerminateAsync()
    {
        try { child!.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        if (OperatingSystem.IsWindows())
        {
            try { child!.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return;
        }
        var group = -child!.Id;
        var signaled = Kill(group, SigTerm) == 0;
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline && (!child.HasExited || Kill(group, 0) == 0))
        {
            if (!signaled) signaled = Kill(group, SigTerm) == 0;
            await Task.Delay(50);
        }
        _ = Kill(group, SigKill);
        if (!child.HasExited)
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
            _ = Kill(group, SigKill);
        }
        try { await child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }

    private static string ResolveUnixExecutable(string executable, string workingDirectory)
    {
        var candidates = executable.Contains('/') ? new[] { Path.GetFullPath(executable, workingDirectory) }
            : (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':')
                .Select(directory => Path.GetFullPath(Path.Combine(directory.Length == 0 ? workingDirectory : directory, executable), workingDirectory));
        foreach (var candidate in candidates)
            if (!Directory.Exists(candidate) && Access(candidate, 1) == 0) return candidate;
        throw new System.ComponentModel.Win32Exception(2);
    }

    /// <summary>Stops the connection, rejecting pending requests, and completes once the child has been reaped.</summary>
    public Task StopAsync()
    {
        Fail(new InvalidOperationException("Codex app-server stopped."));
        return closed.Task;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string path, int mode);

    [GeneratedRegex("""^(?:"([^"]+)"|'([^']+)'|([^\s'";|&<>`$()]+))(?:\s|$)""")]
    private static partial Regex LeadingWord();

    [GeneratedRegex(@"^\w+=")]
    private static partial Regex EnvironmentAssignment();
}