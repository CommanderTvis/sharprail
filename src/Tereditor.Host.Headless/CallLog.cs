using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Tereditor.Host.Headless;

/// <summary>
/// The opt-in call log: one JSON line per tool call in <c>&lt;dir&gt;/&lt;session&gt;.jsonl</c>, between a <c>start</c> and an <c>end</c> line.
/// Calls only queue a line. A background writer appends and flushes each one, so a slow disk never delays a tool and a write
/// error never reaches the protocol. At the size limit it stops with one warning on stderr.
/// </summary>
internal sealed class CallLog : IAsyncDisposable
{
    private const int ArgumentChars = 500;
    private readonly McpConfig config;
    private readonly TextWriter stderr;
    private readonly FileStream file;
    private readonly Channel<(string Line, bool Final)> queue = Channel.CreateUnbounded<(string, bool)>(new() { SingleReader = true });
    private readonly Task writer;
    private long written;
    private int sequence, calls, errors;
    private volatile bool stopped;
    private readonly long started = Environment.TickCount64;

    internal string Session { get; }
    internal string Path { get; }

    private CallLog(McpConfig config, TextWriter stderr, string session, string path, FileStream file)
    {
        this.config = config;
        this.stderr = stderr;
        Session = session;
        Path = path;
        this.file = file;
        writer = Task.Run(Pump);
    }

    /// <summary>Creates the file and writes the <c>start</c> line. Throws <see cref="McpConfigException"/> when the directory cannot be used.</summary>
    internal static CallLog Start(McpConfig config, TextWriter stderr, IReadOnlyList<string> offered)
    {
        var session = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        var path = System.IO.Path.Combine(config.Log.Directory, session + ".jsonl");
        FileStream file;
        try
        {
            Directory.CreateDirectory(config.Log.Directory);
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new McpConfigException($"the call log cannot be written to {config.Log.Directory}: {error.Message}");
        }
        var log = new CallLog(config, stderr, session, path, file);
        log.Enqueue(Event("start", session, config.Arm, new JsonObject
        {
            ["version"] = typeof(CallLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            ["configHash"] = config.Hash(),
            ["level"] = config.Log.Level,
            ["maxResultChars"] = config.Log.MaxResultChars,
            ["maxFileKb"] = config.Log.MaxFileKb,
            ["toolsOffered"] = new JsonArray([.. offered.Select(name => (JsonNode?)JsonValue.Create(name))]),
            ["config"] = config.ToJson(offered)
        }), final: false);
        return log;
    }

    internal int NextSequence() => Interlocked.Increment(ref sequence);

    /// <summary>Queues one <c>call</c> line. Never throws.</summary>
    internal void Record(int seq, string tool, string? operation, TimeSpan duration, bool isError, string? text, string? error, JsonObject arguments)
    {
        if (stopped) return;
        try
        {
            Interlocked.Increment(ref calls);
            if (isError) Interlocked.Increment(ref errors);
            var line = Event("call", Session, config.Arm, new JsonObject
            {
                ["seq"] = seq,
                ["tool"] = tool,
                ["operation"] = operation,
                ["durationMs"] = Math.Round(duration.TotalMilliseconds, 1),
                ["isError"] = isError,
                ["resultChars"] = text?.Length ?? 0,
                ["resultLines"] = text is null or "" ? 0 : text.Count(c => c == '\n') + (text[^1] == '\n' ? 0 : 1),
                ["error"] = error is null ? null : Truncate(error, ArgumentChars),
                ["args"] = config.Log.Level is "args" or "full" ? Summarize(arguments) : null,
                ["result"] = config.Log.Level == "full" && text is not null ? Truncate(text, config.Log.MaxResultChars) : null
            });
            Enqueue(line, final: false);
        }
        catch (Exception) { }
    }

    private static JsonObject Event(string kind, string session, string? arm, JsonObject fields)
    {
        var line = new JsonObject { ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["ev"] = kind, ["session"] = session, ["arm"] = arm };
        foreach (var (key, value) in fields.ToArray())
        {
            fields.Remove(key);
            if (value is not null) line[key] = value;
        }
        return line;
    }

    private static JsonObject Summarize(JsonObject arguments)
    {
        var summary = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            if (key == "workspace") continue;
            summary[key] = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? Truncate(text, ArgumentChars)
                : value is JsonValue or null ? value?.DeepClone() : Truncate(value.ToJsonString(), ArgumentChars);
        }
        return summary;
    }

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";

    private void Enqueue(JsonObject line, bool final) => queue.Writer.TryWrite((line.ToJsonString() + "\n", final));

    private async Task Pump()
    {
        try
        {
            await foreach (var (line, final) in queue.Reader.ReadAllAsync())
            {
                if (stopped && !final) continue;
                var bytes = Encoding.UTF8.GetBytes(line);
                // The end line is small and always written, so a log that hit the limit still says how it ended.
                if (!final && written + bytes.Length > (long)config.Log.MaxFileKb * 1024)
                {
                    stopped = true;
                    stderr.WriteLine($"tereditor-headless: the call log reached {config.Log.MaxFileKb} KB and stops here");
                    continue;
                }
                await file.WriteAsync(bytes);
                await file.FlushAsync();
                written += bytes.Length;
            }
        }
        catch (Exception error)
        {
            stopped = true;
            try { stderr.WriteLine("tereditor-headless: the call log stopped: " + error.Message); } catch (Exception) { }
        }
    }

    /// <summary>Writes the <c>end</c> line and closes the file, waiting a few seconds for queued lines.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            Enqueue(Event("end", Session, config.Arm, new JsonObject { ["calls"] = calls, ["errors"] = errors, ["totalMs"] = Environment.TickCount64 - started }), final: true);
            queue.Writer.TryComplete();
            await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        catch (Exception) { }
        await file.DisposeAsync();
    }
}