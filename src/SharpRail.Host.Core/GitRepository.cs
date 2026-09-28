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
        if (process.ExitCode != 0) throw new IOException(detail.Trim());
        return text;
    }

    internal static async Task<GitSnapshot> SnapshotAsync(string root, string comparison, CancellationToken ct)
    {
        try { await RunAsync(root, ct, "rev-parse", "--git-dir"); }
        catch (IOException) { return new(false, "", [], [], []); }
        string branch;
        try { branch = (await RunAsync(root, ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim(); }
        catch (IOException) { branch = "detached HEAD"; }
        if (string.IsNullOrEmpty(branch)) branch = "detached HEAD";
        var status = await RunAsync(root, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var changes = ParseStatus(status);
        if (comparison.Length > 0)
        {
            await RunAsync(root, ct, "rev-parse", "--verify", "--end-of-options", comparison + "^{commit}");
            var names = await RunAsync(root, ct, "diff", "--name-status", "-z", "--no-renames", comparison, "HEAD", "--");
            changes = ParseNames(names);
        }
        var stats = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        foreach (var stat in comparison.Length > 0
            ? new[] { await RunAsync(root, ct, "diff", "--numstat", "-z", "--no-renames", comparison, "HEAD", "--") }
            : new[] { await RunAsync(root, ct, "diff", "--numstat", "-z", "--no-renames", "--"),
                await RunAsync(root, ct, "diff", "--cached", "--numstat", "-z", "--no-renames", "--") })
        {
            foreach (var row in stat.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = row.Split('\t', 3);
                if (fields.Length != 3) continue;
                var prior = stats.GetValueOrDefault(fields[2]);
                stats[fields[2]] = (prior.Added + (int.TryParse(fields[0], out var add) ? add : 0),
                    prior.Removed + (int.TryParse(fields[1], out var remove) ? remove : 0));
            }
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
        return new(true, branch, changes, worktrees, branches);
    }

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
