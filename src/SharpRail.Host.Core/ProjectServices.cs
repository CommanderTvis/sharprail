using System.Text;
using System.Text.RegularExpressions;

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
            var candidate = Path.TrimEndingDirectorySeparator(ProjectPaths.Resolve(path));
            if (!Directory.Exists(candidate)) throw new DirectoryNotFoundException($"Directory does not exist: {candidate}");
            // Switching to a repository or worktree root resolves from its .git files, without starting Git.
            if (KnownCheckout(candidate) is { } known)
            {
                root = known.Root;
                return new("Default workspace", new DirectoryInfo(root).Name, root) { ProjectRoot = known.ProjectRoot };
            }
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
            if (candidate != root) receipts.Clear();
            root = candidate;
            return new("Default workspace", new DirectoryInfo(root).Name, root) { ProjectRoot = projectRoot };
        }
        finally { mutations.Release(); }
    }

    // A non-bare checkout's main worktree is the parent of the shared .git directory, as `git worktree list` reports it first.
    internal static (string Root, string ProjectRoot)? KnownCheckout(string directory)
    {
        if (Posix.RealPath(directory) is not { } physical) return null;
        var (git, common) = ResolveGitDirectories(physical);
        if (git is null || common is null || Path.GetFileName(common) != ".git") return null;
        return Path.GetDirectoryName(common) is { } main && Directory.Exists(main) ? (physical, main) : null;
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

    private static readonly HashSet<string> RasterMedia = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"];

    public async ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var path = External(currentRoot, relativePath) ?? Resolve(currentRoot, relativePath);
        var length = new FileInfo(path).Length;
        if (Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" && length > FileLimits.PreviewBytes)
            throw new IOException($"Previews are limited to files under {FileLimits.PreviewBytes >> 20} MiB.");
        if (length > FileLimits.EditableBytes) throw new IOException($"Files over {FileLimits.EditableBytes >> 20} MiB cannot be opened.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        // The bytes decide what a file is, never its name. A byte-only file answers empty text plus metadata,
        // for the client to describe rather than fail on; a raster picture small enough to preview also carries its bytes.
        var info = ContentClassifier.Classify(bytes, relativePath);
        if (!info.IsText)
            return new(relativePath, "", RasterMedia.Contains(info.MediaType ?? "") && bytes.Length <= FileLimits.PreviewBytes ? bytes : null) { Info = info };
        return new(relativePath, ContentInfo.Decode(bytes)) { Info = info };
    }

    public async ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all")
    {
        var currentRoot = root;
        var snapshot = await GitRepository.SnapshotAsync(currentRoot, comparisonBranch, cancellationToken, scope);
        if (snapshot.IsRepository) await SyncBranchAsync(currentRoot, snapshot.Branch, cancellationToken);
        return snapshot;
    }

    public async ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default)
        => await GitRepository.ListCommitsAsync(root, comparisonBranch, cancellationToken);

    public async ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default)
        => await GitRepository.GetCommitAsync(root, sha, cancellationToken);

    public ValueTask<string> CreateProjectAsync(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = NewFolderTarget(parentPath, name);
        Directory.CreateDirectory(target);
        return ValueTask.FromResult(target);
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
        if (scope is not ("untracked" or "staged" or "working" or "all" or "uncommitted" or "branch" or "commit" or "pinned")) throw new ArgumentException("Unknown diff scope.");
        if (scope == "untracked") return await AddedDiffAsync(currentRoot, path, cancellationToken);
        if (scope is "uncommitted" or "pinned" &&
            (await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0)
            return await AddedDiffAsync(currentRoot, path, cancellationToken);
        if (scope == "commit")
        {
            var commit = await GitRepository.CommitDiffArgumentsAsync(currentRoot, comparisonBranch, cancellationToken);
            commit.AddRange(["--no-ext-diff", "--no-color", "--unified=5", "--", path]);
            return await GitRepository.RunAsync(currentRoot, cancellationToken, commit.ToArray());
        }
        var args = new List<string> { "diff", "--no-ext-diff", "--no-color", "--unified=5" };
        if (scope == "staged") args.Add("--cached");
        else if (scope == "uncommitted") args.Add("HEAD");
        else if (scope == "pinned") args.Add(await GitRepository.PinnedCommitAsync(currentRoot, comparisonBranch, cancellationToken));
        else if (scope == "all")
        {
            try { await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "HEAD"); args.Add("HEAD"); }
            catch (IOException) { args.Add("--cached"); }
        }
        else if (scope == "branch")
        {
            var baseline = await GitRepository.ComparisonBaseAsync(currentRoot, comparisonBranch, cancellationToken);
            if ((await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0)
                return await AddedDiffAsync(currentRoot, path, cancellationToken);
            args.Add(baseline);
        }
        args.Add("--"); args.Add(path);
        return await GitRepository.RunAsync(currentRoot, cancellationToken, args.ToArray());
    }

    private static async Task<string> AddedDiffAsync(string currentRoot, string path, CancellationToken cancellationToken)
    {
        var bytes = await ReadWorkingBytesAsync(currentRoot, path, cancellationToken);
        return ContentClassifier.Classify(bytes, path).IsText
            ? AddedDiff(path, ContentInfo.Decode(bytes))
            : $"diff --git a/{path} b/{path}\nnew file mode 100644\nBinary files /dev/null and b/{path} differ\n";
    }

    private static async Task<byte[]> ReadWorkingBytesAsync(string currentRoot, string path, CancellationToken cancellationToken)
    {
        var full = Resolve(currentRoot, path);
        if (new FileInfo(full).Length > FileLimits.EditableBytes) throw new IOException($"Files over {FileLimits.EditableBytes >> 20} MiB cannot be opened.");
        return await File.ReadAllBytesAsync(full, cancellationToken);
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
        if (scope is not ("untracked" or "staged" or "working" or "all" or "uncommitted" or "branch" or "commit" or "pinned")) throw new ArgumentException("Unknown diff scope.");
        var range = await GitRepository.ResolveDiffRangeAsync(currentRoot, path, scope, comparisonBranch, cancellationToken);
        static (string Text, ContentMetadata Info) Side(byte[]? bytes, string path)
        {
            if (bytes is null) return ("", ContentClassifier.Absent);
            var info = ContentClassifier.Classify(bytes, path);
            return (info.IsText ? ContentInfo.Decode(bytes) : "", info);
        }
        async Task<(string Text, ContentMetadata Info)> Blob(string? revision) =>
            revision is null ? Side(null, path) : Side(await ReadBlobAsync(currentRoot, revision, path, cancellationToken), path);
        async Task<(string Text, ContentMetadata Info)> Working()
        {
            try { return Side(await ReadWorkingBytesAsync(currentRoot, path, cancellationToken), path); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Side(null, path); }
        }
        var original = await Blob(range.Original);
        var modified = range.Modified is null ? await Working() : await Blob(range.Modified);
        return new(original.Text, modified.Text)
        {
            OriginalHash = original.Info.Sha256,
            ModifiedHash = modified.Info.Sha256,
            OriginalCommit = range.Original is { Length: > 0 } commit ? commit : null,
            OriginalInfo = original.Info,
            ModifiedInfo = modified.Info,
            OriginalRevision = original.Info.Sha256 is null ? null : range.Original,
            ModifiedRevision = modified.Info.Sha256 is null ? null : range.Modified
        };
    }

    [GeneratedRegex("^[0-9a-f]{40}(?:[0-9a-f]{24})?$")]
    private static partial Regex ObjectId();

    public async ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        Resolve(currentRoot, path);
        if (revision is { Length: > 0 } && !ObjectId().IsMatch(revision)) throw new ArgumentException("A revision must be a full commit id.");
        byte[]? bytes;
        if (revision is null) bytes = await ReadWorkingBytesAsync(currentRoot, path, cancellationToken);
        else
        {
            if (revision.Length > 0)
                try { await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "--quiet", "--end-of-options", revision + "^{commit}"); }
                catch (IOException) when (!cancellationToken.IsCancellationRequested) { throw new HostException(HostErrorCode.UnknownCommit, $"Unknown commit: {revision}"); }
            var size = long.Parse((await GitRepository.RunAsync(currentRoot, cancellationToken, "cat-file", "-s", revision + ":./" + path)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (size > FileLimits.EditableBytes) throw new IOException($"Files over {FileLimits.EditableBytes >> 20} MiB cannot be opened.");
            bytes = await ReadBlobAsync(currentRoot, revision, path, cancellationToken) ?? throw new FileNotFoundException("The path is not in that revision.");
        }
        return new(bytes, ContentClassifier.Classify(bytes, path));
    }

    /// <summary>A blob's bytes at a revision (empty for the index), or null when the path is not in it.</summary>
    internal static async Task<byte[]?> ReadBlobAsync(string currentRoot, string revision, string path, CancellationToken cancellationToken)
    {
        try { return await GitRepository.RunBytesAsync(currentRoot, cancellationToken, "cat-file", "blob", revision + ":./" + path); }
        catch (IOException error) when (error.Message.Contains("does not exist", StringComparison.Ordinal) ||
            error.Message.Contains("exists on disk, but not in", StringComparison.Ordinal))
        { return null; }
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
                    await CreateWorkspaceAsync(await MainWorktreeAsync(currentRoot, cancellationToken), "", action.BaseBranch, action.Path, action.Branch, cancellationToken);
                    break;
                case "remove-worktree" or "force-remove-worktree":
                    var snapshot = await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken);
                    var target = snapshot.Worktrees.SingleOrDefault(tree => tree.Path == Path.GetFullPath(action.Path));
                    if (target is null || target.IsMain || target.IsLocked || target.Path == currentRoot)
                        throw new InvalidOperationException("The main, active, or locked worktree cannot be removed.");
                    if (registry.Current.Workspaces.Any(workspace => workspace.Path == target.Path && workspace.Kind == WorkspaceKinds.External))
                        throw new InvalidOperationException("An existing worktree stays on disk; remove it from SharpRail instead.");
                    using (await registry.LockWorkspacesAsync(cancellationToken))
                    {
                        await GitRepository.RunAsync(currentRoot, cancellationToken, ["worktree", "remove", .. (action.Kind == "force-remove-worktree" ? new[] { "--force" } : []), "--", target.Path]);
                        registry.ChangeWorkspaces(current => current.Where(workspace => workspace.Path != target.Path));
                    }
                    DropIndexes(target.Path);
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
            return await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken);
        }
        finally { mutations.Release(); }
    }

    private static async Task<bool> HasConfigAsync(string currentRoot, string key, CancellationToken cancellationToken)
    {
        try { return (await GitRepository.RunAsync(currentRoot, cancellationToken, "config", key)).Trim().Length > 0; }
        catch (IOException) { return false; }
    }

    private static string Resolve(string currentRoot, string path) => Contain(currentRoot, path);
}