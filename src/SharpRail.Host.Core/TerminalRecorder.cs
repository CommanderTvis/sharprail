using System.Text;

namespace SharpRail.Host.Core;

// Recent terminal output for attaching clients, following upstream's output recorder. A fresh view gets a
// bounded snapshot of the main screen: raw bytes, never the alternate screen and never a mode sequence
// itself, preceded by the private modes it last observed (mouse tracking excepted). A resuming client gets
// the exact bytes after its position while they are still retained.
internal sealed class TerminalRecorder(int snapshotBytes = TerminalRecorder.SnapshotBytes, int resumeBytes = TerminalRecorder.ResumeBytes)
{
    internal const int SnapshotBytes = 64 * 1024;
    internal const int ResumeBytes = 1024 * 1024;
    private static readonly int[] Tracked = [1, 7, 25, 2004];
    private static readonly int[] AlternateScreens = [47, 1047, 1049];

    private readonly SortedDictionary<int, bool> modes = [];
    private byte[] recorded = [];
    private int recordedLength;
    private byte[] carry = [];
    private bool alternate;
    private readonly byte[] resume = new byte[resumeBytes];

    // Total bytes of output ever pushed.
    internal long Position { get; private set; }

    internal void Push(ReadOnlySpan<byte> chunk)
    {
        Retain(chunk);
        Position += chunk.Length;
        if (snapshotBytes <= 0 || chunk.IsEmpty) return;
        var text = carry.Length == 0 ? chunk.ToArray() : [.. carry, .. chunk];
        carry = [];
        var partial = PartialMode(text);
        if (partial >= 0)
        {
            carry = text[partial..];
            text = text[..partial];
        }
        Consume(text);
    }

    // Output after the given position, or null once part of it is no longer retained.
    internal byte[]? From(long position)
    {
        if (position > Position) return null;
        var count = Position - position;
        if (count > resume.Length || count > Position) return null;
        var result = new byte[count];
        var start = (int)(position % resume.Length);
        var first = (int)Math.Min(count, resume.Length - start);
        resume.AsSpan(start, first).CopyTo(result);
        resume.AsSpan(0, (int)count - first).CopyTo(result.AsSpan(first));
        return result;
    }

    internal byte[] Snapshot()
    {
        if (recordedLength == 0) return [];
        var prefix = new StringBuilder("\x1b[0m");
        foreach (var (mode, enabled) in modes) prefix.Append($"\x1b[?{mode}{(enabled ? 'h' : 'l')}");
        return [.. Encoding.ASCII.GetBytes(prefix.ToString()), .. recorded.AsSpan(0, recordedLength)];
    }

    // Reloads a snapshot saved by an earlier host. The bytes go through the same parser as live output, so the
    // mode preamble is read back into tracked modes instead of being kept as text.
    internal void Restore(ReadOnlySpan<byte> snapshot)
    {
        if (snapshotBytes <= 0) return;
        var reset = "\x1b[0m"u8;
        if (snapshot.StartsWith(reset)) snapshot = snapshot[reset.Length..];
        Consume(snapshot.ToArray());
    }

    private void Retain(ReadOnlySpan<byte> chunk)
    {
        var position = Position;
        if (chunk.Length > resume.Length) { position += chunk.Length - resume.Length; chunk = chunk[^resume.Length..]; }
        var start = (int)(position % resume.Length);
        var first = Math.Min(chunk.Length, resume.Length - start);
        chunk[..first].CopyTo(resume.AsSpan(start));
        chunk[first..].CopyTo(resume);
    }

    private void Consume(byte[] text)
    {
        var cursor = 0;
        var index = 0;
        while (text.AsSpan(index).IndexOf("\x1b[?"u8) is var found and >= 0)
        {
            index += found;
            var end = index + 3;
            while (end < text.Length && (char.IsAsciiDigit((char)text[end]) || text[end] == ';')) end++;
            if (end >= text.Length || (text[end] != 'h' && text[end] != 'l')) { index += 3; continue; }
            if (!alternate) Append(text.AsSpan(cursor, index - cursor));
            var enabled = text[end] == 'h';
            foreach (var part in Encoding.ASCII.GetString(text, index + 3, end - index - 3).Split(';'))
            {
                if (!int.TryParse(part, out var mode)) continue;
                if (AlternateScreens.Contains(mode)) alternate = enabled;
                else if (Tracked.Contains(mode)) modes[mode] = enabled;
            }
            cursor = index = end + 1;
        }
        if (!alternate) Append(text.AsSpan(cursor));
    }

    private void Append(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty) return;
        if (recorded.Length < recordedLength + text.Length)
            Array.Resize(ref recorded, Math.Max(recordedLength + text.Length, Math.Min(recorded.Length * 2 + 1024, snapshotBytes * 2)));
        text.CopyTo(recorded.AsSpan(recordedLength));
        recordedLength += text.Length;
        if (recordedLength <= snapshotBytes) return;
        // Trim at a line start so the snapshot never begins inside a line or an escape sequence.
        var over = recordedLength - snapshotBytes;
        var boundary = recorded.AsSpan(over - 1, recordedLength - over + 1).IndexOf((byte)'\n');
        var keep = boundary < 0 ? 0 : recordedLength - (over + boundary);
        recorded.AsSpan(recordedLength - keep, keep).CopyTo(recorded);
        recordedLength = keep;
    }

    // The start of a trailing escape that might still become a private mode sequence, or -1.
    private static int PartialMode(byte[] text)
    {
        var escape = Array.LastIndexOf(text, (byte)0x1b);
        if (escape < 0 || text.Length - escape > 20) return -1;
        var tail = text.AsSpan(escape + 1);
        if (tail.IsEmpty) return escape;
        if (tail[0] != '[') return -1;
        tail = tail[1..];
        if (!tail.IsEmpty && tail[0] == '?') tail = tail[1..];
        foreach (var value in tail)
            if (!char.IsAsciiDigit((char)value) && value != ';') return -1;
        return escape;
    }
}