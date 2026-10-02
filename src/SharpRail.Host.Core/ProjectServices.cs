using System.Text;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core;

/// <summary>
/// One client's project session; <paramref name="state"/> receives workspace lifecycle changes, and
/// <paramref name="allowsExternalFile"/> admits exact absolute files outside the workspace for reading and saving.
/// </summary>
public sealed partial class ProjectServices(string initialRoot, HostStateStore? state = null, Func<string, string, bool>? allowsExternalFile = null) : IProjectServices
{
    private string root = Path.GetFullPath(initialRoot);
    private readonly SemaphoreSlim mutations = new(1, 1);

    // An absolute path some active plugin exposes for this workspace, or null for an ordinary workspace path.
    private string? External(string currentRoot, string path) =>
        Path.IsPathFullyQualified(path) && allowsExternalFile?.Invoke(currentRoot, path) == true ? path : null;

    public async ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var candidate = Path.GetFullPath(path);
            if (!Directory.Exists(candidate)) throw new DirectoryNotFoundException($"Directory does not exist: {candidate}");
            try { candidate = (await GitRepository.RunAsync(candidate, cancellationToken, "rev-parse", "--show-toplevel")).TrimEnd('\r', '\n'); }
            catch (IOException) { }
            var projectRoot = candidate;
            if (File.Exists(Path.Combine(candidate, ".git")))
            {
                try
                {
                    var worktrees = await GitRepository.RunAsync(candidate, cancellationToken, "worktree", "list", "--porcelain", "-z");
                    var main = worktrees.Split('\0').FirstOrDefault(field => field.StartsWith("worktree ", StringComparison.Ordinal));
                    if (main is not null) projectRoot = main[9..];
                }
                catch (IOException) { }
            }
            root = candidate;
            return new("Default workspace", new DirectoryInfo(root).Name, root) { ProjectRoot = projectRoot };
        }
        finally { mutations.Release(); }
    }

    public ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentRoot = root;
        var directory = Resolve(currentRoot, relativePath);
        var files = new List<ProjectFile>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(entry);
            if (info.LinkTarget is not null) continue;
            var name = Path.GetFileName(entry);
            if (Hidden(name)) continue;
            var path = entry;
            var isDirectory = Directory.Exists(entry);
            while (isDirectory && SingleChildDirectory(path) is { } child)
            {
                path = child; name += "/" + Path.GetFileName(child);
            }
            files.Add(new(Path.GetRelativePath(currentRoot, path), name, isDirectory));
        }
        return ValueTask.FromResult<IReadOnlyList<ProjectFile>>(files.OrderByDescending(file => file.IsDirectory)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool Hidden(string name) => name is ".git" or ".sharprail" or ".tools";

    private static string? SingleChildDirectory(string directory)
    {
        try
        {
            var children = Directory.EnumerateFileSystemEntries(directory).Where(child => !Hidden(Path.GetFileName(child))).Take(2).ToArray();
            return children.Length == 1 && Directory.Exists(children[0]) && new FileInfo(children[0]).LinkTarget is null ? children[0] : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var path = External(currentRoot, relativePath) ?? Resolve(currentRoot, relativePath);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var image = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
        var length = new FileInfo(path).Length;
        if ((image || extension is ".md" or ".markdown") && length > FileLimits.PreviewBytes)
            throw new IOException($"Previews are limited to files under {FileLimits.PreviewBytes >> 20} MiB.");
        if (length > FileLimits.EditableBytes) throw new IOException($"Files over {FileLimits.EditableBytes >> 20} MiB cannot be opened.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (image) return new(relativePath, "", bytes);
        // Like the reference, text may contain NUL and other control characters; only invalid UTF-8 is binary.
        try { return new(relativePath, StrictUtf8.GetString(bytes)); }
        catch (DecoderFallbackException) { throw new IOException("Binary files cannot be previewed."); }
    }

    public async ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all")
        => await GitRepository.SnapshotAsync(root, comparisonBranch, cancellationToken, scope);

    public async ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default)
        => await GitRepository.ListCommitsAsync(root, comparisonBranch, cancellationToken);

    public async ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default)
        => await GitRepository.GetCommitAsync(root, sha, cancellationToken);

    public async ValueTask<IReadOnlyList<WorktreeInfo>> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        // Only a project this host already knows: a remote client must not probe arbitrary paths through it.
        if (state is not null && !state.Current.Projects.Contains(project) && !state.Current.RecentProjects.Contains(project))
            throw new UnauthorizedAccessException("That folder is not a project of this host.");
        if (!Directory.Exists(project)) return [];
        try
        {
            var trees = GitRepository.ParseWorktrees(await GitRepository.RunAsync(project, cancellationToken, "worktree", "list", "--porcelain", "-z"));
            if (trees.Count > 0) return [.. trees.OrderByDescending(tree => tree.IsMain)];
        }
        catch (IOException) { }
        return [new(project, "", true, false)];
    }

    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    public async ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken cancellationToken = default)
    {
        var source = url.Trim();
        if (source.Length == 0 || source.StartsWith('-')) throw new ArgumentException($"Not a repository URL: {url}");
        if (depth is < 1) throw new ArgumentException($"Clone depth must be a whole number of commits, at least 1: {depth}");
        var target = NewFolderTarget(parentPath, name);
        string[] shallow = depth is { } commits ? ["--depth", commits.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [];
        var clone = await GitRepository.RunBoundedAsync(parentPath, ["clone", .. shallow, "--", source, target],
            new(CloneTimeout, Network: true), cancellationToken).ConfigureAwait(false);
        if (clone.Ok) return target;
        try { if (Directory.Exists(target)) Directory.Delete(target, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        throw new IOException(clone.Failure == GitRunFailure.Timeout ? "git clone did not finish within ten minutes."
            : clone.Err.Trim() is { Length: > 0 } reason ? reason : $"git clone failed: {source}");
    }

    // A new project folder: one plain name inside an existing parent, not already taken.
    private static string NewFolderTarget(string parentPath, string name)
    {
        var parent = Path.GetFullPath(parentPath);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException($"The parent folder does not exist: {parentPath}");
        var folder = name.Trim();
        if (folder.Length == 0 || folder is "." or ".." || folder.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException($"Not a folder name: {name}");
        var target = Path.Combine(parent, folder);
        if (Path.Exists(target)) throw new IOException($"{target} already exists.");
        return target;
    }

    public async ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        Resolve(currentRoot, path);
        if (scope is not ("untracked" or "staged" or "working" or "all" or "uncommitted" or "branch" or "commit")) throw new ArgumentException("Unknown diff scope.");
        if (scope == "untracked") return AddedDiff(path, (await ReadFileAsync(path, cancellationToken)).Text);
        if (scope == "uncommitted" &&
            (await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0)
            return AddedDiff(path, (await ReadFileAsync(path, cancellationToken)).Text);
        if (scope == "commit")
        {
            var commit = await GitRepository.CommitDiffArgumentsAsync(currentRoot, comparisonBranch, cancellationToken);
            commit.AddRange(["--no-ext-diff", "--no-color", "--unified=5", "--", path]);
            return await GitRepository.RunAsync(currentRoot, cancellationToken, commit.ToArray());
        }
        var args = new List<string> { "diff", "--no-ext-diff", "--no-color", "--unified=5" };
        if (scope == "staged") args.Add("--cached");
        else if (scope == "uncommitted") args.Add("HEAD");
        else if (scope == "all")
        {
            try { await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "HEAD"); args.Add("HEAD"); }
            catch (IOException) { args.Add("--cached"); }
        }
        else if (scope == "branch")
        {
            var baseline = await GitRepository.ComparisonBaseAsync(currentRoot, comparisonBranch, cancellationToken);
            if ((await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0)
                return AddedDiff(path, (await ReadFileAsync(path, cancellationToken)).Text);
            args.Add(baseline);
        }
        args.Add("--"); args.Add(path);
        return await GitRepository.RunAsync(currentRoot, cancellationToken, args.ToArray());
    }

    /// <summary>Git's unified diff for a new file, which <c>git diff</c> cannot produce for untracked paths.</summary>
    private static string AddedDiff(string path, string text)
    {
        var diff = new StringBuilder($"diff --git a/{path} b/{path}\nnew file mode 100644\n--- /dev/null\n+++ b/{path}\n");
        if (text.Length == 0) return diff.ToString();
        var lines = text.Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        diff.Append(count == 1 ? "@@ -0,0 +1 @@\n" : $"@@ -0,0 +1,{count} @@\n");
        for (var index = 0; index < count; index++) diff.Append('+').Append(lines[index]).Append('\n');
        if (!text.EndsWith('\n')) diff.Append("\\ No newline at end of file\n");
        return diff.ToString();
    }

    public async ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        Resolve(currentRoot, path);
        if (scope is not ("untracked" or "staged" or "working" or "all" or "uncommitted" or "branch" or "commit")) throw new ArgumentException("Unknown diff scope.");
        async Task<string> Blob(string? revision)
        {
            if (revision is null) return "";
            try { return await GitRepository.RunAsync(currentRoot, cancellationToken, "cat-file", "-p", revision + ":./" + path); }
            catch (IOException error) when (error.Message.Contains("does not exist", StringComparison.Ordinal) ||
                error.Message.Contains("exists on disk, but not in", StringComparison.Ordinal))
            { return ""; }
        }
        async Task<string> Working()
        {
            try { return (await ReadFileAsync(path, cancellationToken)).Text; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return ""; }
        }
        async Task<bool> Untracked() =>
            (await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0;
        async Task<string?> Head()
        {
            try { return (await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "HEAD")).Trim(); }
            catch (IOException) when (!cancellationToken.IsCancellationRequested) { return null; }
        }
        if (scope == "untracked" || (scope is "uncommitted" or "branch") && await Untracked()) return new("", await Working());
        switch (scope)
        {
            case "commit":
                var (parent, commit) = await GitRepository.CommitRangeAsync(currentRoot, comparisonBranch, cancellationToken);
                return new(await Blob(parent), await Blob(commit));
            case "staged":
                return new(await Blob(await Head()), await Blob(""));
            case "working":
                return new(await Blob(""), await Working());
            case "branch":
                return new(await Blob(await GitRepository.ComparisonBaseAsync(currentRoot, comparisonBranch, cancellationToken)), await Working());
            case "uncommitted":
                return new(await Blob(await Head()), await Working());
            default:
                return new(await Blob(await Head() ?? ""), await Working());
        }
    }

    public async ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var currentRoot = root;
            switch (action.Kind)
            {
                case "stage":
                    Resolve(currentRoot, action.Path);
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "add", "--", action.Path);
                    break;
                case "unstage":
                    Resolve(currentRoot, action.Path);
                    try { await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "HEAD"); }
                    catch (IOException)
                    {
                        await GitRepository.RunAsync(currentRoot, cancellationToken, "rm", "--cached", "--", action.Path);
                        break;
                    }
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "restore", "--staged", "--", action.Path);
                    break;
                case "create-worktree":
                    try { await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "HEAD"); }
                    catch (IOException)
                    {
                        throw new InvalidOperationException(
                            "This repository has no commits yet, so there is nothing to branch a workspace from. Make the first commit, then try again.");
                    }
                    await FetchRemoteAsync(currentRoot, action.BaseBranch, cancellationToken);
                    if (string.IsNullOrWhiteSpace(action.Branch)) throw new ArgumentException("Enter a new branch name.");
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "check-ref-format", "--branch", action.Branch);
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "--end-of-options", action.BaseBranch + "^{commit}");
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "worktree", "add", "-b", action.Branch,
                        "--", Path.GetFullPath(action.Path), action.BaseBranch);
                    break;
                case "remove-worktree" or "force-remove-worktree":
                    var snapshot = await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken);
                    var target = snapshot.Worktrees.SingleOrDefault(tree => tree.Path == Path.GetFullPath(action.Path));
                    if (target is null || target.IsMain || target.IsLocked || target.Path == currentRoot)
                        throw new InvalidOperationException("The main, active, or locked worktree cannot be removed.");
                    // Forcing discards the workspace's uncommitted and untracked files; the branch is kept either way.
                    if (action.Kind == "force-remove-worktree") await GitRepository.RunAsync(currentRoot, cancellationToken, "worktree", "remove", "--force", "--", target.Path);
                    else await GitRepository.RunAsync(currentRoot, cancellationToken, "worktree", "remove", "--", target.Path);
                    break;
                case "delete-branch":
                    var local = (await GitRepository.RunAsync(currentRoot, cancellationToken, "for-each-ref", "--format=%(refname:short)", "refs/heads"))
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (!local.Contains(action.Branch)) throw new InvalidOperationException($"There is no local branch {action.Branch}.");
                    // The host refuses whatever any client offers: a checked-out branch is a live workspace or the project's own checkout.
                    var holders = GitRepository.ParseWorktrees(await GitRepository.RunAsync(currentRoot, cancellationToken, "worktree", "list", "--porcelain", "-z"));
                    if (holders.FirstOrDefault(tree => tree.Branch == action.Branch) is { } holder)
                        throw new InvalidOperationException(holder.IsMain
                            ? $"{action.Branch} is the branch currently checked out."
                            : $"{action.Branch} is checked out by the workspace at {holder.Path}.");
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "branch", "-D", action.Branch);
                    break;
                case "fetch":
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "fetch", "--all", "--quiet", "--no-prune");
                    break;
                default: throw new ArgumentException("Unknown git action.");
            }
            var result = await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken);
            if (action.Kind is "create-worktree" or "remove-worktree" or "force-remove-worktree" && state is not null && result.Worktrees.FirstOrDefault(tree => tree.IsMain) is { } main)
                state.PublishWorkspaces(main.Path, result.Worktrees.Select(tree => tree.Path).ToArray(),
                    action.Kind is "remove-worktree" or "force-remove-worktree" ? Path.GetFullPath(action.Path) : null);
            return result;
        }
        finally { mutations.Release(); }
    }

    private static string Resolve(string currentRoot, string path)
    {
        var full = Path.GetFullPath(Path.Combine(currentRoot, path));
        var relative = Path.GetRelativePath(currentRoot, full);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("The path is outside this workspace.");
        var check = currentRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            check = Path.Combine(check, segment);
            if (new FileInfo(check).LinkTarget is not null)
                throw new UnauthorizedAccessException("Symbolic link previews are not supported.");
        }
        return full;
    }
}