using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Omp.Host;

/// <summary>
/// The context files omp reads for a launch directory, as https://omp.sh/docs/context-files describes them: per scope
/// and directory depth the highest-priority provider that has a file wins (native, claude, agents and codex, gemini,
/// opencode, github, agents-md), RULES.md applies at both user and project level, SYSTEM.md and APPEND_SYSTEM.md come from
/// the launch directory's <c>.omp/</c> then the user directory, and <c>@path</c> imports are followed up to five hops.
/// </summary>
public static partial class OmpContext
{
    public const int MaxImportDepth = 5;

    private sealed record Candidate(string Provider, string Path);

    /// <summary>The omp home's agent directory, or the SharpRail profile's when launches use it.</summary>
    public static string UserDirectory(string home, bool profile) =>
        profile ? Path.Combine(home, ".omp", "profiles", OmpLaunch.Profile, "agent") : Path.Combine(home, ".omp", "agent");

    public static OmpContextSnapshot Resolve(string root, string home, bool profile)
    {
        var user = UserDirectory(home, profile);
        var files = new List<OmpContextFile>();
        void Add(OmpContextScope scope, IReadOnlyList<Candidate> candidates)
        {
            var winner = true;
            foreach (var candidate in candidates)
            {
                if (SizeOf(candidate.Path) is not { } bytes) continue;
                files.Add(new(Label(candidate.Path, root, home), candidate.Path, scope, candidate.Provider, bytes, 0) { Shadowed = !winner });
                if (winner) files.AddRange(Imports(candidate.Path, scope, home, root, 1, [candidate.Path]));
                winner = false;
            }
        }

        // User scope: one AGENTS.md provider, then the rules and system files, which are separate.
        Add(OmpContextScope.User,
        [
            new("native", Path.Combine(user, "AGENTS.md")),
            new("codex", Path.Combine(home, ".codex", "AGENTS.md")),
            new("opencode", Path.Combine(home, ".config", "opencode", "AGENTS.md"))
        ]);
        Add(OmpContextScope.User, [new("native", Path.Combine(user, "RULES.md"))]);

        // Directories above the launch directory, most general first: the walking providers only.
        var ancestors = new List<string>();
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)); directory is { Length: > 0 }; directory = Path.GetDirectoryName(directory))
            ancestors.Add(directory);
        ancestors.Reverse();
        foreach (var directory in ancestors)
            Add(OmpContextScope.Ancestor,
            [
                new("agents", Path.Combine(directory, ".agent", "AGENTS.md")),
                new("agents", Path.Combine(directory, ".agents", "AGENTS.md")),
                new("agents-md", Path.Combine(directory, "AGENTS.md"))
            ]);

        Add(OmpContextScope.Project,
        [
            new("native", Path.Combine(root, ".omp", "AGENTS.md")),
            new("native", Path.Combine(root, "AGENTS.md")),
            new("claude", Path.Combine(root, ".claude", "CLAUDE.md")),
            new("agents", Path.Combine(root, ".agent", "AGENTS.md")),
            new("agents", Path.Combine(root, ".agents", "AGENTS.md")),
            new("gemini", Path.Combine(root, ".gemini", "GEMINI.md")),
            new("github", Path.Combine(root, ".github", "copilot-instructions.md"))
        ]);
        Add(OmpContextScope.Project, [new("native", Path.Combine(root, ".omp", "RULES.md"))]);
        foreach (var name in new[] { "SYSTEM.md", "APPEND_SYSTEM.md" })
            Add(File.Exists(Path.Combine(root, ".omp", name)) ? OmpContextScope.Project : OmpContextScope.User,
                [new("native", Path.Combine(root, ".omp", name)), new("native", Path.Combine(user, name))]);

        var offers = new List<OmpContextOffer>();
        if (!File.Exists(Path.Combine(root, "AGENTS.md")) && !File.Exists(Path.Combine(root, ".omp", "AGENTS.md")))
            offers.Add(new(OmpContextTarget.Agents, "Add AGENTS.md", "AGENTS.md"));
        if (!File.Exists(Path.Combine(root, ".omp", "RULES.md"))) offers.Add(new(OmpContextTarget.Rules, "Add .omp/RULES.md", ".omp/RULES.md"));
        if (!File.Exists(Path.Combine(root, ".omp", "APPEND_SYSTEM.md")))
            offers.Add(new(OmpContextTarget.AppendSystem, "Add .omp/APPEND_SYSTEM.md", ".omp/APPEND_SYSTEM.md"));
        return new(root, user, files, offers);
    }

    /// <summary>Creates a missing context file with a short heading; an existing file is left as it is.</summary>
    public static string Create(string root, OmpContextTarget target)
    {
        var (relative, body) = target switch
        {
            OmpContextTarget.Agents => ("AGENTS.md", "# Agent instructions\n"),
            OmpContextTarget.Rules => (Path.Combine(".omp", "RULES.md"), "# Rules\n"),
            _ => (Path.Combine(".omp", "APPEND_SYSTEM.md"), "# Appended system prompt\n")
        };
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(body);
        }
        catch (IOException) when (File.Exists(path)) { }
        return path;
    }

    private static IEnumerable<OmpContextFile> Imports(string path, OmpContextScope scope, string home, string root, int depth, HashSet<string> chain)
    {
        if (depth > MaxImportDepth) yield break;
        string body;
        try { body = File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var raw in ImportTokens(body))
        {
            var target = raw.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, raw[2..])
                : Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, raw));
            // An unreadable import stays as text in omp, so it is not a row.
            if (SizeOf(target) is not { } bytes) continue;
            if (chain.Contains(target))
            {
                yield return new(Label(target, root, home), target, scope, "import", bytes, depth) { Cycle = true };
                continue;
            }
            yield return new(Label(target, root, home), target, scope, "import", bytes, depth);
            foreach (var nested in Imports(target, scope, home, root, depth + 1, [.. chain, target])) yield return nested;
        }
    }

    /// <summary>The <c>@path</c> tokens omp expands: starting a line or after whitespace, outside inline code and fenced blocks.</summary>
    internal static IEnumerable<string> ImportTokens(string body)
    {
        var fenced = false;
        foreach (var line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }
            if (fenced) continue;
            foreach (Match match in ImportToken().Matches(InlineCode().Replace(line, " ")))
                yield return match.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')');
        }
    }

    private static string Label(string path, string root, string home)
    {
        var full = Path.GetFullPath(root);
        if (path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return Path.GetRelativePath(full, path);
        return path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~/" + Path.GetRelativePath(home, path) : path;
    }

    private static long? SizeOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"(?:^|\s)@([^\s`]+)")]
    private static partial Regex ImportToken();

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex InlineCode();
}