using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    public async ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            if (Path.GetFullPath(request.WorkspaceRoot) != root)
                throw new IOException("The workspace changed. Return to the file's workspace before saving.");
            var external = External(root, request.Path);
            var path = external ?? Resolve(root, request.Path);
            var utf8 = new UTF8Encoding(false, true);
            if (utf8.GetByteCount(request.Text) > FileLimits.EditableBytes)
                throw new IOException($"Editing is limited to files under {FileLimits.EditableBytes >> 20} MiB.");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (utf8.GetString(bytes) != request.OriginalText)
                throw new IOException("The file changed on disk. Your edits have been kept; reconcile the changes before saving.");
            temporary = Path.Combine(Path.GetDirectoryName(path)!, ".sharprail-save-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllBytesAsync(temporary, utf8.GetBytes(request.Text), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck after asynchronous I/O so a workspace/file replacement does
            // not accidentally redirect the write through a newly created symlink.
            if (external is null) Resolve(root, request.Path);
            else if (External(root, request.Path) is null) throw new UnauthorizedAccessException("The file is no longer exposed for editing.");
            if (!(await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(bytes))
                throw new IOException("The file changed while saving. Your edits have been kept.");
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { mutations.Release(); }
        }
    }
}