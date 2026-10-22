namespace SharpRail.Host.Core.Language;

/// <summary>
/// Which executable a language server would run, and the rules that keep a project from choosing it.
/// A PATH executable is unpinned and unverified and a workspace can influence what PATH finds (an activated virtualenv,
/// <c>node_modules/.bin</c>, relative entries, repository-planted binaries), so PATH is used only when the host's user allowed it
/// for that server, and never when the result lives inside the workspace, its Git root, its project or that project's worktrees.
/// </summary>
internal static class LanguageExecutables
{
    /// <summary>Servers whose executable is the language server itself, so allowing PATH for them is meaningful.</summary>
    internal static bool PathCapable(LanguageServer server) => server.Spec.PathCapable;

    /// <summary>Physical executables the executable checks themselves run as language servers (the checks binary lives in the repository). Empty in the product.</summary>
    internal static readonly HashSet<string> CheckExemptions = new(StringComparer.Ordinal);

    /// <summary>The physical path, every symbolic link resolved; the input when it cannot be resolved.</summary>
    internal static string Real(string path)
    {
        var full = Path.GetFullPath(path);
        if (Posix.RealPath(full) is { } physical) return physical;
        // Windows and unresolvable (missing) paths: resolve the deepest existing prefix by hand.
        var current = full;
        for (var hops = 0; hops < 40; hops++)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null) break;
            current = info.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return current;
    }

    /// <summary>The directories a workspace's executables must never be taken from.</summary>
    internal static string[] ForbiddenRoots(string workspace)
    {
        var roots = new List<string> { Path.GetFullPath(workspace), Real(workspace) };
        if (ProjectServices.KnownCheckout(workspace) is { } known)
        {
            roots.Add(known.Root); roots.Add(known.ProjectRoot); roots.Add(Real(known.ProjectRoot));
            roots.Add(WorktreePaths.ProjectDirectory(known.ProjectRoot));
        }
        for (var parent = new DirectoryInfo(workspace); parent is not null; parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, ".git")))
            {
                // A repository that contains the whole home directory (dotfiles) would forbid the user's own tools.
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (home.Length == 0 || !LanguageProjectModel.Contains(parent.FullName, home)) { roots.Add(parent.FullName); roots.Add(Real(parent.FullName)); }
                break;
            }
        return roots.Select(Path.TrimEndingDirectorySeparator).Where(root => root.Length > 1).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Null when <paramref name="candidate"/> may run; otherwise why not. Judged by the path with every symbolic link resolved.</summary>
    internal static string? Reject(string candidate, string workspace)
    {
        if (!Path.IsPathFullyQualified(candidate)) return $"'{candidate}' is not an absolute path.";
        if (!File.Exists(candidate)) return $"'{candidate}' does not exist or is not a file.";
        var physical = Real(candidate);
        if (!File.Exists(physical) || (File.GetAttributes(physical) & FileAttributes.Directory) != 0) return $"'{candidate}' is not a regular file.";
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(physical) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            return $"'{candidate}' is not executable.";
        if (CheckExemptions.Contains(physical)) return null;
        foreach (var root in ForbiddenRoots(workspace))
            if (LanguageProjectModel.Contains(root, physical))
                return $"'{candidate}' is inside the workspace or its project ({root}); executables from a project are never run.";
        return null;
    }

    /// <summary>
    /// The first PATH match that passes <see cref="Reject"/>. Empty and relative entries are ignored and the workspace is never the
    /// working directory of the search. <paramref name="rejected"/> collects what was skipped inside the workspace.
    /// </summary>
    internal static string? FindOnPath(string executable, string workspace, List<string>? rejected = null, string? path = null)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        foreach (var entry in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry) || !Path.IsPathFullyQualified(entry)) continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(entry, executable + extension);
                if (!File.Exists(candidate)) continue;
                if (Reject(candidate, workspace) is { } problem) { rejected?.Add(problem); continue; }
                return candidate;
            }
        }
        return null;
    }
}

/// <summary>What a server would run, for Settings and the status operation; computed without starting or installing anything.</summary>
internal sealed record ExecutableResolution(string State, string Source, string Path, string Version, string Pinning, string Warning, string Message, bool PathCapable, bool AllowPath)
{
    internal string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, System.Text.Json.JsonSerializerOptions.Web);
}