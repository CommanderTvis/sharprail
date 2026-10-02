using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// An interrupted turn fires no hook, but Claude appends a <c>[Request interrupted by user]</c> user line to the
/// session transcript. While a tab is running, this polls the transcript's tail once a second and reports the interrupt
/// when the last turn on disk is that marker and was written no earlier than the report that started the watch.
/// </summary>
internal sealed class InterruptWatch(Func<string, DateTimeOffset, bool>? probe = null, Func<DateTimeOffset>? now = null, IPollTimer? timer = null)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    // The hook's POST lands a little after the event it describes.
    public static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(1);
    private const int TailBytes = 16 * 1024;
    private const string Marker = "[Request interrupted by user";

    private sealed record Tracked(Func<string?> Locate, DateTimeOffset Since, Action OnInterrupted);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Tracked> tracked = [];
    private readonly Func<string, DateTimeOffset, bool> probe = probe ?? InterruptedSince;
    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly IPollTimer timer = timer ?? SystemPollTimer.Instance;
    private IDisposable? running;

    private static string? ReadTail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(stream.Length, TailBytes);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            return Encoding.UTF8.GetString(buffer);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsInterruptText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString()!.StartsWith(Marker, StringComparison.Ordinal),
        JsonValueKind.Array => content.EnumerateArray().Any(block => block.ValueKind == JsonValueKind.Object &&
            block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
            block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString()!.StartsWith(Marker, StringComparison.Ordinal)),
        _ => false
    };

    /// <summary>Whether the conversation's last turn on disk is the user's interrupt, written at <paramref name="since"/> or later.</summary>
    public static bool InterruptedSince(string path, DateTimeOffset since)
    {
        if (ReadTail(path) is not { } tail) return false;
        var lines = tail.Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            JsonElement entry;
            try { entry = JsonSerializer.Deserialize<JsonElement>(lines[index]); }
            catch (JsonException) { continue; }
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("user" or "assistant")) continue;
            if (type.GetString() != "user" || !entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) || !IsInterruptText(content)) return false;
            return entry.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var written) && written >= since;
        }
        return false;
    }

    /// <summary>Starts (or restarts, from now) watching a running turn; <paramref name="locate"/> finds the transcript once it exists.</summary>
    public void Track(string key, Func<string?> locate, Action onInterrupted)
    {
        lock (gate)
        {
            tracked[key] = new(locate, now() - ClockSlack, onInterrupted);
            running ??= timer.Every(PollInterval, Sweep);
        }
    }

    public void Stop(string key)
    {
        lock (gate)
        {
            tracked.Remove(key);
            if (tracked.Count > 0) return;
            running?.Dispose();
            running = null;
        }
    }

    public void StopAll()
    {
        lock (gate) foreach (var key in tracked.Keys.ToArray()) Stop(key);
    }

    public void Sweep()
    {
        KeyValuePair<string, Tracked>[] entries;
        lock (gate) entries = [.. tracked];
        foreach (var (key, entry) in entries)
        {
            var path = entry.Locate();
            if (path is null || !probe(path, entry.Since)) continue;
            lock (gate)
            {
                if (!tracked.TryGetValue(key, out var current) || current != entry) continue;
                Stop(key);
            }
            entry.OnInterrupted();
        }
    }
}