using System.Text.RegularExpressions;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>The workspace registry: which worktrees of a project are workspaces, and who owns them.</summary>
public sealed partial class ProjectServices
{
    /// <summary>Per-workspace scratch space that stays out of Git through its own ignore file.</summary>
    public const string ScratchDirectory = ".sharprail/context";
    private const int NameLimit = 60;
    private const string Detached = "detached HEAD";

    // A session without a host store still answers from a registry of its own.
    private readonly HostStateStore registry = state ?? new HostStateStore(null);

    public async ValueTask<WorkspaceCatalog> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var project = ProjectPath(projectRoot);
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException($"Directory does not exist: {project}");
        // Behind this session's own Git actions too, so folder truth read before an init is not written after it.
        await mutations.WaitAsync(cancellationToken);
        try { return await ListAsync(project, cancellationToken); }
        finally { mutations.Release(); }
    }

    private async Task<WorkspaceCatalog> ListAsync(string project, CancellationToken cancellationToken)
    {
        using var gate = await registry.LockWorkspacesAsync(cancellationToken);
        var truth = await FolderTruthAsync(project, cancellationToken);
        var linked = await LinkedWorktreesAsync(project, cancellationToken);
        var workspaces = registry.ChangeWorkspaces(current =>
        {
            var next = current.ToList();
            var defaults = next.Where(workspace => workspace.ProjectRoot == project && workspace.Kind == WorkspaceKinds.Default).ToArray();
            if (defaults.Length == 0)
            {
                next.Add(new(NewId(), project, WorkspaceKinds.Default, project, truth.Branch, truth.Base) { InitialTerminalPending = true });
                // A project met for the first time keeps the worktrees it already shows: those under the
                // location SharpRail creates in are its own, any other stays the user's.
                var represented = Represented(next);
                foreach (var tree in linked ?? [])
                    if (tree.Branch != Detached && !represented.Contains(tree.Path))
                        next.Add(new(NewId(), project, tree.Path.StartsWith(WorktreesDirectory(project) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                            ? WorkspaceKinds.Managed : WorkspaceKinds.External, tree.Path, tree.Branch, truth.Base));
            }
            foreach (var extra in defaults.Skip(1)) next.Remove(extra);
            // Folder truth: a branch follows its checkout, and a worktree Git no longer lists is gone.
            return next.Select(workspace => workspace.ProjectRoot != project ? workspace
                : workspace.Kind == WorkspaceKinds.Default ? workspace with { Branch = truth.Branch, BaseBranch = truth.Base }
                : linked is null ? workspace
                : linked.FirstOrDefault(entry => entry.Path == workspace.Path) is { } found ? workspace with { Branch = found.Branch } : null).OfType<WorkspaceRecord>();
        });
        var known = Represented(workspaces);
        return new(workspaces.Where(workspace => workspace.ProjectRoot == project).ToArray(),
            (linked ?? []).Where(tree => !known.Contains(tree.Path)).Select(tree => new ExistingWorktree(tree.Path, tree.Branch == Detached ? "" : tree.Branch)).ToArray());
    }

    public async ValueTask<WorkspaceRecord?> ApplyWorkspaceActionAsync(WorkspaceAction action, CancellationToken cancellationToken = default) => action.Kind switch
    {
        "create" => await CreateWorkspaceAsync(ProjectPath(action.ProjectRoot), action.Name, action.BaseBranch, null, null, cancellationToken),
        "attach" => await AttachWorktreeAsync(ProjectPath(action.ProjectRoot), action.Path, cancellationToken),
        "forget" => ForgetWorkspace(action.Id),
        "remove" => await RemoveWorkspaceAsync(action.Id, false, cancellationToken),
        "reclaim" => await RemoveWorkspaceAsync(action.Id, true, cancellationToken),
        "reserve-terminal" => ReserveTerminal(action.Id),
        "reveal" => Reveal(action.Path),
        _ => throw new ArgumentException("Unknown workspace action.")
    };

    private async Task<WorkspaceRecord> CreateWorkspaceAsync(string project, string name, string baseBranch, string? path, string? branch,
        CancellationToken cancellationToken)
    {
        var label = DisplayName(name);
        if (branch is not null && string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Enter a new branch name.");
        var chosen = string.IsNullOrWhiteSpace(baseBranch) ? "HEAD" : baseBranch.Trim();
        // The record names where the branch came from, so an unpicked base is the project's current branch.
        if (chosen == "HEAD" && await CheckoutBranchAsync(project, cancellationToken) is { } head && head != Detached) chosen = head;
        GitRefs.Require(chosen);
        if (branch is not null) GitRefs.Require(branch);
        // The remote-tracking ref is named in full: the shorthand could also resolve a local branch called origin/main.
        var start = await FetchBaseAsync(project, chosen, cancellationToken) ?? chosen;
        using var gate = await registry.LockWorkspacesAsync(cancellationToken);
        if (branch is null)
        {
            var slug = label is null ? "" : BranchSlug(label) is { Length: > 0 } derived ? derived : "workspace";
            branch = slug;
            for (var number = label is null ? 1 : 2; branch.Length == 0 || await NameTakenAsync(project, branch, cancellationToken); number++)
                branch = label is null ? "workspace-" + number : slug + "-" + number;
            path = Path.Combine(WorktreesDirectory(project), branch);
        }
        path = Path.GetFullPath(path!);
        await GitRepository.RunAsync(project, cancellationToken, "check-ref-format", "--branch", branch);
        await GitRepository.RunAsync(project, cancellationToken, "rev-parse", "--verify", "--end-of-options", start + "^{commit}");
        // Without --no-track a remote base would become the branch's upstream, aiming its pushes at the base.
        await GitRepository.RunAsync(project, cancellationToken, "worktree", "add", "--no-track", "-b", branch, "--", path, start);
        EnsureScratchDirectory(path);
        var workspace = new WorkspaceRecord(NewId(), project, WorkspaceKinds.Managed, path, branch, chosen) { InitialTerminalPending = true };
        if (label is not null) await registry.ChangeAsync([HostStateChange.Label(path, label)], cancellationToken);
        // HEAD is wherever the project stood, not a target a later reader could resolve to the same commit.
        if (!string.IsNullOrWhiteSpace(baseBranch) && baseBranch.Trim() != "HEAD") registry.RecordWorkspaceBase(path, chosen);
        registry.ChangeWorkspaces(current => current.Where(known => known.Path != path).Append(workspace));
        return workspace;
    }

    /// <summary>
    /// Fetches a remote base and returns its fully qualified tracking ref, or null for a local one. The guard is the
    /// ref rather than the fetch: a failed fetch does not matter once the ref is there, and a successful one that
    /// never mapped the branch does.
    /// </summary>
    private static async Task<string?> FetchBaseAsync(string project, string reference, CancellationToken cancellationToken)
    {
        var remote = (await RemotesAsync(project, cancellationToken))
            .Where(candidate => reference.StartsWith(candidate + "/", StringComparison.Ordinal)).MaxBy(candidate => candidate.Length);
        if (remote is null) return null;
        var tracking = "refs/remotes/" + reference;
        string? failure = null;
        try { await FetchRemoteAsync(project, reference, cancellationToken); }
        catch (IOException error) { failure = error.Message; }
        // Checked after the fetch whatever its outcome, so a concurrent prefetch that landed the ref wins.
        try { await GitRepository.RunAsync(project, cancellationToken, "rev-parse", "--verify", "--quiet", "--end-of-options", tracking); }
        catch (IOException)
        {
            throw new IOException(failure ?? $"Could not fetch {reference}: {remote} does not map it into refs/remotes — check remote.{remote}.fetch");
        }
        if (failure is not null) Console.Error.WriteLine(failure);
        return tracking;
    }

    private async Task<WorkspaceRecord> AttachWorktreeAsync(string project, string requested, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("An existing worktree path is required.");
        var wanted = Path.GetFullPath(requested);
        try { wanted = (await GitRepository.RunAsync(wanted, cancellationToken, "rev-parse", "--show-toplevel")).TrimEnd('\r', '\n'); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { }
        if (wanted == project) return (await ListWorkspacesAsync(project, cancellationToken)).Workspaces.First(workspace => workspace.Kind == WorkspaceKinds.Default);
        using var gate = await registry.LockWorkspacesAsync(cancellationToken);
        var entry = (await LinkedWorktreesAsync(project, cancellationToken))?.FirstOrDefault(tree => tree.Path == wanted)
            ?? throw new InvalidOperationException("The selected path is not a registered worktree of this project.");
        if (entry.Branch == Detached) throw new InvalidOperationException("Detached HEAD worktrees cannot be opened; create a branch first.");
        var baseBranch = await DefaultBaseAsync(project, cancellationToken);
        if (registry.Current.Projects.Contains(wanted)) throw new InvalidOperationException("This worktree is already open as a SharpRail project.");
        WorkspaceRecord? attached = null;
        registry.ChangeWorkspaces(current =>
        {
            attached = current.FirstOrDefault(workspace => workspace.Path == wanted);
            if (attached is not null)
                return attached.ProjectRoot == project ? current : throw new InvalidOperationException("This worktree is already open under another SharpRail project.");
            attached = new(NewId(), project, WorkspaceKinds.External, wanted, entry.Branch, baseBranch) { InitialTerminalPending = true };
            return current.Append(attached);
        });
        return attached!;
    }

    private WorkspaceRecord? ForgetWorkspace(string id)
    {
        WorkspaceRecord? forgotten = null;
        registry.ChangeWorkspaces(current =>
        {
            forgotten = current.FirstOrDefault(workspace => workspace.Id == id);
            if (forgotten?.Kind == WorkspaceKinds.Default) throw new InvalidOperationException("The Default workspace cannot be removed.");
            return current.Where(workspace => workspace.Id != id);
        });
        return forgotten;
    }

    private async Task<WorkspaceRecord> RemoveWorkspaceAsync(string id, bool reclaim, CancellationToken cancellationToken)
    {
        var workspace = registry.Current.Workspaces.FirstOrDefault(known => known.Id == id) ?? throw new InvalidOperationException($"Unknown workspace: {id}");
        // The fallback below deletes a directory, so it must never see the user's repository or an attached checkout.
        if (workspace.Kind != WorkspaceKinds.Managed || workspace.Path == workspace.ProjectRoot)
            throw new InvalidOperationException(workspace.Kind == WorkspaceKinds.External
                ? "An existing worktree stays on disk; remove it from SharpRail instead." : "The Default workspace cannot be removed.");
        using var gate = await registry.LockWorkspacesAsync(cancellationToken);
        if (!reclaim)
        {
            await GitRepository.RunAsync(workspace.ProjectRoot, cancellationToken, "worktree", "remove", "--", workspace.Path);
            ForgetWorkspace(id);
            DropIndexes(workspace.Path);
            return workspace;
        }
        ForgetWorkspace(id);
        DropIndexes(workspace.Path);
        try { await GitRepository.RunAsync(workspace.ProjectRoot, cancellationToken, "worktree", "remove", "--force", "--", workspace.Path); }
        catch (IOException)
        {
            if (Directory.Exists(workspace.Path)) Directory.Delete(workspace.Path, recursive: true);
            await GitRepository.RunAsync(workspace.ProjectRoot, cancellationToken, "worktree", "prune");
        }
        return workspace;
    }

    /// <summary>A removed worktree takes its spec index and its pre-warmed watcher with it.</summary>
    private static void DropIndexes(string path)
    {
        SpecCatalog.Evict(path);
        WorkspaceWatches.Forget(path);
    }

    private WorkspaceRecord ReserveTerminal(string id)
    {
        var workspaces = registry.ChangeWorkspaces(current => current.Select(workspace => workspace.Id == id ? workspace with { InitialTerminalPending = false } : workspace));
        return workspaces.FirstOrDefault(workspace => workspace.Id == id) ?? throw new InvalidOperationException($"Unknown workspace: {id}");
    }

    private WorkspaceRecord Reveal(string path)
    {
        var workspace = registry.Current.Workspaces.FirstOrDefault(known => known.Path == path)
            ?? throw new UnauthorizedAccessException("Only a workspace can be revealed.");
        var opener = Which(OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open")
            ?? throw new InvalidOperationException("No file manager is available on this host.");
        var start = new System.Diagnostics.ProcessStartInfo(opener) { UseShellExecute = false };
        start.ArgumentList.Add(workspace.Path);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Failed to launch the file manager.");
        return workspace;
    }

    /// <summary>Re-syncs the record of a checkout whose branch moved under it, so every client's rail follows a terminal checkout.</summary>
    private async Task SyncBranchAsync(string path, string branch, CancellationToken cancellationToken)
    {
        var workspace = registry.Current.Workspaces.FirstOrDefault(known => known.Path == path);
        if (workspace is null || workspace.Branch == branch) return;
        var baseBranch = workspace.Kind == WorkspaceKinds.Default ? await DefaultBaseAsync(path, cancellationToken) : workspace.BaseBranch;
        registry.ChangeWorkspaces(current => current.Select(known => known.Path == path ? known with { Branch = branch, BaseBranch = baseBranch } : known));
    }

    /// <summary>Seeds the scratch directory; an existing ignore file is the user's and is left alone.</summary>
    public static void EnsureScratchDirectory(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot)) throw new DirectoryNotFoundException($"Workspace directory is missing: {workspaceRoot}");
        var directory = workspaceRoot;
        foreach (var part in ScratchDirectory.Split('/'))
        {
            directory = Path.Combine(directory, part);
            var entry = new DirectoryInfo(directory);
            // Never followed: a checkout must not redirect the seed outside the workspace.
            if (entry.LinkTarget is not null || File.Exists(directory)) throw new IOException($"Refusing to seed the scratch directory: not a real directory: {directory}");
            if (!entry.Exists) Directory.CreateDirectory(directory);
        }
        try
        {
            using var ignore = new FileStream(Path.Combine(directory, ".gitignore"), FileMode.CreateNew, FileAccess.Write);
            ignore.Write("*\n"u8);
        }
        catch (IOException) when (new FileInfo(Path.Combine(directory, ".gitignore")) is { Exists: true } or { LinkTarget: not null }) { }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string ProjectPath(string projectRoot) => !string.IsNullOrEmpty(projectRoot) && !projectRoot.Contains('\0') && Path.IsPathFullyQualified(projectRoot)
        ? Path.GetFullPath(projectRoot) : throw new ArgumentException("Invalid project path.");

    private static string WorktreesDirectory(string project) => project + "-worktrees";

    private HashSet<string> Represented(IEnumerable<WorkspaceRecord> workspaces) => workspaces.Select(workspace => workspace.Path).Concat(registry.Current.Projects).ToHashSet();

    private static string? DisplayName(string? raw)
    {
        var name = Regex.Replace((raw ?? "").Trim(), @"\s+", " ");
        if (name.Length > NameLimit) name = name[..NameLimit].TrimEnd();
        return name.Length > 0 ? name : null;
    }

    private static string BranchSlug(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").TrimStart('-');
        return (slug.Length > NameLimit ? slug[..NameLimit] : slug).TrimEnd('-');
    }

    // Removal keeps the branch and a renamed label keeps the directory, so a free name needs both free.
    private static async Task<bool> NameTakenAsync(string project, string branch, CancellationToken cancellationToken)
    {
        var path = Path.Combine(WorktreesDirectory(project), branch);
        if (Directory.Exists(path) || File.Exists(path)) return true;
        try { await GitRepository.RunAsync(project, cancellationToken, "show-ref", "--verify", "--quiet", "refs/heads/" + branch); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>The linked worktrees still on disk, or null when the folder is not a repository's main worktree.</summary>
    private static async Task<IReadOnlyList<WorktreeInfo>?> LinkedWorktreesAsync(string project, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(project)) return null;
        List<WorktreeInfo> worktrees;
        try { worktrees = GitRepository.ParseWorktrees(await GitRepository.RunAsync(project, cancellationToken, "worktree", "list", "--porcelain", "-z")); }
        catch (IOException) { return null; }
        if (worktrees.Count > 0 && worktrees[0].Path != project) throw new ArgumentException("Workspaces belong to a project's main folder.");
        return worktrees.Where(tree => !tree.IsMain && Directory.Exists(tree.Path)).ToArray();
    }

    private static async Task<(string Branch, string Base)> FolderTruthAsync(string path, CancellationToken cancellationToken) =>
        await CheckoutBranchAsync(path, cancellationToken) is { } branch ? (branch, await DefaultBaseAsync(path, cancellationToken)) : ("", "");

    /// <summary>The checked-out branch, the detached marker, or null when the folder is no readable checkout.</summary>
    private static async Task<string?> CheckoutBranchAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return null;
        try { return (await GitRepository.RunAsync(path, cancellationToken, "symbolic-ref", "--short", "-q", "HEAD")).Trim() is { Length: > 0 } branch ? branch : Detached; }
        catch (IOException) { }
        try { await GitRepository.RunAsync(path, cancellationToken, "rev-parse", "--git-dir"); return Detached; }
        catch (IOException) { return null; }
    }
}