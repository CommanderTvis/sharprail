using System.Diagnostics;
using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

internal static class GitRepository
{
    internal static async Task<string> RunAsync(string root, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["LC_ALL"] = "C";
        start.ArgumentList.Add("--literal-pathspecs");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var text = await output;
        var detail = await error;
        if (process.ExitCode != 0) throw new GitException(process.ExitCode, detail.Trim());
        return text;
    }

    private sealed class GitException(int exitCode, string message) : IOException(message)
    {
        internal int ExitCode { get; } = exitCode;
    }

    internal static async Task<string> ComparisonBaseAsync(string root, string comparison, CancellationToken ct)
    {
        var target = (await RunAsync(root, ct, "rev-parse", "--verify", "--end-of-options", comparison + "^{commit}")).Trim();
        try { return (await RunAsync(root, ct, "merge-base", "--end-of-options", target, "HEAD")).Trim(); }
        catch (GitException error) when (error.ExitCode == 1) { return target; }
    }

    internal static async Task<List<string>> CommitDiffArgumentsAsync(string root, string commit, CancellationToken ct)
    {
        var (parent, sha) = await CommitRangeAsync(root, commit, ct);
        return parent is null ? ["show", "--format=", "--no-renames", sha] : ["diff", "--no-renames", parent, sha];
    }

    /// <summary>Resolves a commit and its first parent, which is null for a root commit.</summary>
    internal static async Task<(string? Parent, string Commit)> CommitRangeAsync(string root, string commit, CancellationToken ct)
    {
        if (commit.Length is < 4 or > 64 || !commit.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("A commit scope requires a hexadecimal commit id.");
        var sha = (await RunAsync(root, ct, "rev-parse", "--verify", "--quiet", "--end-of-options", commit + "^{commit}")).Trim();
        try { return ((await RunAsync(root, ct, "rev-parse", "--verify", "--quiet", "--end-of-options", sha + "^")).Trim(), sha); }
        catch (GitException error) when (error.ExitCode == 1) { return (null, sha); }
    }

    internal static async Task<GitSnapshot> SnapshotAsync(string root, string comparison, CancellationToken ct, string scope = "all")
    {
        if (scope is not ("all" or "uncommitted" or "staged" or "commit")) throw new ArgumentException("Unknown change scope.");
        try { await RunAsync(root, ct, "rev-parse", "--git-dir"); }
        catch (GitException error) when (error.ExitCode == 128 && error.Message.StartsWith("fatal: not a git repository (or any", StringComparison.Ordinal))
        { return new(false, "", [], [], []); }
        string branch;
        try { branch = (await RunAsync(root, ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim(); }
        catch (GitException error) when (error.ExitCode == 1) { branch = "detached HEAD"; }
        if (string.IsNullOrEmpty(branch)) branch = "detached HEAD";
        var status = scope == "commit" ? "" : await RunAsync(root, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var workingChanges = ParseStatus(status);
        var diff = scope == "commit" ? await CommitDiffArgumentsAsync(root, comparison, ct) : new List<string> { "diff", "--no-renames" };
        if (scope == "staged") diff.Add("--cached");
        else if (scope == "all" && comparison.Length > 0)
            diff.Add(await ComparisonBaseAsync(root, comparison, ct));
        else if (scope == "uncommitted") diff.Add("HEAD");
        else if (scope == "all")
        {
            try
            {
                await RunAsync(root, ct, "rev-parse", "--verify", "--quiet", "HEAD");
                diff.Add("HEAD");
            }
            catch (GitException error) when (error.ExitCode == 1) { diff.Add("--cached"); }
        }
        var names = await RunAsync(root, ct, diff.Concat(new[] { "--name-status", "-z", "--" }).ToArray());
        var statuses = workingChanges.ToDictionary(change => change.Path, StringComparer.Ordinal);
        var changes = ParseNames(names).Select(change => statuses.GetValueOrDefault(change.Path, change)).ToList();
        if (scope != "staged") changes.AddRange(workingChanges.Where(change => change.IndexStatus == "?"));
        var stats = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        var stat = await RunAsync(root, ct, diff.Concat(new[] { "--numstat", "-z", "--" }).ToArray());
        foreach (var row in stat.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = row.Split('\t', 3);
            if (fields.Length != 3) continue;
            stats[fields[2]] = (int.TryParse(fields[0], out var add) ? add : 0,
                int.TryParse(fields[1], out var remove) ? remove : 0);
        }
        changes = changes.Select(change => stats.TryGetValue(change.Path, out var value)
            ? change with { Added = value.Added, Removed = value.Removed } : change).ToList();
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            if (change.IndexStatus != "?") continue;
            var path = Path.Combine(root, change.Path);
            try
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null || info.Length > 8 * 1024 * 1024) continue;
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (bytes.Contains((byte)0)) continue;
                var lines = bytes.Count(value => value == '\n');
                if (bytes.Length > 0 && bytes[^1] != '\n') lines++;
                changes[index] = change with { Added = lines };
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var worktrees = ParseWorktrees(await RunAsync(root, ct, "worktree", "list", "--porcelain", "-z"));
        var branches = (await RunAsync(root, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var commits = scope != "commit" ? await ListCommitsAsync(root, comparison, ct) : [];
        return new(true, branch, changes, worktrees, branches) { Commits = commits };
    }

    internal static async Task<IReadOnlyList<GitCommit>> ListCommitsAsync(string root, string comparison, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var commits = new List<GitCommit>();
        if (comparison.Length > 0)
        {
            string log;
            try { log = await RunAsync(root, ct, "log", "--max-count=200", "--format=%H%x00%h%x00%cI%x00%an%x00%s", "--end-of-options", comparison + "..HEAD", "--"); }
            catch (GitException error) when (error.ExitCode == 128) { log = ""; }
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\0', 5);
                if (fields.Length == 5) commits.Add(new(fields[0], fields[1], DisplayText(fields[4]), DisplayText(fields[3]), fields[2]));
            }
        }
        return commits;
    }

    private static string DisplayText(string text) => new(text.Where(value =>
        value is not (< '\u0020' or >= '\u007f' and <= '\u009f' or >= '\u200b' and <= '\u200f' or
            >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069' or '\u061c' or '\ufeff' or '\u00ad')).ToArray());

    private static List<GitChange> ParseStatus(string status)
    {
        var parts = status.Split('\0');
        var result = new List<GitChange>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length < 4) continue;
            var row = parts[i];
            string? original = null;
            if (row[0] is 'R' or 'C' || row[1] is 'R' or 'C') original = parts[++i];
            result.Add(new(row[3..], row[..1], row[1..2], original, 0, 0));
        }
        return result;
    }

    private static List<GitChange> ParseNames(string names)
    {
        var parts = names.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<GitChange>();
        for (var i = 0; i + 1 < parts.Length; i += 2) result.Add(new(parts[i + 1], " ", parts[i], null, 0, 0));
        return result;
    }

    private static List<WorktreeInfo> ParseWorktrees(string text)
    {
        var result = new List<WorktreeInfo>();
        string path = "", branch = "";
        var locked = false;
        foreach (var line in text.Split('\0'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..];
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = line[18..];
            else if (line == "detached") branch = "detached HEAD";
            else if (line.StartsWith("locked", StringComparison.Ordinal)) locked = true;
            else if (line.Length == 0 && path.Length > 0)
            {
                result.Add(new(path, branch, result.Count == 0, locked));
                path = ""; branch = ""; locked = false;
            }
        }
        return result;
    }
}
