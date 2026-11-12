using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed class LanguageLog
{
    private const int Capacity = 32 * 1024;
    private readonly object gate = new();
    private readonly Queue<string> chunks = new();
    private int length;
    private bool truncated;

    internal void Add(string source, string message)
    {
        var text = $"[{DateTime.UtcNow:HH:mm:ss} UTC] {source}: {message.TrimEnd()}\n";
        lock (gate)
        {
            if (text.Length > Capacity) { text = text[^Capacity..]; truncated = true; }
            chunks.Enqueue(text); length += text.Length;
            while (length > Capacity) { length -= chunks.Dequeue().Length; truncated = true; }
        }
    }

    internal LanguageResult Read(string server)
    {
        lock (gate)
            return new("ready", length == 0 ? "No logs yet. Viewing logs does not start the server." : string.Concat(chunks), server)
            { Truncated = truncated };
    }
}