using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpRail.Host.Core;

/// <summary>The host-owned directories for new worktrees, independent of where projects are checked out.</summary>
public static class WorktreePaths
{
    /// <summary>Returns a stable project directory under the host's state directory; equal names remain distinct.</summary>
    public static string ProjectDirectory(string projectRoot, string? stateDirectory = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var name = Regex.Replace(Path.GetFileName(root).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (name.Length == 0) name = "project";
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..8].ToLowerInvariant();
        var state = stateDirectory ?? Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail");
        return Path.Combine(Path.GetFullPath(state), "worktrees", name + "-" + identity);
    }
}