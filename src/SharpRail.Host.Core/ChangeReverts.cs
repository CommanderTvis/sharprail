using System.Security.Cryptography;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    internal const int ReceiptCount = 20;
    internal const long ReceiptBytes = 64L * 1024 * 1024;

    private sealed record FileState(byte[]? Bytes, int? Mode)
    {
        internal string? Hash => Bytes is null ? null : ContentInfo.Sha256Hex(Bytes);
        internal ChangeIdentity Identity => Bytes is null ? new(null, null, null) : new(Hash, Bytes.LongLength, Mode);
    }

    private sealed record ReceiptRecord(ChangeReceipt Receipt, FileState Before);

    // Per workspace: this instance serves one root, and switching roots drops the previous workspace's receipts.
    private readonly List<ReceiptRecord> receipts = [];

    /// <summary>Keeps the newest receipts within the count and held-byte limits, always including the newest.</summary>
    public static List<T> RetainReceipts<T>(IReadOnlyList<T> ring, Func<T, long> size, int count = ReceiptCount, long bytes = ReceiptBytes)
    {
        var retained = new List<T>();
        long total = 0;
        for (var index = ring.Count - 1; index >= 0; index--)
        {
            var held = size(ring[index]);
            if (retained.Count > 0 && (retained.Count >= count || total + held > bytes)) break;
            retained.Insert(0, ring[index]);
            total += held;
        }
        return retained;
    }

    public async ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparisonBranch, RevertTarget target,
        ChangeExpectation expect, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var currentRoot = root;
            if (scope is not ("untracked" or "staged" or "working" or "all" or "uncommitted" or "branch" or "commit" or "pinned")) throw new ArgumentException("Unknown diff scope.");
            if (scope is "commit" or "staged")
                throw new ChangeException(ChangeFailure.ScopeImmutable, "This diff's modified side is not the worktree, so there is nothing to revert.");
            var full = ResolveForWrite(currentRoot, path);
            var original = await OriginalSideAsync(currentRoot, path, scope, comparisonBranch, cancellationToken);
            var modified = WorktreeState(full, path);
            if (original.Hash != expect.OriginalHash || modified.Hash != expect.ModifiedHash)
                throw new ChangeException(ChangeFailure.StaleView,
                    $"The {(original.Hash != expect.OriginalHash ? "original" : "modified")} side of {path} changed since this diff was rendered. Re-read it before reverting.");
            if (original.Bytes is not null && modified.Bytes is not null && original.Hash == modified.Hash && original.Mode != modified.Mode)
                throw Unsupported(path, "has only a mode change, which change mutations do not support.");

            if (!target.IsFile)
            {
                if (target.Original is null || target.Modified is null) throw new ChangeException(ChangeFailure.RangeInvalid, $"A range revert of {path} needs a span on both sides.");
                var next = new FileState(RevertedRange(path, original.Bytes, modified.Bytes, target.Original, target.Modified), modified.Mode);
                WriteAtomic(full, next);
                return Record("revert", path, modified, next);
            }
            if (original.Bytes is null)
            {
                if (modified.Bytes is null) throw new ChangeException(ChangeFailure.RangeInvalid, $"There is no change to revert for {path} in this scope.");
                var claimed = TrashFile(full);
                return Record("revert", path, modified, new(null, null), claimed);
            }
            WriteAtomic(full, original);
            return Record("revert", path, modified, original);
        }
        finally { mutations.Release(); }
    }

    public async ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var index = receipts.FindIndex(held => held.Receipt.Id == receiptId);
            if (index < 0)
                throw new ChangeException(ChangeFailure.ReceiptUnknown, $"This change can no longer be undone (receipt {receiptId} is not held by the host).");
            var held = receipts[index];
            var path = held.Receipt.Path;
            var full = ResolveForWrite(root, path);
            var current = WorktreeState(full, path);
            var modeMoved = held.Receipt.After.Mode is { } moved && current.Mode is { } mode && mode != moved;
            if (current.Hash != expectModifiedHash || modeMoved)
                throw new ChangeException(ChangeFailure.StaleView, $"{path} changed since the change was applied. Nothing was undone.");
            string? trashed = null;
            if (held.Before.Bytes is null)
            {
                if (current.Bytes is not null) trashed = TrashFile(full);
            }
            else WriteAtomic(full, held.Before);
            receipts.RemoveAt(index);
            return Record("undo", path, current, held.Before, trashed);
        }
        finally { mutations.Release(); }
    }

    private ChangeReceipt Record(string kind, string path, FileState before, FileState after, string? trashed = null)
    {
        var receipt = new ChangeReceipt(ReceiptId(), path, kind, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), before.Identity, after.Identity, trashed);
        receipts.Add(new(receipt, before));
        var kept = RetainReceipts(receipts, held => held.Before.Bytes?.LongLength ?? 0);
        receipts.Clear();
        receipts.AddRange(kept);
        return receipt;
    }

    private const string UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Time-sortable, opaque id: 48 bits of milliseconds then 80 random bits in Crockford base32.</summary>
    private static string ReceiptId()
    {
        Span<char> id = stackalloc char[26];
        var time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = 9; index >= 0; index--, time >>= 5) id[index] = UlidAlphabet[(int)(time & 31)];
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        var buffer = 0; var bits = 0; var position = 10;
        foreach (var value in random)
        {
            buffer = (buffer << 8) | value; bits += 8;
            while (bits >= 5) { id[position++] = UlidAlphabet[(buffer >> (bits - 5)) & 31]; bits -= 5; }
        }
        return new(id);
    }

    private static ChangeException Unsupported(string path, string reason) => new(ChangeFailure.UnsupportedChange, $"{path} {reason}");

    /// <summary>The worktree file as it is now; a missing file is absent, while a failed read is an error and never absence.</summary>
    private static FileState WorktreeState(string full, string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(full); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(null, null); }
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw Unsupported(path, "is a symbolic link, which change mutations do not support.");
        if (attributes.HasFlag(FileAttributes.Directory)) throw Unsupported(path, "is a directory, which change mutations do not support.");
        return new(File.ReadAllBytes(full), OperatingSystem.IsWindows() ? 0x1A4 : (int)(File.GetUnixFileMode(full) & (UnixFileMode)0x1FF));
    }

    private static async Task<FileState> OriginalSideAsync(string currentRoot, string path, string scope, string comparison, CancellationToken cancellationToken)
    {
        GitRepository.DiffRange range;
        try { range = await GitRepository.ResolveDiffRangeAsync(currentRoot, path, scope, comparison, cancellationToken); }
        catch (IOException error) when (error is not ChangeException && !cancellationToken.IsCancellationRequested)
        {
            throw new ChangeException(ChangeFailure.StaleView, "The original side of this diff no longer resolves. Re-read it before reverting. " + error.Message);
        }
        if (range.Original is null) return new(null, null);
        var listing = range.Original.Length == 0
            ? await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-files", "--stage", "-z", "--", path)
            : await GitRepository.RunAsync(currentRoot, cancellationToken, "ls-tree", "-z", range.Original, "--", path);
        var entries = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0) return new(null, null);
        if (entries.Length > 1) throw Unsupported(path, "has several index entries, which change mutations do not support.");
        var gitMode = Convert.ToInt32(entries[0][..entries[0].IndexOf(' ')], 8);
        if ((gitMode & 0xF000) == 0xA000) throw Unsupported(path, "is a symbolic link in Git, which change mutations do not support.");
        if ((gitMode & 0xF000) != 0x8000) throw Unsupported(path, "is not a regular Git file, which change mutations do not support.");
        var bytes = await ReadBlobAsync(currentRoot, range.Original, path, cancellationToken)
            ?? throw new IOException($"Could not read a consistent original side for {path}.");
        return new(bytes, (gitMode & 0x49) != 0 ? 0x1ED : 0x1A4);
    }

    private static byte[] RevertedRange(string path, byte[]? original, byte[]? modified, LineSpan originalSpan, LineSpan modifiedSpan)
    {
        if (modified is null)
            throw new ChangeException(ChangeFailure.RangeInvalid, $"{path} is absent from the worktree. Revert the whole file instead of a range.");
        if (!ContentInfo.IsText(modified) || original is not null && !ContentInfo.IsText(original))
            throw new ChangeException(ChangeFailure.RangeInvalid, $"{path} is not text. Only a whole-file revert applies.");
        var originalLines = original is null ? [] : TextSplice.SplitLines(ContentInfo.Decode(original));
        var modifiedLines = TextSplice.SplitLines(ContentInfo.Decode(modified));
        if (!TextSplice.SpanFits(originalSpan, originalLines.Count))
            throw new ChangeException(ChangeFailure.RangeInvalid, $"Lines {originalSpan.Start}+{originalSpan.Count} lie outside the original side of {path} ({originalLines.Count} line(s)).");
        if (!TextSplice.SpanFits(modifiedSpan, modifiedLines.Count))
            throw new ChangeException(ChangeFailure.RangeInvalid, $"Lines {modifiedSpan.Start}+{modifiedSpan.Count} lie outside the worktree side of {path} ({modifiedLines.Count} line(s)).");
        return ContentInfo.Encode(TextSplice.RevertedText(originalLines, modifiedLines, originalSpan, modifiedSpan));
    }

    private static void WriteAtomic(string full, FileState state)
    {
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".sharprail-revert-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temporary, state.Bytes!);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, (UnixFileMode)state.Mode!);
            File.Move(temporary, full, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    /// <summary>
    /// Renames the file to a claim beside it so nothing can write through the original name while it is
    /// trashed, and puts it back (or reports where it was left) if the trash refuses it.
    /// </summary>
    private static string TrashFile(string full)
    {
        var claimed = Path.Combine(Path.GetDirectoryName(full)!, ".sharprail-revert-" + ReceiptId());
        File.Move(full, claimed);
        try { Trash.Move(claimed, Path.GetFileName(full)); }
        catch
        {
            try { File.Move(claimed, full); }
            catch (Exception)
            {
                var recovery = Path.Combine(Path.GetDirectoryName(full)!, ".sharprail-recovery-" + ReceiptId());
                File.Move(claimed, recovery);
                throw new IOException($"The system trash refused {Path.GetFileName(full)}. The claimed file was preserved at {recovery}.");
            }
            throw;
        }
        return claimed;
    }

    /// <summary>
    /// Containment for a path about to be written: inside the workspace, never under <c>.git</c>, no linked
    /// directory on the way, and the leaf may be missing or a link (it is refused later, never followed).
    /// </summary>
    private static string ResolveForWrite(string currentRoot, string path) => Contain(currentRoot, path, write: true);
}