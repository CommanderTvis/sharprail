namespace Tereditor.Host.Abstractions;

/// <summary>Failures a client reacts to specifically. Change writes name theirs with <see cref="ChangeFailure"/>.</summary>
public enum HostErrorCode
{
    /// <summary>A commit named by a scope or revision no longer resolves, so the client resets that scope.</summary>
    UnknownCommit,
    /// <summary>The folder to open as a project is not inside a Git repository.</summary>
    NotGit,
    /// <summary>The folder to open as a project is already held as a workspace of another project.</summary>
    AlreadyOpen,
    /// <summary>A save found different text on disk than the text the editor started from, so the client offers to compare, overwrite or reload.</summary>
    FileChangedOnDisk
}

/// <summary>A named host failure; it reaches local and remote clients as this type, so they branch on <see cref="Code"/> instead of text.</summary>
public sealed class HostException(HostErrorCode code, string message) : IOException(message)
{
    public HostErrorCode Code { get; } = code;
}