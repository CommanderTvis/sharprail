using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>How a client-supplied project path becomes a host path, and what is found there.</summary>
internal static class ProjectPaths
{
    internal static string Resolve(string path) => Resolve(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string Resolve(string path, string home)
    {
        if (path.Contains('\0')) throw new ArgumentException("Invalid project path.");
        var expanded = path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && path.StartsWith(@"~\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..]) : path;
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException($"Project path must be absolute or start with ~/: {path}");
        return DirectoryIdentity(expanded);
    }

    /// <summary>Lists one host directory for browsing: every entry, folders first, then by name ignoring case.</summary>
    internal static HostDirectoryListing ListDirectory(string path, CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = Resolve(path.Length == 0 ? "~" : path, home);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"No folder at {directory}");
        var entries = new List<HostDirectoryEntry>();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A link is listed as what it points to, so a linked folder can be entered.
            var isDirectory = entry is DirectoryInfo || entry.LinkTarget is not null && Directory.Exists(entry.FullName);
            long size = 0;
            if (!isDirectory) { try { size = new FileInfo(entry.FullName).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            entries.Add(new(entry.Name, isDirectory, size));
        }
        var sorted = entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(directory, Path.GetDirectoryName(directory), home, sorted);
    }

    /// <summary>
    /// The one stored spelling of a directory identity (ThinkRail #775): the host's resolved full path, with the
    /// platform's separators and no trailing separator except at a root such as <c>/</c> or <c>C:\</c>. Case is kept.
    /// </summary>
    internal static string DirectoryIdentity(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    internal static async Task<ProjectPathKind> InspectAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (File.Exists(resolved)) return ProjectPathKind.NotDirectory;
        if (!Directory.Exists(resolved)) return ProjectPathKind.Missing;
        return await ToplevelAsync(resolved, cancellationToken) is null ? ProjectPathKind.Initable : ProjectPathKind.Repository;
    }

    /// <summary>Heals a stored directory written before the one spelling: a well-formed absolute path takes it, anything else stays for validation to judge.</summary>
    internal static string Heal(string path)
    {
        try { return !string.IsNullOrEmpty(path) && !path.Contains('\0') && Path.IsPathFullyQualified(path) ? DirectoryIdentity(path) : path; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>The working tree a folder belongs to, or null when Git finds none there.</summary>
    internal static async Task<string?> ToplevelAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var toplevel = (await GitRepository.RunAsync(directory, cancellationToken, "rev-parse", "--show-toplevel")).TrimEnd('\r', '\n');
            return toplevel.Length > 0 ? DirectoryIdentity(toplevel) : null;
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