using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    internal const string FileTransferPrefix = ".sharprail-transfer-";

    public async ValueTask<IReadOnlyList<string>> ImportFilesAsync(FileTransferRequest request, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        string? stage = null;
        var installed = new List<string>();
        string? attachmentDirectory = null;
        try
        {
            var currentRoot = root;
            if (!string.Equals(Path.GetFullPath(request.WorkspaceRoot), currentRoot, StringComparison.Ordinal))
                throw new IOException("The workspace changed before the files were dropped.");
            if (request.Entries.Count is 0 or > 4096 || request.Entries.Sum(entry => (long)entry.Data.Length) > FileLimits.EditableBytes)
                throw new IOException("Drop at most 4096 entries totalling 64 MB.");
            var names = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var entry in request.Entries)
            {
                var parts = entry.Path.Split('/');
                if (entry.Path.Length > 1024 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains('\\') || part.Contains(':') || part.Contains('\0') || part.Equals(".git", StringComparison.OrdinalIgnoreCase)) || !names.Add(entry.Path))
                    throw new IOException("Invalid or repeated upload path: " + entry.Path);
                if (entry.IsDirectory && entry.Data.Length != 0) throw new IOException("A directory cannot contain file bytes.");
            }
            var roots = request.Entries.Select(entry => entry.Path.Split('/')[0]).Distinct(names.Comparer).ToArray();
            string destination;
            if (request.Folder is null)
            {
                if (state?.DirectoryPath is not { } stateDirectory) throw new IOException("This host has no attachment directory.");
                destination = Path.Combine(stateDirectory, "attachments", Guid.NewGuid().ToString("N"));
                attachmentDirectory = destination;
                Directory.CreateDirectory(destination);
                PrivateDirectory(Path.GetDirectoryName(destination)!);
                PrivateDirectory(destination);
            }
            else
            {
                destination = Resolve(currentRoot, request.Folder);
                if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("The drop destination no longer exists.");
                foreach (var name in roots)
                {
                    var target = Resolve(currentRoot, Path.Combine(request.Folder, name));
                    if (Path.Exists(target)) throw new IOException(name + " already exists.");
                }
            }
            stage = Path.Combine(destination, FileTransferPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            PrivateDirectory(stage);
            foreach (var entry in request.Entries.OrderBy(entry => entry.Path.Count(c => c == '/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Contain(stage, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (entry.IsDirectory) Directory.CreateDirectory(path);
                else
                {
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await output.WriteAsync(entry.Data, cancellationToken);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            foreach (var name in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = request.Folder is null ? Path.Combine(destination, name) : Resolve(currentRoot, Path.Combine(request.Folder, name));
                MoveNew(Path.Combine(stage, name), target);
                installed.Add(target);
            }
            return installed.ToArray();
        }
        catch
        {
            foreach (var path in installed) RemoveTransfer(path);
            if (attachmentDirectory is not null) RemoveTransfer(attachmentDirectory);
            throw;
        }
        finally
        {
            try { if (stage is not null && Directory.Exists(stage)) RemoveTransfer(stage); }
            finally { mutations.Release(); }
        }
    }

    private static void PrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void MoveNew(string source, string target)
    {
        if (Path.Exists(target)) throw new IOException(Path.GetFileName(target) + " already exists.");
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target);
    }

    private static void RemoveTransfer(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            if (Directory.Exists(path)) Directory.Delete(path);
            else File.Delete(path);
            return;
        }
        if (Directory.Exists(path))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) RemoveTransfer(child);
            Directory.Delete(path);
        }
        else File.Delete(path);
    }

    private static async Task CopyPathAsync(string currentRoot, string from, string to, CancellationToken token)
    {
        var source = Contain(currentRoot, from);
        var target = Contain(currentRoot, to);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(currentRoot, comparison) || target.Equals(source, comparison) || target.StartsWith(source + Path.DirectorySeparatorChar, comparison))
            throw new IOException("A path cannot be copied into itself.");
        if (Path.Exists(target)) throw new IOException(to + " already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("The drop destination no longer exists.");
        var stage = Path.Combine(Path.GetDirectoryName(target)!, FileTransferPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            async Task Copy(string path, string output, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 128) throw new IOException("The folder nesting is too deep.");
                Contain(currentRoot, Path.GetRelativePath(currentRoot, path));
                if (Directory.Exists(path))
                {
                    Directory.CreateDirectory(output);
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) await Copy(child, Path.Combine(output, Path.GetFileName(child)), depth + 1);
                }
                else
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await using var sink = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await input.CopyToAsync(sink, token);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, File.GetUnixFileMode(path));
            }
            await Copy(source, stage, 0);
            Contain(currentRoot, to);
            MoveNew(stage, target);
        }
        finally { if (Path.Exists(stage)) RemoveTransfer(stage); }
    }
}