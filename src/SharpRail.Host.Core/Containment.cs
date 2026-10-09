namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    /// <summary>
    /// The one containment rule for workspace paths: inside the root, never under <c>.git</c>, and no symbolic
    /// link on the way. The leaf may be missing. A read refuses a linked leaf; a write leaves it to its caller,
    /// which replaces or refuses the link and never follows it.
    /// </summary>
    internal static string Contain(string currentRoot, string path, bool write = false)
    {
        var full = Path.GetFullPath(Path.Combine(currentRoot, path));
        var relative = Path.GetRelativePath(currentRoot, full);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("The path is outside this workspace.");
        var segments = relative.Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(write ? "Git's own directory cannot be changed." : "Git's own directory cannot be read.");
        var check = currentRoot;
        foreach (var segment in write ? segments[..^1] : segments)
        {
            check = Path.Combine(check, segment);
            if (new FileInfo(check).LinkTarget is not null)
                throw new UnauthorizedAccessException(write ? "Changes through a symbolic link are not supported." : "Symbolic link previews are not supported.");
        }
        return full;
    }
}