using System.Text;
using System.Text.RegularExpressions;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>One client's project session; <paramref name="state"/> receives workspace lifecycle changes.</summary>
public sealed partial class ProjectServices(string initialRoot, HostStateStore? state = null) : IProjectServices
{
    private string root = Path.GetFullPath(initialRoot);
    private readonly SemaphoreSlim mutations = new(1, 1);

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
            if (candidate != root) receipts.Clear();
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
        var path = Resolve(root, relativePath);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var image = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
        var length = new FileInfo(path).Length;
        if ((image || extension is ".md" or ".markdown") && length > FileLimits.PreviewBytes)
            throw new IOException($"Previews are limited to files under {FileLimits.PreviewBytes >> 20} MiB.");
        if (length > FileLimits.EditableBytes) throw new IOException($"Files over {FileLimits.EditableBytes >> 20} MiB cannot be opened.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var info = ContentClassifier.Classify(bytes, relativePath);
        if (image) return new(relativePath, "", bytes) { Info = info };
        // Like the reference, text may contain NUL and other control characters; only invalid UTF-8 is binary.
        try { return new(relativePath, StrictUtf8.GetString(bytes)) { Info = info }; }
        catch (DecoderFallbackException) { throw new IOException("Binary files cannot be previewed."); }
    }

    public async ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all")
        => await GitRepository.SnapshotAsync(root, comparisonBranch, cancellationToken, scope);

    public async ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default)
        => await GitRepository.ListCommitsAsync(root, comparisonBranch, cancellationToken);

    public async ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default)
        => await SpecCatalog.ReadAsync(root, cancellationToken);

    public async ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        Resolve(currentRoot, path);
        if (scope is not ("untracked" or "staged" or "working" or "all" or "uncommitted" or "branch" or "commit")) throw new ArgumentException("Unknown diff scope.");
        if (scope == "untracked") return await AddedDiffAsync(currentRoot, path, cancellationToken);
        if (scope == "uncommitted" &&
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
        if (scope is not ("untracked" or "staged" or "working" or "all" or "uncommitted" or "branch" or "commit")) throw new ArgumentException("Unknown diff scope.");
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
                catch (IOException) when (!cancellationToken.IsCancellationRequested) { throw new FileNotFoundException("The commit does not exist."); }
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
                case "init":
                    if ((await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken)).IsRepository)
                        throw new InvalidOperationException("This folder is already a Git repository.");
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "init", "-b", "main");
                    try
                    {
                        await GitRepository.RunAsync(currentRoot, cancellationToken, "add", "-A");
                        var commit = new List<string>();
                        if (!await HasConfigAsync(currentRoot, "user.name", cancellationToken)) commit.AddRange(["-c", "user.name=SharpRail"]);
                        if (!await HasConfigAsync(currentRoot, "user.email", cancellationToken)) commit.AddRange(["-c", "user.email=sharprail@localhost"]);
                        commit.AddRange(["commit", "--allow-empty", "-m", "Initial commit"]);
                        await GitRepository.RunAsync(currentRoot, cancellationToken, commit.ToArray());
                    }
                    catch
                    {
                        Directory.Delete(Path.Combine(currentRoot, ".git"), recursive: true);
                        throw;
                    }
                    break;
                case "create-worktree":
                    if (string.IsNullOrWhiteSpace(action.Branch)) throw new ArgumentException("Enter a new branch name.");
                    GitRefs.Require(action.Branch);
                    GitRefs.Require(action.BaseBranch);
                    await FetchRemoteAsync(currentRoot, action.BaseBranch, cancellationToken);
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "--end-of-options", action.BaseBranch + "^{commit}");
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "worktree", "add", "-b", action.Branch,
                        "--", Path.GetFullPath(action.Path), action.BaseBranch);
                    break;
                case "remove-worktree":
                    var snapshot = await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken);
                    var target = snapshot.Worktrees.SingleOrDefault(tree => tree.Path == Path.GetFullPath(action.Path));
                    if (target is null || target.IsMain || target.IsLocked || target.Path == currentRoot)
                        throw new InvalidOperationException("The main, active, or locked worktree cannot be removed.");
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "worktree", "remove", "--", target.Path);
                    break;
                default: throw new ArgumentException("Unknown git action.");
            }
            var result = await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken);
            if (action.Kind is "create-worktree" or "remove-worktree" && state is not null && result.Worktrees.FirstOrDefault(tree => tree.IsMain) is { } main)
                state.PublishWorkspaces(main.Path, result.Worktrees.Select(tree => tree.Path).ToArray(),
                    action.Kind == "remove-worktree" ? Path.GetFullPath(action.Path) : null);
            return result;
        }
        finally { mutations.Release(); }
    }

    private static async Task<bool> HasConfigAsync(string currentRoot, string key, CancellationToken cancellationToken)
    {
        try { return (await GitRepository.RunAsync(currentRoot, cancellationToken, "config", key)).Trim().Length > 0; }
        catch (IOException) { return false; }
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