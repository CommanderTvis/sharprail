using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Lsp.IntelliJ;

internal sealed record IntelliJBundle(string Version, string Platform, Uri Url, string Sha256);

internal static class IntelliJDistribution
{
    internal const string Version = "263.7084.0";

    internal const string CatalogSource = "https://github.com/JetBrains/intellij-lsp.nvim/blob/main/lua/intellij-lsp/server_bundles.lua";

    /// <summary>The platforms with a pinned bundle.</summary>
    internal static readonly string[] Platforms = ["macos-aarch64", "macos-x86_64", "linux-aarch64", "linux-x86_64", "windows-aarch64", "windows-x86_64"];

    /// <summary>
    /// The terms inside every pinned bundle: the JetBrains User Agreement as the file <c>EULA.txt</c>. Its SHA-256 was read from the macOS bundle on disk and from the
    /// linux-x86_64 and linux-aarch64 archives streamed from download.jetbrains.com (the archive hashes matched the pins); Windows is not verified separately.
    /// The page under <see cref="TermsUrl"/> is JetBrains' legal index named in the agreement itself.
    /// </summary>
    internal const string TermsSha256 = "cd82e6cdaf2790f1aa1a4e08479828a2cd8d790d14f702f2a6edd884410c9a25";

    internal const string TermsTitle = "JetBrains User Agreement, version 2.0, effective April 16, 2025";

    internal const string TermsUrl = "https://www.jetbrains.com/legal/";

    internal static IntelliJBundle Bundle(string platform)
    {
        var hash = platform switch
        {
            "macos-aarch64" => "ea728da71e3511939b4cb8e4af4aa5e4dda6e0ab7c2bdd1830a5d110bdf2bbec",
            "macos-x86_64" => "3b31cd867ce0e361bfc763ba78cf4fa1dcb1a5324b97c817a0ec045532278420",
            "linux-aarch64" => "d5df0980acc4eeb35b1e76227caa62639fcda188649064d17b5b0de1077b38c0",
            "linux-x86_64" => "3842f2f457e29e58cd9c37a97482b28255d598a2ff142075753d947206c0b922",
            "windows-aarch64" => "2ce197ad10684d55334c5611433c1479ccb574e52d78e41d023e3f2dd0be4704",
            "windows-x86_64" => "d84629cbdd79d58f021a15a75f05018518398906519a0e830df6f8a8d515e286",
            _ => throw new PlatformNotSupportedException($"No IntelliJ distribution for {platform}.")
        };
        var extension = platform.StartsWith("macos", StringComparison.Ordinal) ? ".sit" : platform.StartsWith("windows", StringComparison.Ordinal) ? ".win.zip" : ".tar.gz";
        return new(Version, platform, new($"https://download.jetbrains.com/language-server/intellij-server/{Version}/intellij-server-{Version}{(platform.EndsWith("aarch64", StringComparison.Ordinal) ? "-aarch64" : "")}{extension}"), hash);
    }

    /// <summary>The managed installation directory: <c>servers/intellij/&lt;build&gt;-&lt;platform&gt;</c> beside the host's root for this server.</summary>
    internal static string Managed(ILanguageServerInstallation installation) =>
        Path.Combine(Path.GetDirectoryName(installation.Root)!, Version + "-" + installation.Platform);

    internal static string Installation(ILanguageServerInstallation installation) =>
        installation.InstallationPath.Length > 0 ? installation.InstallationPath : Managed(installation);

    /// <summary>The installation to review without downloading.</summary>
    internal static string Existing(ILanguageServerInstallation installation) => Installation(installation);

