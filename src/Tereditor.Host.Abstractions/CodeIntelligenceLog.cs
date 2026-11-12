using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace SharpRail.Host.Abstractions;

/// <summary>
/// The Code Intelligence lifecycle log: one line per event, <c>key=value</c>, at <c>~/.sharprail/logs/code-intelligence.log</c>
/// (about 2 MB, one rotated copy <c>.1</c>). Callers only enqueue; a background thread writes, so no caller (above all the UI thread)
/// waits for the disk. Lines carry names, ids, durations and exception types, never file contents or secrets.
/// </summary>
public static class CodeIntelligenceLog
{
    private const long Limit = 2 * 1024 * 1024;
    private const int MaxValue = 300;
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private static readonly ConcurrentDictionary<string, Operation> Pending = new();
    private static readonly object Gate = new();
    private static string? path;
    private static Thread? writer;
    private static Timer? reporter;
    private static long written, enqueued, nextId;

    /// <summary>The log file; tests point it at a temporary file before the first line is written.</summary>
    public static string Path
    {
        get => path ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail", "logs", "code-intelligence.log");
        set { lock (Gate) path = value; }
    }

    /// <summary>How long an operation may be pending before the reporter names it, every period after.</summary>
    public static TimeSpan StillWaitingAfter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A short correlation id for one request.</summary>
    public static string NewId() => Interlocked.Increment(ref nextId).ToString("x", CultureInfo.InvariantCulture) + (Environment.TickCount64 & 0xfff).ToString("x3", CultureInfo.InvariantCulture);

    public static void Info(string evt, params (string Key, object? Value)[] fields) => Write("INFO", evt, fields);
    public static void Warn(string evt, params (string Key, object? Value)[] fields) => Write("WARN", evt, fields);
    public static void Error(string evt, Exception error, params (string Key, object? Value)[] fields) =>
        Write("ERROR", evt, [.. fields, ("error", error.GetType().Name), ("message", error.Message)]);

    /// <summary>Starts a tracked operation: logs <c>begin</c>, is named by the reporter while pending, and logs <c>end</c> with duration and outcome when ended.</summary>
    public static Operation Begin(string name, params (string Key, object? Value)[] fields)
    {
        var operation = new Operation(NewId(), name, fields);
        Pending[operation.Id] = operation;
        Write("INFO", name + ".begin", [("id", operation.Id), .. fields]);
        EnsureReporter();
        return operation;
    }

    /// <summary>Waits until every line enqueued so far is on disk (checks and shutdown).</summary>
    public static void Flush()
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref written) < Volatile.Read(ref enqueued) && Environment.TickCount64 < deadline) Thread.Sleep(5);
    }

    private static void Write(string level, string evt, (string Key, object? Value)[] fields)
    {
        if (OperatingSystem.IsBrowser()) return;
        var line = new System.Text.StringBuilder(160);
        line.Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append(' ').Append(level)
            .Append(" tid=").Append(Environment.CurrentManagedThreadId).Append(" evt=").Append(evt);
        foreach (var (key, value) in fields) line.Append(' ').Append(key).Append('=').Append(Format(value));
        EnsureWriter();
        Interlocked.Increment(ref enqueued);
        Queue.Writer.TryWrite(line.ToString());
    }

    private static string Format(object? value)
    {
        var text = value switch { null => "", bool flag => flag ? "true" : "false", IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        text = text.ReplaceLineEndings(" ");
        if (text.Length > MaxValue) text = text[..MaxValue] + "…";
        return text.Length == 0 || text.Any(char.IsWhiteSpace) || text.Contains('"') ? "\"" + text.Replace("\"", "'") + "\"" : text;
    }

    private static void EnsureWriter()
    {
        if (writer is not null) return;
        lock (Gate)
        {
            if (writer is not null) return;
            writer = new Thread(Drain) { IsBackground = true, Name = "code-intelligence-log" };
            writer.Start();
        }
    }

    private static void Drain()
    {
        var reader = Queue.Reader;
        StreamWriter? open = null; string? openPath = null;
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                try
                {
                    var target = Path;
                    if (open is null || openPath != target)
                    {
                        open?.Dispose();
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                        Rotate(target);
                        open = new StreamWriter(new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
                        openPath = target;
                    }
                    var count = 0;
                    while (reader.TryRead(out var line)) { open.WriteLine(line); count++; }
                    open.Flush();
                    Interlocked.Add(ref written, count);
                    if (open.BaseStream.Length > Limit) { open.Dispose(); open = null; Rotate(target); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Logging must never take the app down; drop what could not be written.
                    open?.Dispose(); open = null;
                    while (reader.TryRead(out _)) Interlocked.Increment(ref written);
                    Interlocked.Exchange(ref written, Volatile.Read(ref enqueued));
                    Thread.Sleep(1000);
                }
            }
        }
        finally { open?.Dispose(); }
    }

    private static void Rotate(string target)
    {
        try
        {
            var file = new FileInfo(target);
            if (file.Exists && file.Length > Limit) File.Move(target, target + ".1", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void EnsureReporter()
    {
        if (reporter is not null) return;
        lock (Gate) reporter ??= new Timer(_ => Report(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static void Report()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var operation in Pending.Values)
        {
            var age = Stopwatch.GetElapsedTime(operation.Started, now);
            if (age < StillWaitingAfter || Stopwatch.GetElapsedTime(operation.Reported, now) < StillWaitingAfter) continue;
            operation.Reported = now;
            Write("WARN", "still-waiting", [("id", operation.Id), ("op", operation.Name), ("pendingMs", (long)age.TotalMilliseconds), .. operation.Fields]);
        }
    }

    /// <summary>One pending operation; end it exactly once with <see cref="Succeed"/>, <see cref="Fail"/> or <see cref="Cancel"/>.</summary>
    public sealed class Operation
    {
        internal Operation(string id, string name, (string Key, object? Value)[] fields)
        { Id = id; Name = name; Fields = fields; Started = Reported = Stopwatch.GetTimestamp(); }

        public string Id { get; }
        internal string Name { get; }
        internal (string Key, object? Value)[] Fields { get; }
        internal long Started { get; }
        internal long Reported { get; set; }
        private int ended;

        public void Succeed(string outcome = "ok", params (string Key, object? Value)[] fields) => End("INFO", outcome, null, fields);
        public void Fail(Exception error, params (string Key, object? Value)[] fields) => End("ERROR", error is OperationCanceledException ? "cancelled" : "error", error, fields);
        public void Cancel(string reason, params (string Key, object? Value)[] fields) => End("INFO", "cancelled", null, [("reason", reason), .. fields]);

        private void End(string level, string outcome, Exception? error, (string Key, object? Value)[] fields)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            Pending.TryRemove(Id, out _);
            var extra = new List<(string, object?)> { ("id", Id), ("outcome", outcome), ("durationMs", (long)Stopwatch.GetElapsedTime(Started).TotalMilliseconds) };
            if (error is not null) { extra.Add(("error", error.GetType().Name)); extra.Add(("message", error.Message)); }
            Write(level, Name + ".end", [.. extra, .. fields]);
        }
    }
}