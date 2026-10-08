using System.Diagnostics;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    private static readonly (string Id, string Label, string Bin)[] Editors =
    [
        ("vscode", "VS Code", "code"),
        ("emacs", "Emacs", "emacs"),
        ("jetbrains", "IntelliJ IDEA", "idea"),
        ("jetbrains", "WebStorm", "webstorm"),
        ("jetbrains", "PyCharm", "pycharm"),
        ("jetbrains", "GoLand", "goland"),
        ("jetbrains", "Rider", "rider"),
        ("jetbrains", "CLion", "clion"),
        ("jetbrains", "PhpStorm", "phpstorm"),
        ("jetbrains", "RubyMine", "rubymine")
    ];

    public async ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var defaultBase = await DefaultBaseAsync(currentRoot, cancellationToken);
        if (fetchDefault)
        {
            try { await FetchRemoteAsync(currentRoot, defaultBase, cancellationToken); }
            catch (IOException error) { Console.Error.WriteLine(error.Message); }
        }
        var remotes = await RemotesAsync(currentRoot, cancellationToken);
        var refs = (await GitRepository.RunAsync(currentRoot, cancellationToken, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var local = refs.Where(name => name.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(name => name[11..]).ToArray();
        var remote = new List<RemoteBranch>();
        foreach (var name in refs.Where(name => name.StartsWith("refs/remotes/", StringComparison.Ordinal)).Select(name => name[13..]))
        {
            var owner = remotes.Where(candidate => name.StartsWith(candidate + "/", StringComparison.Ordinal)).MaxBy(candidate => candidate.Length);
            if (owner is null || name.Length == owner.Length + 1) continue;
            var branch = name[(owner.Length + 1)..];
            if (branch != "HEAD") remote.Add(new(owner, branch));
        }
        var (path, suggested) = await NextWorkspaceAsync(currentRoot, cancellationToken);
        return new(local, remote, defaultBase) { SuggestedPath = path, SuggestedBranch = suggested };
    }

    public ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default)
    {
        var found = new List<EditorInfo>();
        foreach (var editor in Editors)
            if (!found.Any(item => item.Id == editor.Id) && Which(editor.Bin) is not null) found.Add(new(editor.Id, editor.Label));
        return ValueTask.FromResult<IReadOnlyList<EditorInfo>>(found);
    }

    public async ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(worktreePath);
        var worktrees = GitRepository.ParseWorktrees(await GitRepository.RunAsync(root, cancellationToken, "worktree", "list", "--porcelain", "-z"));
        if (!worktrees.Any(tree => tree.Path == target))
            throw new UnauthorizedAccessException("Only this project's workspaces can be opened in an editor.");
        var bin = Editors.Where(editor => editor.Id == editorId).Select(editor => Which(editor.Bin)).FirstOrDefault(path => path is not null)
            ?? throw new InvalidOperationException($"\"{editorId}\" isn't installed on this host.");
        var start = new ProcessStartInfo(bin) { UseShellExecute = false, WorkingDirectory = target };
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new IOException("Failed to launch the requested application.");
    }

    private static string? Which(string bin)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, bin);
            if (!File.Exists(candidate)) continue;
            if (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return candidate;
        }
        return null;
    }

    private static async Task<string[]> RemotesAsync(string currentRoot, CancellationToken cancellationToken) =>
        (await GitRepository.RunAsync(currentRoot, cancellationToken, "remote")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<string> DefaultBaseAsync(string currentRoot, CancellationToken cancellationToken)
    {
        try
        {
            var head = (await GitRepository.RunAsync(currentRoot, cancellationToken, "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD")).Trim();
            // A missing tracking ref is still the base while origin exists (creation fetches it, as the reference
            // does), but removing the remote leaves origin/HEAD dangling with nothing to fetch it from.
            // The repository's own answer is a ref like any other: a crafted origin/HEAD must not pass unchecked.
            if (head.StartsWith("refs/remotes/", StringComparison.Ordinal) && GitRefs.IsSafe(head[13..]) &&
                (await RemotesAsync(currentRoot, cancellationToken)).Contains("origin"))
                return head[13..];
        }
        catch (IOException) { }
        try
        {
            var main = await MainWorktreeAsync(currentRoot, cancellationToken);
            var branch = (await GitRepository.RunAsync(main, cancellationToken, "symbolic-ref", "--short", "--quiet", "HEAD")).Trim();
            if (GitRefs.IsSafe(branch)) return branch;
        }
        catch (IOException) { }
        return "HEAD";
    }

    private static async Task<string> MainWorktreeAsync(string currentRoot, CancellationToken cancellationToken)
    {
        var list = await GitRepository.RunAsync(currentRoot, cancellationToken, "worktree", "list", "--porcelain", "-z");
        return list.Split('\0').First(field => field.StartsWith("worktree ", StringComparison.Ordinal))[9..];
    }

    private static async Task FetchRemoteAsync(string currentRoot, string reference, CancellationToken cancellationToken)
    {
        var remote = (await RemotesAsync(currentRoot, cancellationToken))
            .Where(candidate => reference.StartsWith(candidate + "/", StringComparison.Ordinal)).MaxBy(candidate => candidate.Length);
        if (remote is null) return;
        var branch = GitRefs.Require(reference)[(remote.Length + 1)..];
        try { await GitRepository.RunAsync(currentRoot, cancellationToken, "fetch", "--quiet", "--no-tags", "--end-of-options", remote, branch); }
        catch (IOException error) { throw new IOException($"Could not fetch {reference}: {error.Message}"); }
    }

    private static async Task<(string Path, string Branch)> NextWorkspaceAsync(string currentRoot, CancellationToken cancellationToken)
    {
        var parent = await MainWorktreeAsync(currentRoot, cancellationToken) + "-worktrees";
        for (var number = 1; ; number++)
        {
            var name = "workspace-" + number;
            var path = Path.Combine(parent, name);
            if (Directory.Exists(path) || File.Exists(path)) continue;
            try { await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "--verify", "--quiet", "refs/heads/" + name); }
            catch (IOException) { return (path, name); }
        }
    }
}