using System.Text;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core;

public sealed partial class ProjectServices
{
    public async ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            var saveRoot = SaveRoot(request.WorkspaceRoot);
            var external = External(saveRoot, request.Path);
            var path = external ?? Resolve(saveRoot, request.Path);
            if (GeneratedInstructions.Owns(path)) throw new UnauthorizedAccessException(GeneratedInstructions.Notice);
            var utf8 = new UTF8Encoding(false, true);
            if (utf8.GetByteCount(request.Text) > FileLimits.EditableBytes)
                throw new IOException($"Editing is limited to files under {FileLimits.EditableBytes >> 20} MiB.");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!request.Overwrite && utf8.GetString(bytes) != request.OriginalText)
                throw new HostException(HostErrorCode.FileChangedOnDisk, "The file changed on disk. Your edits have been kept; reconcile the changes before saving.");
            temporary = Path.Combine(Path.GetDirectoryName(path)!, ".tereditor-save-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllBytesAsync(temporary, utf8.GetBytes(request.Text), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck after asynchronous I/O so a workspace/file replacement does
            // not accidentally redirect the write through a newly created symlink.
            if (external is null) Resolve(saveRoot, request.Path);
            else if (External(saveRoot, request.Path) is null) throw new UnauthorizedAccessException("The file is no longer exposed for editing.");
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

    // A save goes to the checkout it was edited in, which may differ from the session's current one (a buffer kept
    // open in another workspace of the same project, saved on quit); anything else is refused.
    private string SaveRoot(string requested)
    {
        var full = Path.GetFullPath(requested);
        if (full == root) return root;
        if (KnownCheckout(full) is { } other && KnownCheckout(root) is { } current && other.ProjectRoot == current.ProjectRoot)
            return other.Root;
        throw new IOException("The file belongs to a workspace of another project. Return to it before saving.");
    }
}