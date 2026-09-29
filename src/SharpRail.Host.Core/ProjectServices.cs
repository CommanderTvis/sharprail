using System.Text;
using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed class ProjectServices(string initialRoot) : IProjectServices
{
    private string root = Path.GetFullPath(initialRoot);
    private readonly SemaphoreSlim mutations = new(1, 1);

    public async ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var candidate = Path.GetFullPath(path);
            if (!Directory.Exists(candidate)) throw new DirectoryNotFoundException(candidate);
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
            if (name is ".git" or ".sharprail" or ".tools") continue;
            files.Add(new(Path.GetRelativePath(currentRoot, entry), name, Directory.Exists(entry)));
        }
        return ValueTask.FromResult<IReadOnlyList<ProjectFile>>(files.OrderByDescending(file => file.IsDirectory)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Resolve(root, relativePath);
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Preview is limited to files under 8 MiB.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")
            return new(relativePath, "", bytes);
        if (bytes.Contains((byte)0)) throw new IOException("Binary files cannot be previewed.");
        return new(relativePath, Encoding.UTF8.GetString(bytes));
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
        if (scope == "untracked") return (await ReadFileAsync(path, cancellationToken)).Text;
        if (scope == "uncommitted" &&
            (await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--others", "--exclude-standard", "-z", "--", path)).Length > 0)
            return (await ReadFileAsync(path, cancellationToken)).Text;
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
                return (await ReadFileAsync(path, cancellationToken)).Text;
            args.Add(baseline);
        }
        args.Add("--"); args.Add(path);
        return await GitRepository.RunAsync(currentRoot, cancellationToken, args.ToArray());
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
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "commit", "--allow-empty", "-m", "Initial commit");
                    break;
                case "create-worktree":
                    if (string.IsNullOrWhiteSpace(action.Branch)) throw new ArgumentException("Enter a new branch name.");
                    await GitRepository.RunAsync(currentRoot, cancellationToken, "check-ref-format", "--branch", action.Branch);
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
            return await GitRepository.SnapshotAsync(currentRoot, "", cancellationToken);
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
