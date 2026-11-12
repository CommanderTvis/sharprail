using System.Text.RegularExpressions;

using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

internal static class TerminalCommitLinks
{
    private const string Prefix = "sharprail-commit:";
    private static readonly Regex Hashes = new(@"(?<![\w])[0-9a-fA-F]{4,64}(?![\w])", RegexOptions.CultureInvariant);
    private static readonly Regex Urls = new(@"[a-zA-Z][a-zA-Z0-9+.-]*://\S+", RegexOptions.CultureInvariant);

    internal static bool HasCandidates(string text) => text.Length <= 16384 && Hashes.IsMatch(text);

    internal static string? Commit(TerminalLink link) => link.Path.StartsWith(Prefix, StringComparison.Ordinal) ? link.Path[Prefix.Length..] : null;

    internal static async Task<IReadOnlyList<TerminalLink>> Resolve(IProjectServices project, string text, CancellationToken token)
    {
        if (text.Length > 16384) return [];
        var urls = Urls.Matches(text).Cast<Match>().ToArray();
        var result = new List<TerminalLink>();
        var found = new Dictionary<string, GitCommit?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Hashes.Matches(text))
        {
            token.ThrowIfCancellationRequested();
            if (urls.Any(url => match.Index >= url.Index && match.Index < url.Index + url.Length)) continue;
            if (!found.TryGetValue(match.Value, out var commit))
            {
                if (found.Count >= 32) break;
                try { commit = await project.GetCommitAsync(match.Value, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { commit = null; }
                found[match.Value] = commit;
            }
            if (commit is not null && commit.Sha.StartsWith(match.Value, StringComparison.OrdinalIgnoreCase))
                result.Add(new(match.Index, match.Length, Prefix + commit.Sha, 1, 1, false));
        }
        return result;
    }
}