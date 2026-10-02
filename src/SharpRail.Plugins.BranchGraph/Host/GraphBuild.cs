using System.Text;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.BranchGraph.Host;

/// <summary>Reads <c>git log</c> and <c>git worktree list</c> output into the graph the panel draws.</summary>
internal static class GraphBuild
{
    public const int Page = 400;
    private const char Separator = '\0';
    private const int Fields = 6;

    public static IReadOnlyList<string> LogArgs(int skip) =>
    [
        "log", "--branches", "--date-order", $"--skip={Math.Max(0, skip)}",
        // One past the page, so whether there is more is answered by the same read.
        $"--max-count={Page + 1}",
        "--format=%H%x00%h%x00%P%x00%cI%x00%an%x00%D%x00%s",
        "--end-of-options", "--"
    ];

    // Text a commit's author wrote, minus the control and invisible characters that could disguise it.
    private static string PlainText(string raw)
    {
        var text = new StringBuilder(raw.Length);
        foreach (var rune in raw.EnumerateRunes())
        {
            var code = rune.Value;
            if (code < 0x20 || code == 0x7f || code is >= 0x80 and <= 0x9f || code is >= 0x200b and <= 0x200f || code is >= 0x202a and <= 0x202e ||
                code is >= 0x2066 and <= 0x2069 || code is 0x061c or 0xfeff or 0x00ad) continue;
            text.Append(rune.ToString());
        }
        return text.ToString();
    }

    // %D's decoration list, minus the "HEAD -> " prefix git writes on the checked-out one.
    private static string[] Refs(string decoration) =>
    [
        .. decoration.Split(',').Select(name => name.Trim()).Select(name => name.StartsWith("HEAD -> ", StringComparison.Ordinal) ? name["HEAD -> ".Length..] : name)
            .Where(name => name.Length > 0 && name != "HEAD")
    ];

    public static (IReadOnlyList<GitGraphCommit> Commits, bool HasMore) ParseLog(string output)
    {
        var commits = new List<GitGraphCommit>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(Separator);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) continue;
            string Part(int index) => index < parts.Length ? parts[index] : "";
            commits.Add(new(parts[0], parts[1], Part(2).Split(' ', StringSplitOptions.RemoveEmptyEntries), Refs(Part(5)),
                PlainText(string.Join(Separator, parts.Skip(Fields))), PlainText(Part(4)), Part(3)));
        }
        var hasMore = commits.Count > Page;
        return (hasMore ? commits[..Page] : commits, hasMore);
    }

    /// <summary>macOS hands out <c>/var/...</c> for a <c>/private/var/...</c> worktree, so paths are matched after resolution.</summary>
    public static string SamePathKey(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try { return Resolve(full, 40); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return full; }
    }

    // Resolves symbolic links in the path and every ancestor, the way realpath does; a loop stops at the depth.
    private static string Resolve(string path, int depth)
    {
        if (depth == 0 || Path.GetDirectoryName(path) is not { } parent) return path;
        var resolved = Path.Combine(Resolve(parent, depth), Path.GetFileName(path));
        return new FileInfo(resolved).LinkTarget is { } target ? Resolve(Path.GetFullPath(target, Path.GetDirectoryName(resolved)!), depth - 1) : resolved;
    }

    public static IReadOnlyList<GitGraphWorktree> ParseWorktrees(string output, IReadOnlyList<HostWorkspace> workspaces)
    {
        var known = new Dictionary<string, HostWorkspace>();
        foreach (var workspace in workspaces) known[SamePathKey(workspace.Path)] = workspace;
        var worktrees = new List<GitGraphWorktree>();
        string path = "", head = "";
        void Flush()
        {
            if (path.Length == 0 || head.Length == 0) return;
            var workspace = known.GetValueOrDefault(SamePathKey(path));
            worktrees.Add(new(head, workspace?.Name ?? path[(path.LastIndexOf('/') + 1)..], workspace?.Id));
            path = head = "";
        }
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { Flush(); path = line["worktree ".Length..].Trim(); }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line["HEAD ".Length..].Trim();
        }
        Flush();
        return worktrees;
    }
}