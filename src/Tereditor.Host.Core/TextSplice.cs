using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// Line arithmetic for reverting one hunk. Only <c>\n</c> terminates a line and endings stay attached to their
/// line, so the file keeps its own CRLF/LF mix and a missing final newline survives a round trip.
/// </summary>
public static class TextSplice
{
    public static List<string> SplitLines(string text)
    {
        var segments = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            segments.Add(text[start..(index + 1)]);
            start = index + 1;
        }
        if (start < text.Length) segments.Add(text[start..]);
        return segments;
    }

    public static bool SpanFits(LineSpan span, int lines)
    {
        if (span.Start < 1 || span.Count < 0) return false;
        return span.Count == 0 ? span.Start <= lines + 1 : (long)span.Start + span.Count - 1 <= lines;
    }

    private static string? Ending(string? segment) =>
        segment is null ? null : segment.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : segment.EndsWith('\n') ? "\n" : null;

    private static bool EndsAtTail(LineSpan span, int lines) => (long)span.Start + span.Count - 1 == lines;

    private static string? DominantEol(IReadOnlyList<string> segments)
    {
        int crlf = 0, lf = 0;
        foreach (var segment in segments)
        {
            if (segment.EndsWith("\r\n", StringComparison.Ordinal)) crlf++;
            else if (segment.EndsWith('\n')) lf++;
        }
        if (crlf == 0 && lf == 0) return null;
        return crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>The modified text with its span replaced by the original's; both spans must fit their sides.</summary>
    public static string RevertedText(IReadOnlyList<string> original, IReadOnlyList<string> modified, LineSpan originalSpan, LineSpan modifiedSpan)
    {
        var from = originalSpan.Start - 1;
        var segments = new List<string>(modified);
        segments.RemoveRange(modifiedSpan.Start - 1, modifiedSpan.Count);
        segments.InsertRange(modifiedSpan.Start - 1, original.Skip(from).Take(originalSpan.Count));
        var eol = DominantEol(modified) ?? DominantEol(original) ?? "\n";
        for (var index = 0; index < segments.Count - 1; index++)
            if (Ending(segments[index]) is null) segments[index] += eol;
        if (segments.Count > 0)
        {
            var last = segments.Count - 1;
            var tail = segments[last];
            var bothSpansReachTail = EndsAtTail(modifiedSpan, modified.Count) && EndsAtTail(originalSpan, original.Count);
            var source = bothSpansReachTail ? original.LastOrDefault() : modified.LastOrDefault();
            var finalEnding = Ending(source);
            if (finalEnding is not null && Ending(tail) is null) segments[last] = tail + finalEnding;
            else if (finalEnding is null && Ending(tail) is not null)
                segments[last] = tail.EndsWith("\r\n", StringComparison.Ordinal) ? tail[..^2] : tail[..^1];
        }
        return string.Concat(segments);
    }
}