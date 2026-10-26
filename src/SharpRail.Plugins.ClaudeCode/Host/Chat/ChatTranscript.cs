using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>Reads a session's transcript JSONL, and its subagents' files beside it, back into a <see cref="ChatFolder"/>.</summary>
internal static class ChatTranscript
{
    /// <summary>What a replay found: the uuid of the last message folded, for resuming a session at that point.</summary>
    public sealed record Result(string? LastUuid, bool Found);

    /// <summary>
    /// Folds the transcript. With <paramref name="stopBefore"/> it ends before the message of that uuid, which is how a
    /// rewind keeps only what preceded a prompt. Reads the file line by line; call from a background thread.
    /// </summary>
    public static Result Replay(ChatFolder folder, string path, string? stopBefore, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(null, false);
        string? last = null;
        var stopped = false;
        var skipped = 0;
        foreach (var line in ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParse(line, out var message)) { skipped++; continue; }
            var type = ChatFolder.Str(message, "type");
            if (type is not ("user" or "assistant" or "system")) continue;
            if (ChatFolder.Str(message, "uuid") is { } uuid)
            {
                if (uuid == stopBefore) { stopped = true; break; }
                if (type == "assistant" || type == "user") last = uuid;
            }
            folder.Apply(message);
        }
        // Lines that could not be read are one notice, not one each.
        if (skipped > 0) folder.AddNotice(ChatNoticeLevel.Warning, $"{skipped} earlier {(skipped == 1 ? "line" : "lines")} of this conversation could not be shown.", ChatNoticeKinds.Skipped);
        if (stopBefore is null || !stopped)
            ReplaySubagents(folder, path, cancellationToken);
        return new(last, true);
    }

    // The subagents' own conversations live in <session>/subagents/agent-*.jsonl, each with a meta file naming the Task call
    // it ran for, which is the parent its items hang from.
    private static void ReplaySubagents(ChatFolder folder, string path, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), "subagents");
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "agent-*.jsonl").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? toolUse = null;
            try
            {
                var meta = Path.ChangeExtension(file, ".meta.json");
                if (File.Exists(meta)) toolUse = ChatFolder.Str(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(meta)), "toolUseId");
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            if (toolUse is null) continue;
            foreach (var line in ReadLines(file))
                if (TryParse(line, out var message) && ChatFolder.Str(message, "type") is "user" or "assistant")
                    folder.Apply(message, toolUse);
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        StreamReader reader;
        try { reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        using (reader)
            while (reader.ReadLine() is { } line)
                if (line.Length > 0) yield return line;
    }

    private static bool TryParse(string line, out JsonElement message)
    {
        try { message = JsonSerializer.Deserialize<JsonElement>(line); return message.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { message = default; return false; }
    }
}