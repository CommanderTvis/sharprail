using System.Collections.Concurrent;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;

namespace SharpRail.Host.Remote;

/// <summary>
/// Resolves each call's workspace from the root the client opened, so clients never share a
/// current project and a reconnecting client needs no server-side session to resume.
/// </summary>
public sealed class ProjectSessions(string defaultRoot, HostStateStore state)
{

    private readonly ConcurrentDictionary<string, ProjectServices> roots = new(StringComparer.Ordinal);

    public IProjectServices For(string? root)
    {
        if (string.IsNullOrEmpty(root) || root.Contains('\0') || !Path.IsPathFullyQualified(root)) root = defaultRoot;
        return roots.GetOrAdd(Path.GetFullPath(root), path => new ProjectServices(path, state));
    }

    /// <summary>A session for resolving a project to open, leaving every cached workspace unchanged.</summary>
    public IProjectServices Detached() => new ProjectServices(defaultRoot, state);
}
