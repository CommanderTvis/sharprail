namespace SharpRail.Host.Abstractions;

/// <summary>What a folder is before a client opens it: a repository, a plain folder Git can be initialised in, or nothing usable.</summary>
public enum ProjectPathKind { Repository, Initable, Missing, NotDirectory }

public partial interface IProjectServices
{
    /// <summary>Classifies a host path without opening it. <c>~</c> is the host user's home; a relative path is refused.</summary>
    ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Starts watching a workspace the client is about to open, so its subscription finds the watcher running. A hint: it never fails for a folder that is gone.</summary>
    ValueTask PrewarmWorkspaceAsync(string path, CancellationToken cancellationToken = default);
}