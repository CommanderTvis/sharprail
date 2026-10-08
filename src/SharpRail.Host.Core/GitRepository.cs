using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

internal static class GitRepository
{
    /// <summary>Upstream's budget for any Git call: long enough for a slow fetch, short enough to report one that stalled.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(55);
    private const int ErrorLimit = 2000, ErrorHead = 1200;
    private const string Truncated = "… (truncated) …";

    private static readonly Dictionary<string, string?> Prompts = new()
    {
        ["LC_ALL"] = "C",
        // Background status refreshes must not take index.lock away from the user's own Git commands.
        ["GIT_OPTIONAL_LOCKS"] = "0",
        // With no terminal to ask on, a credential or passphrase prompt fails at once instead of waiting.
        ["GIT_TERMINAL_PROMPT"] = "0"
    };

    internal static async Task<string> RunAsync(string root, CancellationToken ct, params string[] args) =>
        (await ExecuteAsync(root, args, Budget, ct)).Output;

    /// <summary>Runs Git and returns its standard output undecoded, for blob contents whose bytes are hashed.</summary>
    internal static async Task<byte[]> RunBytesAsync(string root, CancellationToken ct, params string[] args) =>
        (await ExecuteAsync(root, args, Budget, ct)).Bytes;

    internal static async Task<ChildProcess.Result> ExecuteAsync(string root, string[] args, TimeSpan budget, CancellationToken ct)
    {
        string[] full = ["--literal-pathspecs", "-c", "core.quotepath=false", .. args];
        ChildProcess.Result result;
        try { result = await ChildProcess.RunAsync("git", root, full, budget, ct, Prompts); }
        catch (ChildProcess.ExpiredException error) { throw new GitException(-1, TimeoutMessage(error, args)); }
        if (result.ExitCode != 0) throw new GitException(result.ExitCode, Bounded(result.Error));
        return result;
    }

    /// <summary>Keeps what Git wrote; without it, names only what was observed, with the SSH hint for network calls alone.</summary>
    private static string TimeoutMessage(ChildProcess.ExpiredException error, string[] args)
    {
        var network = args.Length > 0 && args[0] is "fetch" or "push" or "pull" or "clone" or "ls-remote";
        var captured = Bounded(error.Error);
        if (captured.Length == 0)
            captured = network
                ? "the remote never answered; if it uses SSH, a key that is not loaded is the usual cause (`ssh-add`)"
                : "git did not exit";
        return $"timed out after {Math.Max(1, Math.Round(error.Waited.TotalSeconds)):0}s — {captured}";
    }

    private static string Bounded(string raw)
    {
        var text = raw.Trim();
        return text.Length <= ErrorLimit ? text : text[..ErrorHead] + Truncated + text[^(ErrorLimit - Truncated.Length - ErrorHead)..];
    }

    /// <summary>The two sides of a file's diff: null is absent, an empty string the index, otherwise a commit id; a null modified side is the working tree.</summary>
    internal sealed record DiffRange(string? Original, string? Modified);

    /// <summary>Resolves a scope to the exact revisions both sides are read at, so a diff and a revert of it agree.</summary>
    internal static async Task<DiffRange> ResolveDiffRangeAsync(string root, string path, string scope, string comparison, CancellationToken ct)
    {
        async Task<string?> Head()
        {
            try { return (await RunAsync(root, ct, "rev-parse", "--verify", "HEAD")).Trim(); }
            catch (IOException) when (!ct.IsCancellationRequested) { return null; }
        }
        async Task<bool> Untracked() =>
            (await RunAsync(root, ct, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0;
        if (scope == "untracked" || (scope is "uncommitted" or "branch") && await Untracked()) return new(null, null);
        switch (scope)
        {
            case "commit":
                var (parent, commit) = await CommitRangeAsync(root, comparison, ct);
                return new(parent, commit);
            case "staged": return new(await Head(), "");
            case "working": return new("", null);
            case "branch": return new(await ComparisonBaseAsync(root, comparison, ct), null);
            case "uncommitted": return new(await Head(), null);
            default: return new(await Head() ?? "", null);
        }
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

    internal static List<WorktreeInfo> ParseWorktrees(string text)
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