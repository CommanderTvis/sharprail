namespace Tereditor.Host.Abstractions;

/// <summary>
/// Go to file's match of a query against a workspace-relative <c>/</c>-spelled path: the query's characters in order,
/// ignoring case. A query without a slash prefers the file name (whole name, then its start, then anywhere in it, then
/// spread over it) over the folders; one with a slash matches the whole path, so <c>tasks/dmg</c> finds
/// <c>scripts/tasks/dmg.cs</c>. Pure, so the host ranks and a client highlights by the same rule.
/// </summary>
public static class FileNameMatch
{
    /// <summary>
    /// The match's score, higher is better, or null when <paramref name="path"/> does not match. When given,
    /// <paramref name="positions"/> receives the indexes of the matched characters in <paramref name="path"/>.
    /// </summary>
    public static int? Score(string path, string query, List<int>? positions = null)
    {
        positions?.Clear();
        query = query.Trim();
        if (query.Length == 0) return null;
        var nameStart = path.LastIndexOf('/') + 1;
        var name = path.AsSpan(nameStart);
        if (!query.Contains('/'))
        {
            var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                Fill(positions, nameStart + at, query.Length);
                var tier = name.Length == query.Length ? 4000
                    : at == 0 && name[query.Length] == '.' ? 3800
                    : at == 0 ? 3500
                    : Boundary(path, nameStart + at) ? 3200 : 3000;
                return tier - path.Length;
            }
            if (Spread(path, nameStart, query, positions) is { } inName) return 2000 + inName - path.Length;
        }
        var whole = path.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (whole >= 0)
        {
            Fill(positions, whole, query.Length);
            return 1500 + (whole + query.Length > nameStart ? 200 : 0) - path.Length;
        }
        return Spread(path, 0, query, positions) is { } inPath ? 500 + inPath - path.Length : null;
    }

    /// <summary>The paths that match <paramref name="query"/>, best first, ties in ordinal path order.</summary>
    public static IEnumerable<string> Rank(IEnumerable<string> paths, string query)
    {
        var scored = new List<(string Path, int Score)>();
        foreach (var path in paths)
            if (Score(path, query) is { } score) scored.Add((path, score));
        return scored.OrderByDescending(match => match.Score).ThenBy(match => match.Path, StringComparer.Ordinal).Select(match => match.Path);
    }

    private static void Fill(List<int>? positions, int start, int length)
    {
        if (positions is null) return;
        for (var i = 0; i < length; i++) positions.Add(start + i);
    }

    // The query spread over path[from..], leftmost: adjacent characters and word starts earn, gaps cost.
    private static int? Spread(string path, int from, string query, List<int>? positions)
    {
        var bonus = 0;
        var last = -1;
        var at = from;
        foreach (var wanted in query)
        {
            var lower = char.ToLowerInvariant(wanted);
            var found = -1;
            for (var i = at; i < path.Length; i++)
                if (char.ToLowerInvariant(path[i]) == lower) { found = i; break; }
            if (found < 0) { positions?.Clear(); return null; }
            if (last >= 0) bonus += found == last + 1 ? 15 : -Math.Min(found - last, 20);
            if (Boundary(path, found)) bonus += 10;
            positions?.Add(found);
            last = found; at = found + 1;
        }
        return bonus;
    }

    private static bool Boundary(string path, int index) =>
        index == 0 || path[index - 1] is '/' or '.' or '_' or '-' or ' ' || char.IsUpper(path[index]) && char.IsLower(path[index - 1]);
}