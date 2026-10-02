using System.Text;
using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// A session's token spending, read forward from its own transcript. Claude Code writes one assistant line per content
/// block, every line of a message sharing its id with a usage that grows while it streams, so usage is kept per message
/// id, last line wins, and the session total is the sum, subagent transcripts beside it included.
/// </summary>
internal sealed class TranscriptUsage
{
    private sealed class FileState
    {
        public long Offset;
        public readonly Dictionary<string, AgentTokenUsage> ByMessage = [];
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, FileState> files = [];

    private static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? count : 0;

    private static byte[]? ReadFrom(string path, long offset)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length <= offset) return [];
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - offset];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> SubagentTranscripts(string transcript)
    {
        var directory = Path.Combine(transcript.EndsWith(".jsonl", StringComparison.Ordinal) ? transcript[..^6] : transcript, "subagents");
        try { return Directory.GetFiles(directory).Where(path => path.EndsWith(".jsonl", StringComparison.Ordinal)).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // Complete lines only: a turn mid-write is picked up on the next read.
    private FileState Advance(string path)
    {
        if (!files.TryGetValue(path, out var state)) files[path] = state = new FileState();
        if (ReadFrom(path, state.Offset) is not { } chunk) return state;
        var complete = Array.LastIndexOf(chunk, (byte)'\n') + 1;
        foreach (var line in Encoding.UTF8.GetString(chunk, 0, complete).Split('\n'))
        {
            if (line.Length == 0) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "assistant") continue;
                if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                state.ByMessage[id.GetString()!] = new(Count(usage, "input_tokens"), Count(usage, "output_tokens"),
                    Count(usage, "cache_read_input_tokens"), Count(usage, "cache_creation_input_tokens"));
            }
            catch (JsonException) { }
        }
        state.Offset += complete;
        return state;
    }

    /// <summary>The session's spending so far: its own transcript plus every subagent transcript beside it.</summary>
    public AgentTokenUsage Read(string transcript)
    {
        lock (gate)
        {
            long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
            foreach (var path in SubagentTranscripts(transcript).Prepend(transcript))
                foreach (var usage in Advance(path).ByMessage.Values)
                {
                    input += usage.Input; output += usage.Output; cacheRead += usage.CacheRead; cacheWrite += usage.CacheWrite;
                }
            return new(input, output, cacheRead, cacheWrite);
        }
    }

    public void Forget(string transcript)
    {
        var prefix = (transcript.EndsWith(".jsonl", StringComparison.Ordinal) ? transcript[..^6] : transcript) + "/";
        lock (gate)
            foreach (var path in files.Keys.Where(path => path == transcript || path.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                files.Remove(path);
    }
}