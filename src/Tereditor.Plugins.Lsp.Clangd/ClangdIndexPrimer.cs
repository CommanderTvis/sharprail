using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace SharpRail.Plugins.Lsp.Clangd;

/// <summary>
/// Seeds a new checkout's clangd background index from another checkout of the same repository. clangd keys each shard
/// file by the hash of its source's absolute path and stores absolute paths and URIs inside it, so a plain copy is never
/// read; each shard is instead rewritten with the source root replaced by the target's and saved under the name clangd
/// derives from the new path. Content digests are kept, so clangd loads every shard whose file is unchanged and indexes
/// only the rest. Anything unexpected skips that shard: clangd then indexes the file itself.
/// </summary>
internal static class ClangdIndexPrimer
{
    private const string IndexDirectory = ".cache/clangd/index";
    private const int MaximumDepth = 4;
    private const long MaximumShardBytes = 64L << 20;

    /// <summary>Rewrites every shard of <paramref name="source"/>'s index directories into <paramref name="target"/>; returns how many were written.</summary>
    internal static int Prime(string source, string target, CancellationToken cancellationToken)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (source == target || !Directory.Exists(source) || !Directory.Exists(target)) return 0;
        var written = 0;
        foreach (var index in IndexDirectories(source, cancellationToken))
        {
            var relative = Path.GetRelativePath(source, index);
            var destination = Path.Combine(target, relative);
            // Only where the checkout has the same directory: the index lives beside a compilation database's project root.
            if (!Directory.Exists(Path.GetFullPath(Path.Combine(destination, "..", "..", "..")))) continue;
            foreach (var shard in Directory.EnumerateFiles(index, "*.idx"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { if (Rewrite(shard, source, target, destination)) written++; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
        return written;
    }

    // Bounded walk: the index sits at a compilation database's project root, which is the checkout or a shallow subdirectory.
    private static IEnumerable<string> IndexDirectories(string source, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>([(source, 0)]);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Path.Combine(current.Path, IndexDirectory);
            if (Directory.Exists(index)) yield return index;
            if (current.Depth == MaximumDepth) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current.Path).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" || new DirectoryInfo(child).LinkTarget is not null) continue;
                pending.Push((child, current.Depth + 1));
            }
        }
    }

    /// <summary>The shard file name clangd uses for a source path: its file name, then the hex of its path's XXH3 digest, little-endian.</summary>
    internal static string ShardName(string path) =>
        Path.GetFileName(path) + "." + Convert.ToHexString(BitConverter.GetBytes(XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(path))).AsSpan()) + ".idx";

    private static bool Rewrite(string shard, string source, string target, string destination)
    {
        var bytes = new FileInfo(shard).Length <= MaximumShardBytes ? File.ReadAllBytes(shard) : throw new InvalidDataException("Shard too large.");
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("CdIx"u8))
            throw new InvalidDataException("Not a clangd index shard.");
        var chunks = new List<(byte[] Id, byte[] Data)>();
        for (var at = 12; at < bytes.Length;)
        {
            if (at + 8 > bytes.Length) throw new InvalidDataException("Truncated chunk header.");
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (length < 0 || at + 8 + length > bytes.Length) throw new InvalidDataException("Truncated chunk.");
            chunks.Add((bytes[at..(at + 4)], bytes[(at + 8)..(at + 8 + length)]));
            at += 8 + length + (length & 1);
        }
        var table = chunks.FindIndex(chunk => chunk.Id.AsSpan().SequenceEqual("stri"u8));
        if (table < 0) throw new InvalidDataException("No string table.");
        var (strings, compressed) = ReadStrings(chunks[table].Data);
        var ownName = Path.GetFileName(shard);
        string? own = null;
        var sourceUri = FileUri(source);
        var targetUri = FileUri(target);
        for (var i = 0; i < strings.Length; i++)
        {
            var value = strings[i];
            var path = value.StartsWith("file://", StringComparison.Ordinal) ? Uri.UnescapeDataString(value["file://".Length..]) : value;
            if (own is null && Under(path, source) && ShardName(path) == ownName) own = path;
            strings[i] = Replace(Replace(value, sourceUri, targetUri), source, target);
        }
        if (own is null) return false;
        var output = Path.Combine(destination, ShardName(target + own[source.Length..]));
        if (File.Exists(output)) return false;
        chunks[table] = (chunks[table].Id, WriteStrings(strings, compressed));
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        stream.Write("RIFF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)(4 + chunks.Sum(chunk => 8 + chunk.Data.Length + (chunk.Data.Length & 1))));
        stream.Write(number);
        stream.Write("CdIx"u8);
        foreach (var (id, data) in chunks)
        {
            stream.Write(id);
            BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)data.Length);
            stream.Write(number);
            stream.Write(data);
            if ((data.Length & 1) == 1) stream.WriteByte(0);
        }
        Directory.CreateDirectory(destination);
        // Written beside the shard and moved into place, so clangd never reads a partial file.
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, output, overwrite: false);
        }
        finally { File.Delete(temporary); }
        return true;
    }

    private static bool Under(string path, string root) => path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal) && path[root.Length] == '/';

    // Replaces the root wherever it stands as a whole path prefix, also inside arguments such as -I<root>/include.
    private static string Replace(string value, string from, string to)
    {
        if (!value.Contains(from, StringComparison.Ordinal)) return value;
        var result = new StringBuilder();
        var at = 0;
        for (var found = value.IndexOf(from, StringComparison.Ordinal); found >= 0; found = value.IndexOf(from, at, StringComparison.Ordinal))
        {
            var end = found + from.Length;
            var whole = end == value.Length || value[end] == '/';
            result.Append(value, at, found - at).Append(whole ? to : from);
            at = end;
        }
        return result.Append(value, at, value.Length - at).ToString();
    }

    // clangd's file URI: every byte outside the unreserved set and '/' percent-encoded in upper case.
    private static string FileUri(string path)
    {
        var result = new StringBuilder("file://");
        foreach (var b in Encoding.UTF8.GetBytes(path))
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~' or (byte)'/') result.Append((char)b);
            else result.Append('%').Append(b.ToString("X2"));
        return result.ToString();
    }

    private static (string[] Strings, bool Compressed) ReadStrings(byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("Truncated string table.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data);
        byte[] raw;
        if (size == 0) raw = data[4..];
        else
        {
            if (size > MaximumShardBytes * 4) throw new InvalidDataException("String table too large.");
            raw = new byte[size];
            using var zlib = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
            zlib.ReadExactly(raw);
        }
        if (raw.Length > 0 && raw[^1] != 0) throw new InvalidDataException("Unterminated string table.");
        var strings = raw.Length == 0 ? [] : Encoding.UTF8.GetString(raw, 0, raw.Length - 1).Split('\0');
        return (strings, size != 0);
    }

    private static byte[] WriteStrings(string[] strings, bool compress)
    {
        var raw = new MemoryStream();
        foreach (var value in strings)
        {
            raw.Write(Encoding.UTF8.GetBytes(value));
            raw.WriteByte(0);
        }
        using var output = new MemoryStream();
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, compress ? (uint)raw.Length : 0);
        output.Write(size);
        if (compress)
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zlib);
        else raw.WriteTo(output);
        return output.ToArray();
    }
}