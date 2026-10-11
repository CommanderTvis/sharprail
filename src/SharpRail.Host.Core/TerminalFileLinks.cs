using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

// Character scanner with existence-checked candidates, following IntelliJ's terminal filter approach.
internal static class TerminalFileLinks
{
    internal const int MaxLineLength = 16384;
    private static bool Boundary(char c) => char.IsWhiteSpace(c) || "()[]<>{}\"'`:;,=|^!?*".Contains(c);
    private static bool Separator(char c) => c is '/' or '\\';

    internal static IReadOnlyList<TerminalFileLink> Find(string text, string? directory, string? home, CancellationToken ct = default)
    {
        if (text.Length > MaxLineLength) return [];
        var links = new List<TerminalFileLink>();
        var kinds = new Dictionary<string, bool?>();
        bool? Lookup(string path)
        {
            ct.ThrowIfCancellationRequested();
            if (kinds.TryGetValue(path, out var known)) return known;
            bool? result = Directory.Exists(path) ? true : File.Exists(path) ? false : null;
            kinds[path] = result;
            return result;
        }
        string? Resolve(string candidate)
        {
            try
            {
                candidate = candidate.Replace('\\', Path.DirectorySeparatorChar);
                if (candidate.StartsWith("~/", StringComparison.Ordinal))
                {
                    if (home is null) return null;
                    candidate = Path.Combine(home, candidate[2..]);
                }
                if (!Path.IsPathFullyQualified(candidate))
                {
                    if (directory is null || candidate.Length > 1 && candidate[1] == ':') return null;
                    candidate = Path.Combine(directory, candidate);
                }
                return Path.GetFullPath(candidate);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        for (var start = 0; start < text.Length; start++)
        {
            ct.ThrowIfCancellationRequested();
            if (start > 0 && !Boundary(text[start - 1])) continue;
            var first = text[start];
            if (Boundary(first) || "#$%&+".Contains(first)) continue;
            var absolute = first == '/' || first == '~' && start + 1 < text.Length && Separator(text[start + 1]) ||
                char.IsAsciiLetter(first) && start + 2 < text.Length && text[start + 1] == ':' && Separator(text[start + 2]);
            var tokenEnd = start;
            while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]) && text[tokenEnd] is not '<' and not '>' and not '`') tokenEnd++;
            var token = text.AsSpan(start, tokenEnd - start);
            if (token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            { start = tokenEnd - 1; continue; }
            if (token.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var end = tokenEnd;
                while (end > start && (Boundary(text[end - 1]) || text[end - 1] == '.')) end--;
                if (Uri.TryCreate(text[start..end], UriKind.Absolute, out var uri) && uri.IsFile && (uri.Host.Length == 0 || uri.IsLoopback) &&
                    Resolve(uri.LocalPath) is { } path && Lookup(path) is { } isDirectory)
                    links.Add(new(start, end - start, path, 1, 1, isDirectory));
                start = tokenEnd - 1;
                continue;
            }
            if (!absolute && (directory is null || first is '~' or '\\')) continue;
            TerminalFileLink? best = null;
            var segment = start;
            var scan = start;
            for (; scan <= text.Length; scan++)
            {
                var c = scan < text.Length ? text[scan] : '\0';
                if (scan - segment > 255) break;
                if (scan > start && Separator(c) && !(first == '~' && scan == start + 1) && !(absolute && scan == start + 2 && text[start + 1] == ':'))
                {
                    if (Resolve(text[start..scan]) is not { } parent || Lookup(parent) != true) break;
                    segment = scan + 1;
                }
                var driveColon = absolute && scan == start + 1 && char.IsAsciiLetter(first);
                if (scan < text.Length && !Boundary(c) && c != '\0' || driveColon) continue;
                var end = scan;
                for (var trim = 0; trim <= 3 && end > start; trim++, end--)
                {
                    var candidate = text[start..end];
                    if (candidate is not "." and not ".." and not "..." && candidate.Any(ch => !Separator(ch)) &&
                        Resolve(candidate) is { } path && Lookup(path) is { } isDirectory)
                    {
                        var position = Position(text, scan);
                        var linkEnd = position?.End ?? end;
                        if (position is null || linkEnd == text.Length || Boundary(text[linkEnd]) || text[linkEnd] == '.')
                            best = new(start, linkEnd - start, path, position?.Line ?? 1, position?.Column ?? 1, isDirectory);
                        break;
                    }
                    if (text[end - 1] != '.' && !Boundary(text[end - 1])) break;
                }
                if (!absolute || scan == text.Length || c is ':' or '\r' or '\n') break;
            }
            if (best is not null) { links.Add(best); start += best.Length - 1; }
            else { while (start + 1 < text.Length && !Boundary(text[start + 1])) start++; }
        }
        return links;
    }

    private static (int Line, int Column, int End)? Position(string text, int offset)
    {
        if (offset >= text.Length || text[offset] != ':') return null;
        var i = offset + 1;
        var tuple = text.AsSpan(i).StartsWith(" (");
        if (tuple) i += 2;
        int Number()
        {
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            return i > start && i - start <= 7 && int.TryParse(text.AsSpan(start, i - start), out var n) ? Math.Max(1, n) : 0;
        }
        var line = Number();
        if (line == 0) return null;
        var column = 1;
        if (tuple)
        {
            if (!text.AsSpan(i).StartsWith(", ")) return null;
            i += 2; column = Number();
            if (column == 0 || i >= text.Length || text[i++] != ')') return null;
        }
        else if (i < text.Length && text[i] is ':' or '-')
        {
            var separator = text[i++];
            var number = Number();
            if (number == 0) return null;
            if (separator == ':') column = number;
        }
        return (line, column, i);
    }
}