using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpRail.Host.Core.Language;

internal sealed record IntelliJBundle(string Version, string Platform, Uri Url, string Sha256);

internal static class IntelliJDistribution
{
    internal const string Version = "263.6379.0";
    internal const string AcceptanceRequired = "Review and accept the IntelliJ EULA in workspace settings before starting this evaluation provider.";
    internal const string Expired = "JetBrains refused this build (exit 7). Install an authorized newer build and review its terms. Existing Java and Kotlin providers remain available.";
    internal const string CatalogSource = "https://github.com/JetBrains/intellij-lsp.nvim/blob/main/lua/intellij-lsp/server_bundles.lua";

    internal static IntelliJBundle Bundle(string os, string arch)
    {
        var hash = (os, arch) switch
        {
            ("macos", "aarch64") => "443157ce085dae947637594ab883692fa7e952ae0c7d0baa6eb917fc91cabdd1",
            ("macos", "x86_64") => "e5fff8dfa0af523db053bb9cad46529f862116616e1e0d7b2cbaed032eddef15",
            ("linux", "aarch64") => "7b50afa2d6f08cbd9dc39ccd9bf89c03bb37ff256e25243fc229cf8ec10354ea",
            ("linux", "x86_64") => "7529cc733d4a020e1c1369183b2f60e135c85b8f074bbf27d1476a5a7b3839c3",
            ("windows", "aarch64") => "dd160805fbec807c4c9c414f04b67457ec441fd3e3d70bd1a85c321d4b8efbb2",
            ("windows", "x86_64") => "270d02e08268129022ea5a379ce747f04d91a679213614c597361a7a6d0df41d",
            _ => throw new PlatformNotSupportedException($"No IntelliJ distribution for {os}/{arch}.")
        };
        var extension = os == "macos" ? ".sit" : os == "windows" ? ".win.zip" : ".tar.gz";
        return new(Version, os + "-" + arch, new($"https://download.jetbrains.com/language-server/intellij-server/{Version}/intellij-server-{Version}{(arch == "aarch64" ? "-aarch64" : "")}{extension}"), hash);
    }

