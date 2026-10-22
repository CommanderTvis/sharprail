using System.Text;

namespace SharpRail.Host.Core;

// Recent terminal output for attaching clients, following upstream's output recorder. A fresh view gets a
// bounded snapshot of the main screen: raw bytes, never the alternate screen and never a mode sequence
// itself, preceded by the private modes it last observed (mouse tracking excepted). A resuming client gets
// the exact bytes after its position while they are still retained.
internal sealed class TerminalRecorder(int snapshotBytes = TerminalRecorder.SnapshotBytes, int resumeBytes = TerminalRecorder.ResumeBytes)
{
    internal const int SnapshotBytes = 64 * 1024;
    internal const int MaxSnapshotBytes = 1024 * 1024;
    internal const int ResumeBytes = 1024 * 1024;
    private static readonly int[] Tracked = [1, 7, 25, 2004];
    private static readonly int[] AlternateScreens = [47, 1047, 1049];
    private static readonly int[] InputModes = [1000, 1002, 1003, 1006, 1007];

    private readonly SortedDictionary<int, bool> modes = [];
    private byte[] recorded = [];
    private int recordedLength;
    private byte[] alternateOutput = [];
    private int alternateLength;
    // A trailing escape that may still become a private mode sequence (at most 20 bytes), and the scratch
    // space that joins it with the next chunk, so pushing a chunk allocates nothing.
    private readonly byte[] carry = new byte[20];
    private int carryLength;
    private byte[] joined = [];
    private int? alternate;
    private readonly SortedSet<int> input = [];
    private readonly byte[] resume = new byte[resumeBytes];

    // Total bytes of output ever pushed.
    internal long Position { get; private set; }

    internal void Push(ReadOnlySpan<byte> chunk)
    {
        Retain(chunk);
        Position += chunk.Length;
        if (snapshotBytes <= 0 || chunk.IsEmpty) return;
        var text = chunk;
        if (carryLength > 0)
        {
            if (joined.Length < carryLength + chunk.Length) joined = new byte[Math.Max(carryLength + chunk.Length, joined.Length * 2)];
            carry.AsSpan(0, carryLength).CopyTo(joined);
            chunk.CopyTo(joined.AsSpan(carryLength));
            text = joined.AsSpan(0, carryLength + chunk.Length);
            carryLength = 0;
        }
        var partial = PartialMode(text);
        if (partial >= 0)
        {
            text[partial..].CopyTo(carry);
            carryLength = text.Length - partial;
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
        return [.. Encoding.ASCII.GetBytes(prefix.ToString()), .. TerminalReplay.WithoutQueries(recorded.AsSpan(0, recordedLength))];
    }

    // Reloads a snapshot saved by an earlier host. The bytes go through the same parser as live output, so the
    // mode preamble is read back into tracked modes instead of being kept as text.
    internal void Restore(ReadOnlySpan<byte> snapshot)
    {
        if (snapshotBytes <= 0) return;
        var reset = "\x1b[0m"u8;
        if (snapshot.StartsWith(reset)) snapshot = snapshot[reset.Length..];
        Consume(snapshot);
        alternate = null; alternateLength = 0; input.Clear();
    }

    // Live alternate-screen output is separate from the durable main-screen recording. A watcher cannot
    // ask the foreground program to redraw, so it must receive the existing alternate contents too.
    // The alternate screen and mouse/alternate-scroll modes of a full-screen program that is still on it.
    // A fresh view of a live shell needs them after the snapshot; they never enter the snapshot itself, so
    // an exited program's mouse tracking cannot outlive it.
    internal byte[] LiveScreen()
    {
        if (alternate is not { } screen) return [];
        var modes = new StringBuilder();
        foreach (var mode in input) modes.Append($"\x1b[?{mode}h");
        return [.. Encoding.ASCII.GetBytes($"\x1b[?{screen}h"),
            .. TerminalReplay.WithoutQueries(alternateOutput.AsSpan(0, alternateLength)),
            .. Encoding.ASCII.GetBytes(modes.ToString())];
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

    private void Consume(ReadOnlySpan<byte> text)
    {
        var cursor = 0;
        var index = 0;
        while (text[index..].IndexOf("\x1b[?"u8) is var found and >= 0)
        {
            index += found;
            var end = index + 3;
            while (end < text.Length && (char.IsAsciiDigit((char)text[end]) || text[end] == ';')) end++;
            if (end >= text.Length || (text[end] != 'h' && text[end] != 'l')) { index += 3; continue; }
            AppendScreen(text[cursor..index]);
            var previousAlternate = alternate;
            var enabled = text[end] == 'h';
            foreach (var range in text[(index + 3)..end].Split((byte)';'))
            {
                if (!int.TryParse(text[(index + 3)..end][range], out var mode)) continue;
                if (AlternateScreens.Contains(mode))
                {
                    alternate = enabled ? mode : null;
                    if (alternate != previousAlternate) alternateLength = 0;
                    if (!enabled) input.Clear();
                }
                else if (InputModes.Contains(mode)) { if (enabled) input.Add(mode); else input.Remove(mode); }
                else if (Tracked.Contains(mode)) modes[mode] = enabled;
            }
            if (alternate is not null && previousAlternate == alternate)
                Append(ref alternateOutput, ref alternateLength, text[index..(end + 1)]);
            cursor = index = end + 1;
        }
        AppendScreen(text[cursor..]);
    }

    private void AppendScreen(ReadOnlySpan<byte> text)
    {
        if (alternate is null) Append(ref recorded, ref recordedLength, text);
        else Append(ref alternateOutput, ref alternateLength, text);
    }

    private void Append(ref byte[] recorded, ref int recordedLength, ReadOnlySpan<byte> text)
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
    private static int PartialMode(ReadOnlySpan<byte> text)
    {
        var escape = text.LastIndexOf((byte)0x1b);
        if (escape < 0 || text.Length - escape > 20) return -1;
        var tail = text[(escape + 1)..];
        if (tail.IsEmpty) return escape;
        if (tail[0] != '[') return -1;
        tail = tail[1..];
        if (!tail.IsEmpty && tail[0] == '?') tail = tail[1..];
        foreach (var value in tail)
            if (!char.IsAsciiDigit((char)value) && value != ';') return -1;
        return escape;
    }
}