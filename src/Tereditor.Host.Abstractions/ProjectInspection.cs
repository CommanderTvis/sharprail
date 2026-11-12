namespace SharpRail.Host.Abstractions;

/// <summary>What a folder is before a client opens it: a repository, a plain folder Git can be initialised in, or nothing usable.</summary>
public enum ProjectPathKind { Repository, Initable, Missing, NotDirectory }

/// <summary>One entry of a host directory; <see cref="Size"/> is the file's length in bytes, 0 for a folder.</summary>
public sealed record HostDirectoryEntry(string Name, bool IsDirectory, long Size);

/// <summary>
/// A host directory's entries, folders first then by name ignoring case, with its resolved absolute path, its
/// parent (null at a root) and the host user's home, so a client can browse the host without knowing its layout.
/// </summary>
public sealed record HostDirectoryListing(string Path, string? Parent, string Home, IReadOnlyList<HostDirectoryEntry> Entries);

public partial interface IProjectServices
{
    /// <summary>
    /// Lists a host directory, read-only. <c>~</c> or an empty path is the host user's home; a relative path is refused.
    /// A missing folder throws <see cref="DirectoryNotFoundException"/>, an unreadable one <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    ValueTask<HostDirectoryListing> ListHostDirectoryAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Classifies a host path without opening it. <c>~</c> is the host user's home; a relative path is refused.</summary>
    ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Starts watching a workspace the client is about to open, so its subscription finds the watcher running. A hint: it never fails for a folder that is gone.</summary>
    ValueTask PrewarmWorkspaceAsync(string path, CancellationToken cancellationToken = default);
}