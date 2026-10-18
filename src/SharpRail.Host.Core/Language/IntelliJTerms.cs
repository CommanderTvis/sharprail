using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal static class IntelliJTerms
{
    private sealed record Acceptance(string ArtifactId, string Version, string EulaSha256, string AcceptedAtUtc);

    internal static async Task<IntelliJTermsResult> ReviewAsync(string storage, string installation, IntelliJTermsRequest request, CancellationToken token)
    {
        if (request.Accept && (request.ArtifactId is not { Length: 64 } || request.EulaSha256 is not { Length: 64 }))
            throw new ArgumentException("Acceptance requires the exact artifact and full EULA hash shown to the user.");
        try
        {
            if (installation.Length == 0)
            {
                var os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
                var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "aarch64", Architecture.X64 => "x86_64", _ => "unsupported" };
                var bundle = IntelliJDistribution.Bundle(os, arch);
                installation = Path.Combine(storage, "servers", "intellij", bundle.Version + "-" + bundle.Platform);
            }
            if (!Path.IsPathFullyQualified(installation) || !Directory.Exists(installation))
                return new("unavailable", "Download and review the IntelliJ EULA from workspace settings, or select an existing installation.");
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
            var result = new IntelliJTermsResult("requiresAcceptance", installation, version, artifact, eulaHash, text);
            var records = Path.Combine(storage, "agreements", "intellij");
            var record = Path.Combine(records, artifact + ".json");
            if (File.Exists(record))
            {
                Acceptance? accepted;
                try { accepted = JsonSerializer.Deserialize<Acceptance>(await File.ReadAllTextAsync(record, token)); }
                catch (JsonException) { accepted = null; }
                if (accepted is not null && accepted.ArtifactId == artifact && accepted.Version == version && accepted.EulaSha256 == eulaHash &&
                    DateTimeOffset.TryParse(accepted.AcceptedAtUtc, out _))
                    result = result with { State = "accepted", Accepted = true, AcceptedAtUtc = accepted.AcceptedAtUtc };
            }
            if (!request.Accept) return result;
            if (request.ArtifactId != artifact || request.EulaSha256 != eulaHash)
                return result with { State = "changed", Detail = "The installation or terms changed. Read the updated text and accept again.", Accepted = false, AcceptedAtUtc = "" };
            if (result.Accepted) return result;
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            Directory.CreateDirectory(records);
            var temporary = record + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Acceptance(artifact, version, eulaHash, timestamp)), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, record, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result with { State = "accepted", Accepted = true, AcceptedAtUtc = timestamp };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or DecoderFallbackException or PlatformNotSupportedException)
        {
            return new("unavailable", error.Message);
        }
    }

    private static async Task<byte[]> ReadAsync(string root, string name, int limit, CancellationToken token)
    {
        var file = new FileInfo(Path.Combine(root, name));
        if (file.ResolveLinkTarget(true) is { } target && !LanguageProjectModel.Contains(root, target.FullName)) throw new IOException("IntelliJ terms metadata points outside the installation.");
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

public sealed partial class LanguageService
{
    internal async ValueTask<IntelliJTermsResult> TermsAsync(IntelliJTermsRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace)) throw new ArgumentException("Terms review requires an existing absolute workspace directory.");
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        var options = WorkspaceLanguageConfiguration.Parse(configuration(workspace, "intellij"), "intellij");
        // This dedicated UI operation may prepare the distribution, but never executes it or infers consent.
        var installation = request.Accept ? options.InstallationPath :
            await IntelliJDistribution.PrepareAsync(servers.Storage, options.InstallationPath, LanguageServers.Http, token);
        return await IntelliJTerms.ReviewAsync(servers.Storage, installation, request, token);
    }
}