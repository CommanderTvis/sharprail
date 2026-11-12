namespace SharpRail.Host.Core;

/// <summary>
/// Removes a linked worktree's checkout and its Git registration. A checkout may hold read-only directories
/// (a cloned Go module cache), whose entries neither Git nor a recursive delete can unlink, so once the
/// checkout is known to be removable its directories are made writable first. Symlinks are never followed:
/// cloned tool entries may point into a shared store outside the worktree, which must stay untouched.
/// </summary>
internal static class WorktreeRemoval
{
    /// <summary>Plain removal keeps Git's refusal of uncommitted work (checked before anything on disk changes); forced removal discards it.</summary>
    internal static async Task RemoveAsync(string projectRoot, string path, bool force, CancellationToken cancellationToken)
    {
        if (!force && Directory.Exists(path)
            && (await GitRepository.RunAsync(path, cancellationToken, "status", "--porcelain", "--ignore-submodules=none")).Trim().Length > 0)
            throw new IOException($"'{path}' contains modified or untracked files, use --force to delete it");
        await Task.Run(() => MakeWritable(path), cancellationToken);
        try
        {
            await GitRepository.RunAsync(projectRoot, cancellationToken, force
                ? ["worktree", "remove", "--force", "--", path]
                : ["worktree", "remove", "--", path]);
        }
        catch (IOException error) when (Directory.Exists(path) && error.Message.Contains("failed to delete", StringComparison.Ordinal))
        {
            // Git removed what it could and kept the registration; finish the deletion here and report what stayed.
            await DeleteAndPruneAsync(projectRoot, path, cancellationToken);
        }
    }

    /// <summary>Reclaim discards the checkout whatever it holds, falling back to deleting the directory and pruning.</summary>
    internal static async Task ReclaimAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        await Task.Run(() => MakeWritable(path), cancellationToken);
        try { await GitRepository.RunAsync(projectRoot, cancellationToken, "worktree", "remove", "--force", "--", path); }
        catch (IOException) { await DeleteAndPruneAsync(projectRoot, path, cancellationToken); }
    }

    private static async Task DeleteAndPruneAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        var failures = await Task.Run(() => Delete(path), cancellationToken);
        await GitRepository.RunAsync(projectRoot, cancellationToken, "worktree", "prune");
        if (failures.Count > 0)
            throw new IOException($"The workspace was only partly deleted; {failures.Count} entr{(failures.Count == 1 ? "y" : "ies")} remain under {path}, first: {failures[0]}");
    }

    /// <summary>Adds owner write to every real directory in the tree (and clears read-only attributes on Windows), never through a link.</summary>
    internal static void MakeWritable(string path)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists || root.LinkTarget is not null) return;
        var pending = new Stack<DirectoryInfo>([root]);
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out var directory))
        {
            Unlock(directory);
            FileSystemInfo[] entries;
            try { entries = directory.EnumerateFileSystemInfos("*", options).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (IsLink(entry)) continue;
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (OperatingSystem.IsWindows()) Unlock(entry);
            }
        }
    }

    private static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static void Unlock(FileSystemInfo entry)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
            }
            else if (!entry.UnixFileMode.HasFlag(UnixFileMode.UserWrite)) entry.UnixFileMode |= UnixFileMode.UserWrite;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes the tree bottom-up, unlinking links rather than following them, and returns what could not be deleted.</summary>
    private static List<string> Delete(string path)
    {
        var failures = new List<string>();
        var root = new DirectoryInfo(path);
        if (!root.Exists || IsLink(root)) return failures;
        MakeWritable(path);
        DeleteEntry(root, failures);
        return failures;
    }

    private static void DeleteEntry(FileSystemInfo entry, List<string> failures)
    {
        if (entry is DirectoryInfo directory && !IsLink(directory))
        {
            try
            {
                foreach (var child in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToArray())
                    DeleteEntry(child, failures);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add($"{directory.FullName}: {error.Message}"); return; }
        }
        try
        {
            if (entry is DirectoryInfo folder) folder.Delete(recursive: false);
            else entry.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add($"{entry.FullName}: {error.Message}"); }
    }
}