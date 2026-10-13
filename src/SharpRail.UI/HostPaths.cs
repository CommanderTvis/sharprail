namespace SharpRail.UI;

internal static class HostPaths
{
    internal static bool IsAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\0') &&
        (path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal) ||
         path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\');

    internal static string Relative(string root, string path)
    {
        var windows = root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length >= 2 && root[1] == ':';
        var normalizedRoot = windows ? root.Replace('\\', '/') : root;
        var normalizedPath = windows ? path.Replace('\\', '/') : path;
        var prefix = normalizedRoot.TrimEnd('/') + '/';
        return normalizedPath.StartsWith(prefix, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? normalizedPath[prefix.Length..] : path;
    }
}