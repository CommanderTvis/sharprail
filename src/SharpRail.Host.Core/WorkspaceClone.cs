using System.Runtime.InteropServices;

namespace SharpRail.Host.Core;

/// <summary>
/// Warm-start for a created workspace: copy-on-write clones of the Default workspace's untracked build state, so the
/// new worktree begins with an incremental build instead of a cold one. Best effort in every respect: no volume,
/// platform or filesystem that cannot clone, and no failure, ever reaches the caller.
/// </summary>
internal static class WorkspaceClone
{
    /// <summary>Directory names cloned wherever Git reports them as an entirely ignored, untracked directory.</summary>
    internal static readonly string[] BuildDirectoryNames = [".tools", "obj", "bin"];

    private const uint NoFollow = 1;

    [DllImport("libc", EntryPoint = "clonefile", SetLastError = true)]
    private static extern int CloneFile([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    /// <summary>Whether this platform can clone at all; only APFS on macOS does, and the call itself reports the rest.</summary>
    internal static bool Supported => OperatingSystem.IsMacOS();

    /// <summary>
    /// Clones one path (a directory clones its whole tree, symlinks stay symlinks) to a destination that must not exist.
    /// Returns false for any refusal: non-APFS, another volume, an existing destination, a missing source.
    /// </summary>
    internal static bool TryClone(string source, string destination)
    {
        if (!Supported) return false;
        try { return CloneFile(source, destination, NoFollow) == 0; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Clones the Default workspace's build-state directories into <paramref name="target"/> and returns the relative
    /// paths cloned. Discovery is one Git call that reports ignored directories without descending into them (so a
    /// <c>node_modules</c> is one entry, not a walk); only entries named in <see cref="BuildDirectoryNames"/> qualify, and
    /// Git lists a directory as a unit only when nothing in it is tracked. Existing targets are never replaced.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> CloneBuildStateAsync(string source, string target, CancellationToken cancellationToken,
        Func<string, string, bool>? clone = null)
    {
        if (clone is null && !Supported) return [];
        clone ??= TryClone;
        var cloned = new List<string>();
        try
        {
            var listing = await GitRepository.RunAsync(source, cancellationToken, "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z");
            foreach (var entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.EndsWith('/')) continue;
                var relative = entry.TrimEnd('/');
                if (!BuildDirectoryNames.Contains(relative.Split('/')[^1]) || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
                var from = Path.Combine(source, relative);
                var to = Path.Combine(target, relative);
                var parent = Path.GetDirectoryName(to)!;
                // The target's parent comes from the checkout; a path that already exists there (even a dangling link) is left alone.
                if (!Directory.Exists(parent) || Path.Exists(to) || new FileInfo(to).LinkTarget is not null || !Directory.Exists(from)) continue;
                if (clone(from, to)) cloned.Add(relative);
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException) { }
        return cloned;
    }
}