    internal static async Task<string> PrepareAsync(ILanguageServerInstallation installation, CancellationToken token)
    {
        if (installation.InstallationPath.Length > 0) return installation.InstallationPath;
        var destination = Managed(installation);
        var installed = await InstallAsync(destination, Bundle(installation.Platform), installation.Http, token);
        _ = Task.Run(() => PruneSuperseded(installed, installation.Platform));
        return installed;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Launched = new();

    /// <summary>Remembers that a session directory (removed when its session ends) runs from this installation, so pruning keeps it.</summary>
    internal static void RecordLaunch(string sessionDirectory, string root) => Launched[sessionDirectory] = root;

    private static bool InUse(string root)
    {
        foreach (var (session, launchedRoot) in Launched)
            if (!Directory.Exists(session)) Launched.TryRemove(session, out _);
            else if (launchedRoot == root) return true;
        return false;
    }

    /// <summary>
    /// After a managed build installed, removes older managed builds of this platform beside it and their <c>.expired</c> markers.
    /// Never touches a user-selected installation, the installed build or one a session in this process runs from; failures are ignored.
    /// </summary>
    internal static void PruneSuperseded(string installed, string platform)
    {
        try
        {
            var parent = Path.GetDirectoryName(installed)!;
            var keep = Path.GetFileName(installed);
            var suffix = "-" + platform;
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                var name = Path.GetFileName(entry);
                var stem = name.EndsWith(".expired", StringComparison.Ordinal) ? name[..^8] : name;
                if (stem == keep || !stem.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (!System.Version.TryParse(stem[..^suffix.Length], out var older) || !System.Version.TryParse(keep[..^suffix.Length], out var current) || older >= current) continue;
                try
                {
                    if (Directory.Exists(entry)) { if (!InUse(entry)) Directory.Delete(entry, true); }
                    else if (name != stem && !InUse(Path.Combine(parent, stem))) File.Delete(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static string EulaHash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static LanguageServerLaunch Launch(ILanguageServerInstallation installation)
    {
        var root = Installation(installation);
        var accepted = installation.AcceptedTerms ?? throw new IOException("IntelliJ terms were not accepted for this artifact.");
        var actual = EulaHash(File.ReadAllBytes(Path.Combine(root, "EULA.txt")));
        if (actual != accepted.TextSha256) throw new IOException("IntelliJ terms changed or were not accepted for this artifact.");
        if (!accepted.Answers.TryGetValue(IntelliJTerms.Region, out var region) || !accepted.Answers.TryGetValue(IntelliJTerms.DataSharing, out var sharing))
            throw new IOException("IntelliJ region and data-sharing choices were not recorded for this artifact.");
        Directory.CreateDirectory(installation.SessionDirectory);
        RecordLaunch(installation.SessionDirectory, root);
        return new(Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server"),
            ["--stdio", "--system-path", installation.SessionDirectory, "--eula", actual[..16]])
        {
            Environment = new Dictionary<string, string?> { ["IJ_LAUNCHER_DEBUG"] = null, ["INTELLIJ_DATA_SHARING"] = sharing, ["INTELLIJ_REGION"] = region }
        };
    }

    internal static async Task<string> InstallAsync(string destination, IntelliJBundle bundle, HttpClient http, CancellationToken token)
    {
        if (bundle.Url.Scheme != "https" || bundle.Url.Host != "download.jetbrains.com") throw new IOException("IntelliJ downloads must originate at JetBrains.");
        if (new[] { bundle.Version, bundle.Platform }.Any(value => value.Length == 0 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))))
            throw new IOException("Invalid IntelliJ artifact identity.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
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
            var saved = JsonSerializer.Deserialize(await File.ReadAllTextAsync(marker, token), IntelliJDistributionJson.Default.Installed);
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
            await File.WriteAllTextAsync(Path.Combine(staging, ".sharprail-artifact.json"), JsonSerializer.Serialize(new Installed(bundle.Sha256, terms), IntelliJDistributionJson.Default.Installed), token);
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

    internal sealed record Installed(string ArchiveSha256, string EulaSha256);

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

    internal static bool Contains(string directory, string file)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
            if (!Contains(root, path)) throw new IOException("IntelliJ archive escapes installation.");
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
            if (Path.IsPathFullyQualified(target) || target.Contains('\\') || target.Contains(':') || !Contains(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target))))
                throw new IOException("IntelliJ symbolic link escapes installation.");
            if (links.Any(link => link.Path != path && Contains(link.Path, path))) throw new IOException("Nested IntelliJ symbolic link.");
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

/// <summary>Source-generated JSON metadata, with the serializer's default options, for the marker an installed distribution keeps.</summary>
[JsonSerializable(typeof(IntelliJDistribution.Installed))]
internal sealed partial class IntelliJDistributionJson : JsonSerializerContext;