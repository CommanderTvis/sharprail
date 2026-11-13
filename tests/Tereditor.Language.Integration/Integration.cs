// Shared by the language integration scripts (run.cs, check.cs, shared-cache.cs) through `#:include`, beside
// scripts/lib/Tasks.cs. It has no entry point: the corpus rules, the machine-wide cache, child processes with a time limit,
// the standalone host started with TEREDITOR_HEADLESS_MCP=1 and the JSON-RPC client of its /mcp endpoint.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static class Suite
{
    /// <summary>The Release host every script launches; build it first.</summary>
    public static readonly string HostAssembly = Path.Combine(Tasks.Root, "src", "Tereditor.Host.Remote", "bin", "Release", "net10.0", "Tereditor.Host.Remote.dll");

    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true, NewLine = "\n" };

    /// <summary>Runs a script under <see cref="Tasks.Guard"/>, reporting any failure as one message and exit code 1, as the Node scripts this replaced exited.</summary>
    public static int Entry(Func<int> script) => Tasks.Guard(() =>
    {
        try { return script(); }
        catch (Exception exception) when (exception is not TaskFailure) { throw new TaskFailure(exception.Message); }
    });

    /// <summary>A file of this suite, beside the scripts.</summary>
    public static string SuiteFile(string name) => Path.Combine(Tasks.Root, "tests", "Tereditor.Language.Integration", name);

    public static JsonObject ReadJson(string name) => JsonNode.Parse(File.ReadAllText(SuiteFile(name)))?.AsObject() ?? throw new InvalidDataException($"{name} is not a JSON object.");

    /// <summary>Writes <paramref name="node"/> indented by two spaces with a final newline, as the reports have always been written.</summary>
    public static void WriteJson(string path, JsonNode node) => File.WriteAllText(path, node.ToJsonString(Indented) + "\n");

    public static string? Text(JsonNode? node, string key) => node is JsonObject item && item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static double? Number(JsonNode? node, string key) => node is JsonObject item && item[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    public static JsonArray? Items(JsonNode? node, string key) => node is JsonObject item ? item[key] as JsonArray : null;

    /// <summary>A value as a JavaScript template literal prints it: a missing one is "undefined".</summary>
    public static string Js(string? value) => value ?? "undefined";

    /// <summary>Sets a key only when there is a value, as JSON.stringify leaves out undefined ones.</summary>
    public static void Put(JsonObject target, string key, JsonNode? value)
    {
        if (value is not null) target[key] = value;
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static string Head(string text, int length) => text.Length <= length ? text : text[..length];

    public static string IsoNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    static readonly Regex ServerId = new(@"^[a-z]+\z"), Repository = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z"), Revision = new(@"^[a-f0-9]{40}\z");

    /// <summary>The corpus rules: pinned repositories and commits, probes inside the project, a source floor and real semantic assertions.</summary>
    public static void ValidateCorpus(JsonNode corpus)
    {
        if (Items(corpus, "cases") is not { Count: > 0 } cases) throw new InvalidDataException("No corpus cases");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in cases)
        {
            if (Text(scenario, "server") is not { } server || !ServerId.IsMatch(server) || !ids.Add(server)) throw new InvalidDataException("Invalid or duplicate provider");
            if (Text(scenario, "repository") is not { } repository || !Repository.IsMatch(repository) || Text(scenario, "revision") is not { } revision || !Revision.IsMatch(revision))
                throw new InvalidDataException("A repository and immutable commit are required");
            if (Text(scenario, "path") is not { Length: > 0 } probe || probe.StartsWith('/') || probe.Split('\\', '/').Contains("..")) throw new InvalidDataException("Probe must stay inside the project");
            if (!(Number(scenario, "minimumSourceFiles") >= 25) || Items(scenario, "extensions") is not { Count: > 0 }) throw new InvalidDataException("A medium-sized source floor is required");
            var calls = Items(scenario, "calls");
            if (calls is null || !calls.Any(call => Text(call, "assert") == "locations") || !calls.Any(call => Text(call, "assert") == "checked"))
                throw new InvalidDataException("Startup alone is not a semantic check");
            foreach (var call in calls)
            {
                if (Text(call, "tool") is not ("lsp_status" or "lsp_navigate" or "lsp_diagnostics")) throw new InvalidDataException("Only language MCP calls belong in this suite");
                if (Text(call, "assert") is { Length: > 0 } assert && assert is not ("locations" or "checked" or "crossFile" or "protocol")) throw new InvalidDataException("Unknown assertion");
                if (call is JsonObject item && item["anchor"] is { } anchor && (Text(anchor, "text") is not { } text || Text(anchor, "identifier") is not { } identifier || !text.Contains(identifier, StringComparison.Ordinal)))
                    throw new InvalidDataException("Invalid symbol anchor");
            }
        }
    }

    /// <summary>
    /// The machine-wide cache every checkout shares: TEREDITOR_CACHE_DIR from mise.toml, else the same default, for runs outside
    /// mise (as scripts/cache-dir.mjs and browser-compress.cs). A root created here is excluded from Time Machine on macOS.
    /// </summary>
    public static string CacheDir(params string[] parts)
    {
        var root = Tasks.Env("TEREDITOR_CACHE_DIR")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Tasks.Env("LOCALAPPDATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local"), "tereditor", "cache")
                : Path.Combine(Tasks.Env("XDG_CACHE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "tereditor"));
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            if (OperatingSystem.IsMacOS())
            {
                try { using var tmutil = Process.Start(new ProcessStartInfo("tmutil") { ArgumentList = { "addexclusion", root }, RedirectStandardOutput = true, RedirectStandardError = true }); tmutil?.WaitForExit(); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return Path.GetFullPath(Path.Combine([root, .. parts]));
    }

    public static void Apply(ProcessStartInfo info, IReadOnlyDictionary<string, string?>? environment)
    {
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>()) info.Environment[key] = value;
    }

    /// <summary>
    /// Runs a program to completion and returns its standard output, failing on a nonzero exit or after
    /// <paramref name="timeout"/> milliseconds (-1: none). Standard input is closed; standard error reaches the console
    /// unless <paramref name="quiet"/>.
    /// </summary>
    public static string Command(string file, IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string?>? environment = null, int timeout = 300_000, bool quiet = false)
    {
        var info = new ProcessStartInfo(Tasks.Resolve(file)) { UseShellExecute = false, WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = quiet };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Apply(info, environment);
        var line = string.Join(' ', arguments.Prepend(file));
        Process child;
        try { child = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {file}"); }
        catch (System.ComponentModel.Win32Exception exception) { throw new InvalidOperationException($"Cannot start {file}: {exception.Message}"); }
        using (child)
        {
            child.StandardInput.Close();
            var output = child.StandardOutput.ReadToEndAsync();
            var error = quiet ? child.StandardError.ReadToEndAsync() : Task.FromResult("");
            if (!child.WaitForExit(timeout))
            {
                try { child.Kill(); } catch (InvalidOperationException) { }
                child.WaitForExit();
                throw new InvalidOperationException($"Command timed out after {timeout / 1000} s: {line}");
            }
            child.WaitForExit();
            var text = output.GetAwaiter().GetResult();
            var errorText = error.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new InvalidOperationException($"Command failed with exit code {child.ExitCode}: {line}{(errorText.Length > 0 ? "\n" + errorText : "")}");
            return text;
        }
    }

    static int interrupted;
    static readonly HashSet<Process> children = [];

    public static bool Interrupted => Volatile.Read(ref interrupted) == 1;

    /// <summary>Ctrl+C marks the run interrupted and asks every running host to stop (SIGTERM); the script then winds down and reports.</summary>
    public static void TrapInterrupt() => Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Volatile.Write(ref interrupted, 1);
        Process[] running;
        lock (children) running = [.. children];
        foreach (var child in running) Signal(child, force: false);
    };

    public static void Track(Process child) { lock (children) children.Add(child); }

    public static void Untrack(Process child) { lock (children) children.Remove(child); }

    [DllImport("libc", EntryPoint = "kill")] static extern int Kill(int pid, int signal);

    /// <summary>SIGTERM, so the host can stop its language servers, or SIGKILL when <paramref name="force"/>; Windows has only the latter.</summary>
    public static void Signal(Process child, bool force)
    {
        try
        {
            if (child.HasExited) return;
            if (force || OperatingSystem.IsWindows()) child.Kill();
            else Kill(child.Id, 15);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>The standalone host as a child process, with a bounded tail of its output.</summary>
sealed class HostProcess
{
    static readonly Regex Listening = new(@"Now listening on: (http://127\.0\.0\.1:[0-9]+)");
    readonly Process child;
    readonly StringBuilder log = new();
    readonly int limit;
    bool stopped;

    HostProcess(Process child, int limit)
    {
        this.child = child;
        this.limit = limit;
    }

    public string Log
    {
        get { lock (log) return log.ToString(); }
    }

    public bool Exited => child.HasExited;

    /// <summary>Starts `dotnet Tereditor.Host.Remote.dll` from the repository root with <paramref name="environment"/> over this process's environment.</summary>
    public static HostProcess Start(string dotnet, IReadOnlyDictionary<string, string?> environment, int limit)
    {
        var info = new ProcessStartInfo(Tasks.Resolve(dotnet))
        {
            UseShellExecute = false, WorkingDirectory = Tasks.Root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(Suite.HostAssembly);
        Suite.Apply(info, environment);
        var child = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {dotnet}");
        var host = new HostProcess(child, limit);
        Suite.Track(child);
        child.OutputDataReceived += (_, e) => host.Append(e.Data);
        child.ErrorDataReceived += (_, e) => host.Append(e.Data);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        child.StandardInput.Close();
        return host;
    }

    void Append(string? line)
    {
        if (line is null) return;
        lock (log)
        {
            log.Append(line).Append('\n');
            if (log.Length > limit) log.Remove(0, log.Length - limit);
        }
    }

    /// <summary>Waits for Kestrel's listening line and returns the /mcp endpoint; fails with <paramref name="failure"/> when the host exits, the time runs out or the run is interrupted.</summary>
    public async Task<string> Endpoint(int milliseconds, int poll, string failure, bool stopOnInterrupt)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (true)
        {
            var match = Listening.Match(Log);
            if (match.Success) return match.Groups[1].Value + "/mcp";
            if (child.HasExited || Environment.TickCount64 > deadline || (stopOnInterrupt && Suite.Interrupted)) throw new InvalidOperationException(failure);
            await Task.Delay(poll);
        }
    }

    /// <summary>SIGTERM, up to ten seconds for the host to stop its servers, then SIGKILL.</summary>
    public async Task Stop()
    {
        if (stopped) return;
        stopped = true;
        if (!child.HasExited)
        {
            Suite.Signal(child, force: false);
            using var timeout = new CancellationTokenSource(10_000);
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                Suite.Signal(child, force: true);
                await child.WaitForExitAsync();
            }
        }
        Suite.Untrack(child);
        child.Dispose();
    }
}

/// <summary>JSON-RPC over the host's bearer-authenticated POST /mcp, two minutes per request.</summary>
sealed class McpClient(string endpoint, string token, bool numberRequests, string errorPrefix)
{
    // No proxy: the endpoint is loopback, and the Node runner this replaced never used HTTP_PROXY either.
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(120) };
    int nextId;

    public async Task<JsonNode?> Rpc(string method, JsonNode? parameters = null)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = numberRequests ? ++nextId : 1, ["method"] = method };
        Suite.Put(body, "params", parameters);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(Suite.Compact), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"MCP HTTP {(int)response.StatusCode}");
        var message = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        if (message is JsonObject reply && reply["error"] is { } error) throw new InvalidOperationException(errorPrefix + error.ToJsonString(Suite.Compact));
        return message is JsonObject result ? result["result"] : null;
    }

    /// <summary>A tools/call with <paramref name="workspace"/> merged in before <paramref name="arguments"/>, as the agent terminals send it.</summary>
    public Task<JsonNode?> Tool(string name, string workspace, JsonObject arguments)
    {
        var merged = new JsonObject { ["workspace"] = workspace };
        foreach (var (key, value) in arguments) merged[key] = value?.DeepClone();
        return Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = merged });
    }

    public static string NewToken() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}
