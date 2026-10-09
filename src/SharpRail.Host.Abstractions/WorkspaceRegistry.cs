namespace SharpRail.Host.Abstractions;

public static class WorkspaceKinds
{
    /// <summary>The project folder itself: user-owned, never renamed, removed or reclaimed.</summary>
    public const string Default = "default";
    /// <summary>A worktree SharpRail created and may remove.</summary>
    public const string Managed = "managed";
    /// <summary>An existing worktree attached in place: user-owned, forgotten but never mutated.</summary>
    public const string External = "external";
}

/// <summary>
/// One registered workspace of a project. <see cref="Id"/> never changes; <see cref="BaseBranch"/> is the ref a
/// managed worktree was cut from, or the repository default for a user-owned one. The display label lives in
/// <see cref="HostState.WorkspaceLabels"/>.
/// </summary>
public sealed record WorkspaceRecord(string Id, string ProjectRoot, string Kind, string Path, string Branch, string BaseBranch)
{
    /// <summary>Set when the host first records the workspace and cleared once its first terminal is reserved.</summary>
    public bool InitialTerminalPending { get; init; }
}

/// <summary>A worktree of the project that no workspace or project represents yet; a detached one has no branch.</summary>
public sealed record ExistingWorktree(string Path, string Branch)
{
    public bool IsDetached => Branch.Length == 0;
}

public sealed record WorkspaceCatalog(IReadOnlyList<WorkspaceRecord> Workspaces, IReadOnlyList<ExistingWorktree> Existing);

public sealed record WorkspaceAction(string Kind, string ProjectRoot = "", string Id = "", string Path = "", string Name = "", string BaseBranch = "")
{
    /// <summary>Cuts a managed worktree; a name becomes the label and derives the branch, none takes the next workspace-N.</summary>
    public static WorkspaceAction Create(string projectRoot, string name = "", string baseBranch = "") => new("create", projectRoot, Name: name, BaseBranch: baseBranch);
    /// <summary>Records a worktree Git already lists, without touching it.</summary>
    public static WorkspaceAction Attach(string projectRoot, string path) => new("attach", projectRoot, Path: path);
    /// <summary>Drops the record and keeps the worktree.</summary>
    public static WorkspaceAction Forget(string id) => new("forget", Id: id);
    /// <summary>Removes a managed worktree unless Git refuses, keeping its branch.</summary>
    public static WorkspaceAction Remove(string id) => new("remove", Id: id);
    /// <summary>Drops a managed workspace and removes its worktree even when it is dirty or locked.</summary>
    public static WorkspaceAction Reclaim(string id) => new("reclaim", Id: id);
    public static WorkspaceAction ReserveTerminal(string id) => new("reserve-terminal", Id: id);
    /// <summary>Shows a workspace's folder in the file manager of the machine the host runs on.</summary>
    public static WorkspaceAction Reveal(string path) => new("reveal", Path: path);
}

/// <summary>
/// A pushed lifecycle change. On the project channel <see cref="Kind"/> is opened or closed; on the workspace
/// channel it is created, updated (both carrying the record) or removed.
/// </summary>
public sealed record LifecycleEvent(string Channel, string Kind, string ProjectRoot, string WorkspaceId = "", WorkspaceRecord? Workspace = null)
{
    public const string Projects = "project";
    public const string Workspaces = "workspace";
}