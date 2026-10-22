using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Lsp.IntelliJ;

/// <summary>Reads an IntelliJ installation's EULA and fingerprints the whole installation, for the host's terms review.</summary>
internal static class IntelliJTerms
{
    internal static async Task<LanguageServerTerms> ReadAsync(string installation, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(installation) || !Directory.Exists(installation))
            throw new IOException("Download and review the IntelliJ EULA from workspace settings, or select an existing installation.");
        var directory = new DirectoryInfo(installation);
        installation = Path.TrimEndingDirectorySeparator(directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName);
        var eula = await ReadAsync(installation, "EULA.txt", 256 * 1024, token);
        var productBytes = await ReadAsync(installation, "product-info.json", 1024 * 1024, token);
        using var product = JsonDocument.Parse(productBytes);
        if (product.RootElement.ValueKind != JsonValueKind.Object || !product.RootElement.TryGetProperty("productCode", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() != "ILS") throw new IOException("The selected installation is not an IntelliJ language server.");
        if (!product.RootElement.TryGetProperty("buildNumber", out var build) || build.ValueKind != JsonValueKind.String) throw new IOException("Invalid IntelliJ build version.");
        var version = build.GetString() ?? "";
        if (version.Length is 0 or > 128) throw new IOException("Invalid IntelliJ build version.");
        var text = new UTF8Encoding(false, true).GetString(eula);
        var eulaHash = IntelliJDistribution.EulaHash(eula);
        var artifact = await FingerprintAsync(installation, token);
        if (IntelliJDistribution.EulaHash(await ReadAsync(installation, "EULA.txt", 256 * 1024, token)) != eulaHash)
            throw new IOException("The EULA changed while it was being read. Review it again.");
        var currentProduct = await ReadAsync(installation, "product-info.json", 1024 * 1024, token);
        if (!productBytes.AsSpan().SequenceEqual(currentProduct))
            throw new IOException("The IntelliJ build changed while it was being read. Review it again.");
        return new(installation, version, artifact, text, eulaHash);
    }

    private static async Task<byte[]> ReadAsync(string root, string name, int limit, CancellationToken token)
    {
        var file = new FileInfo(Path.Combine(root, name));
        if (file.ResolveLinkTarget(true) is { } target && !IntelliJDistribution.Contains(root, target.FullName)) throw new IOException("IntelliJ terms metadata points outside the installation.");
        await using var input = file.OpenRead();
        if (input.Length > limit) throw new IOException("IntelliJ terms metadata exceeds its size limit.");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static async Task<string> FingerprintAsync(string root, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var length = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        Append(root);
        var pending = new Stack<string>([root]);
        var buffer = new byte[81920];
        long remaining = 8L * 1024 * 1024 * 1024;
        var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Take(100_001).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (++count > 100_000) throw new IOException("IntelliJ installation exceeds its terms-review file limit.");
                var attributes = File.GetAttributes(path);
                Append(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'));
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Append("link");
                    FileSystemInfo link = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
                    Append(link.LinkTarget ?? throw new IOException("Cannot identify IntelliJ installation link."));
                }
                else if ((attributes & FileAttributes.Directory) != 0) { Append("directory"); pending.Push(path); }
                else
                {
                    Append("file");
                    var before = new FileInfo(path);
                    var modified = before.LastWriteTimeUtc;
                    var length = before.Length;
                    remaining -= length;
                    if (remaining < 0) throw new IOException("IntelliJ installation exceeds its terms-review size limit.");
                    Append(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await using var file = File.OpenRead(path);
                    int read;
                    long consumed = 0;
                    while ((read = await file.ReadAsync(buffer, token)) > 0)
                    {
                        consumed += read;
                        if (consumed > length) throw new IOException("IntelliJ installation changed during review. Try again.");
                        hash.AppendData(buffer.AsSpan(0, read));
                    }
                    var after = new FileInfo(path);
                    if (consumed != length || after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("IntelliJ installation changed during review. Try again.");
                }
            }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}