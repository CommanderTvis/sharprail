using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>How a client-supplied project path becomes a host path, and what is found there.</summary>
internal static class ProjectPaths
{
    internal static string Resolve(string path) => Resolve(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string Resolve(string path, string home)
    {
        if (path.Contains('\0')) throw new ArgumentException("Invalid project path.");
        var expanded = path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException($"Project path must be absolute or start with ~/: {path}");
        return Path.GetFullPath(expanded);
    }

    internal static async Task<ProjectPathKind> InspectAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (File.Exists(resolved)) return ProjectPathKind.NotDirectory;
        if (!Directory.Exists(resolved)) return ProjectPathKind.Missing;
        return await ToplevelAsync(resolved, cancellationToken) is null ? ProjectPathKind.Initable : ProjectPathKind.Repository;
    }

    /// <summary>The working tree a folder belongs to, or null when Git finds none there.</summary>
    internal static async Task<string?> ToplevelAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var toplevel = (await GitRepository.RunAsync(directory, cancellationToken, "rev-parse", "--show-toplevel")).TrimEnd('\r', '\n');
            return toplevel.Length > 0 ? toplevel : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>The main worktree that owns a linked worktree, read from its <c>.git</c> file; null for anything else.</summary>
    internal static string? LinkedWorktreeOwner(string directory)
    {
        var (gitDirectory, commonDirectory) = ProjectServices.ResolveGitDirectories(directory);
        return gitDirectory != commonDirectory && Path.GetFileName(commonDirectory) == ".git" ? Path.GetDirectoryName(commonDirectory) : null;
    }
}