    internal static IntelliJBundle CurrentBundle() => Bundle(
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            _ => "unsupported"
        });

    internal static string Installation(string storage, string configured)
    {
        if (configured.Length > 0) return configured;
        var bundle = CurrentBundle();
        return Path.Combine(storage, "servers", "intellij", bundle.Version + "-" + bundle.Platform);
    }

    internal static async Task<string> PrepareAsync(string storage, string configured, HttpClient http, CancellationToken token)
    {
        if (configured.Length > 0) return configured;
        var destination = Installation(storage, "");
        if (File.Exists(destination + ".expired")) throw new IntelliJExpiredException();
        return await InstallAsync(storage, CurrentBundle(), http, token);
    }

    internal static string EulaHash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static async Task<ProcessStartInfo> AcknowledgedLaunchAsync(string storage, string installation, string systemPath, CancellationToken token)
    {
        if (File.Exists(installation + ".expired")) throw new IntelliJExpiredException();
        var agreement = await IntelliJTerms.ReviewAsync(storage, installation, new(installation), token);
        if (!agreement.Accepted) throw new IntelliJAcceptanceException();
        return BuildLaunch(installation, systemPath, agreement.EulaSha256);
    }

    private static ProcessStartInfo BuildLaunch(string installation, string systemPath, string acceptedEulaSha256)
    {
        var actual = EulaHash(File.ReadAllBytes(Path.Combine(installation, "EULA.txt")));
        if (actual != acceptedEulaSha256) throw new IOException("IntelliJ terms changed or were not accepted for this artifact.");
        var start = new ProcessStartInfo(Path.Combine(installation, "bin", OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server"));
        foreach (var argument in new[] { "--stdio", "--system-path", systemPath, "--eula", actual[..16] }) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "IJ_LAUNCHER_DEBUG", "INTELLIJ_DATA_SHARING", "INTELLIJ_REGION" }) start.Environment.Remove(name);
        return start;
    }

    internal static void RecordExpiry(string storage, string configured)
    {
        if (configured.Length > 0) return; // Never delete a user-managed installation.
        var installation = Installation(storage, "");
        Directory.CreateDirectory(Path.GetDirectoryName(installation)!);
        File.WriteAllText(installation + ".expired", Expired);
        if (Directory.Exists(installation)) Directory.Delete(installation, true);
    }

    internal static string SystemPath(string storage, string workspace, string processInstance) =>
        Path.Combine(storage, "workspaces", LanguageServers.Key(workspace), "intellij", LanguageServers.Key(processInstance));

    internal static async Task<string> InstallAsync(string storage, IntelliJBundle bundle, HttpClient http, CancellationToken token)
    {
        if (bundle.Url.Scheme != "https" || bundle.Url.Host != "download.jetbrains.com") throw new IOException("IntelliJ downloads must originate at JetBrains.");
        if (new[] { bundle.Version, bundle.Platform }.Any(value => value.Length == 0 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))))
            throw new IOException("Invalid IntelliJ artifact identity.");
        var parent = Path.Combine(storage, "servers", "intellij");
        Directory.CreateDirectory(parent);
        var destination = Path.Combine(parent, bundle.Version + "-" + bundle.Platform);
        FileStream lease;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { lease = new(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) { await Task.Delay(50, token); }
        }
        await using var held = lease;
        var marker = Path.Combine(destination, ".sharprail-artifact.json");
        if (File.Exists(marker))
        {
            var saved = JsonSerializer.Deserialize<Installed>(await File.ReadAllTextAsync(marker, token));
            if (saved is not null && saved.ArchiveSha256 == bundle.Sha256 && Validate(destination, bundle.Version) == saved.EulaSha256) return destination;
            throw new IOException("IntelliJ installation changed; remove the SharpRail-owned installation before retrying.");
        }
        if (Directory.Exists(destination)) throw new IOException("Incomplete IntelliJ installation; remove the SharpRail-owned installation before retrying.");
        var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var archive = staging + ".download";
        try
        {
            using (var response = await http.GetAsync(bundle.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var final = response.RequestMessage?.RequestUri;
                if (final is null || final.Scheme != "https" || final.Host is not ("download.jetbrains.com" or "download-cdn.jetbrains.com"))
                    throw new IOException("Unexpected IntelliJ download redirect.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await CopyBoundedAsync(input, output, 1024L * 1024 * 1024, token);
            }
            await using (var input = File.OpenRead(archive))
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != bundle.Sha256) throw new IOException("IntelliJ archive SHA-256 mismatch.");
            Directory.CreateDirectory(staging);
            await ExtractAsync(archive, staging, bundle.Url.AbsolutePath.EndsWith(".tar.gz", StringComparison.Ordinal), token);
            var terms = Validate(staging, bundle.Version);
            await File.WriteAllTextAsync(Path.Combine(staging, ".sharprail-artifact.json"), JsonSerializer.Serialize(new Installed(bundle.Sha256, terms)), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private sealed record Installed(string ArchiveSha256, string EulaSha256);

    private static string Validate(string root, string version)
    {
        var executable = Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(root, "EULA.txt"))) throw new IOException("IntelliJ archive is missing its launcher or terms.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new IOException("IntelliJ launcher is not executable.");
        using var product = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "product-info.json")));
        if (product.RootElement.GetProperty("buildNumber").GetString() != version || product.RootElement.GetProperty("productCode").GetString() != "ILS")
            throw new IOException("IntelliJ archive product/version mismatch.");
        return EulaHash(File.ReadAllBytes(Path.Combine(root, "EULA.txt")));
    }

    private static async Task ExtractAsync(string archive, string root, bool tar, CancellationToken token)
    {
        var links = new List<(string Path, string Target)>();
        long remaining = 3L * 1024 * 1024 * 1024;
        var count = 0;
        string EntryPath(string name)
        {
            if (++count > 100_000 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/')) throw new IOException("Invalid IntelliJ archive path.");
            var parts = name.Split('/');
            if (!parts[0].StartsWith("intellij-server-", StringComparison.Ordinal) || parts.Any(part => part is ".." or ".")) throw new IOException("Invalid IntelliJ archive root/path.");
            var path = Path.GetFullPath(Path.Combine(root, string.Join('/', parts.Skip(1))));
            if (!LanguageProjectModel.Contains(root, path)) throw new IOException("IntelliJ archive escapes installation.");
            return path;
        }
        async Task Write(string path, Stream input, long size, int mode)
        {
            if (size < 0 || size > remaining) throw new IOException("IntelliJ archive exceeds extraction limit.");
            remaining -= size;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyBoundedAsync(input, output, size, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1ff));
        }
        if (tar)
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (await reader.GetNextEntryAsync(false, token) is { } entry)
            {
                var path = EntryPath(entry.Name);
                if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
                else if (entry.EntryType == TarEntryType.SymbolicLink) links.Add((path, entry.LinkName));
                else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data) await Write(path, data, entry.Length, (int)entry.Mode);
                else throw new IOException("Unsupported IntelliJ archive entry.");
            }
        }
        else
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var path = EntryPath(entry.FullName);
                var mode = entry.ExternalAttributes >> 16;
                if ((mode & 0xf000) is not (0 or 0x8000 or 0x4000 or 0xa000)) throw new IOException("Unsupported IntelliJ archive entry.");
                if ((mode & 0xf000) == 0xa000)
                {
                    using var input = entry.Open();
                    using var reader = new StreamReader(input);
                    if (entry.Length > 4096) throw new IOException("Invalid IntelliJ symbolic link.");
                    links.Add((path, await reader.ReadToEndAsync(token)));
                }
                else if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(path);
                else { using var input = entry.Open(); await Write(path, input, entry.Length, mode == 0 ? 0x1a4 : mode); }
            }
        }
        foreach (var (path, target) in links)
        {
            token.ThrowIfCancellationRequested();
            if (Path.IsPathFullyQualified(target) || target.Contains('\\') || target.Contains(':') || !LanguageProjectModel.Contains(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target))))
                throw new IOException("IntelliJ symbolic link escapes installation.");
            if (links.Any(link => link.Path != path && LanguageProjectModel.Contains(link.Path, path))) throw new IOException("Nested IntelliJ symbolic link.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.CreateSymbolicLink(path, target);
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            limit -= read;
            if (limit < 0) throw new IOException("IntelliJ download/archive exceeds size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}
internal sealed class IntelliJAcceptanceException() : IOException(IntelliJDistribution.AcceptanceRequired);

internal sealed class IntelliJExpiredException() : IOException(IntelliJDistribution.Expired);