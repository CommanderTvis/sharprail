using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

/// <summary>How completely a managed installation is pinned.</summary>
internal enum Pinning
{
    /// <summary>Exact version and a SHA-256 stored in this repository for every platform artifact, verified before extraction.</summary>
    Archive,
    /// <summary>Exact versions of the whole npm graph with the registry integrity hashes of the embedded package-lock.json, installed with <c>npm ci</c>.</summary>
    Lockfile,
    /// <summary>Exact version and a SHA-512 stored in this repository for every NuGet package of the tool (the pointer package and the runtime pack), downloaded and verified by SharpRail before <c>dotnet tool install</c> reads them from a local feed.</summary>
    NuGet,
    /// <summary>Exact version and an embedded <c>go.mod</c>/<c>go.sum</c> whose <c>h1:</c> hashes cover the whole module graph, built with <c>-mod=readonly</c>; Go's checksum database is not bypassed.</summary>
    GoModule,
    /// <summary>Exact version, but integrity is verified only by the package manager's own mechanism, not by a hash stored here.</summary>
    Partial,
    /// <summary>Nothing is installed by SharpRail: the server comes from the host's SDK toolchain.</summary>
    None
}

/// <summary>One downloadable platform artifact of a managed server, with the server it belongs to. <see cref="Source"/> records where the hash was obtained.</summary>
internal sealed record ManagedArtifact(string Server, string Version, string Platform, string Url, string Sha256, string Format, string Source, string[] Hosts);

/// <summary>The pins every provider declares (H21), read from its spec; the host keeps none of its own.</summary>
internal static class ManagedArtifacts
{
    internal static readonly string[] NuGetHosts = ["api.nuget.org", "globalcdn.nuget.org"];
    private static readonly string[] Platforms = ["macos-aarch64", "macos-x86_64", "linux-aarch64", "linux-x86_64", "windows-aarch64", "windows-x86_64"];

    internal static string Platform() => (OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux") + "-" +
        (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x86_64");

    internal static Pinning PinningOf(LanguageServer server) => (Pinning)(int)server.Spec.Pinning;

    /// <summary>The honest one-line description of what pinning a provider has; shown in Settings and the specs.</summary>
    internal static string Describe(LanguageServer server) => server.Spec.PinningDescription.Length > 0 ? server.Spec.PinningDescription : PinningOf(server) switch
    {
        Pinning.Lockfile => "Managed and pinned: exact versions and npm integrity hashes for the whole dependency graph (package-lock.json), install scripts disabled.",
        Pinning.Archive => "Managed and pinned: exact version and a SHA-256 per platform artifact, verified before extraction.",
        Pinning.None => "No managed installation: runs the host SDK's executable only when allowed for this server.",
        _ => "Managed, partially pinned: exact version; integrity is left to the package manager."
    };

    internal static IEnumerable<ManagedArtifact> Artifacts(LanguageServer server) => server.Spec.Artifacts.Select(item =>
        new ManagedArtifact(server.Id, server.Version, item.Platform, item.Url, item.Sha256, item.Format, item.Source, [.. item.Hosts]));

    /// <summary>The verified packages to install on a platform, or null when no runtime pack is pinned for it.</summary>
    internal static LanguageServerNuGetPackage[]? NuGetFor(LanguageServer server, string platform) =>
        server.Spec.NuGetPackages.Any(item => item.Platform == platform) ? [.. server.Spec.NuGetPackages.Where(item => item.Platform is "any" || item.Platform == platform)] : null;

    internal static ManagedArtifact? Find(LanguageServer server, string platform) =>
        Artifacts(server).FirstOrDefault(item => item.Platform == platform || item.Platform == "any");

    /// <summary>One of the server's embedded install inputs, or null.</summary>
    internal static string? Lock(LanguageServer server, string file = "package-lock.json") => server.Spec.LockFiles.GetValueOrDefault(file);

    private static string Short(string hash) => hash[..8];

    /// <summary>The installation directory name: version plus the first digits of the pinned hash, so a bump or a different artifact never reuses a directory.</summary>
    internal static string Directory(LanguageServer server, string platform)
    {
        var kind = PinningOf(server);
        if (kind == Pinning.Lockfile)
            return server.Version + "-" + Short(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Lock(server) ?? ""))));
        if (kind == Pinning.NuGet && NuGetFor(server, platform) is { } packages) return server.Version + "-" + Short(packages[^1].Sha512);
        if (kind == Pinning.GoModule) return server.Version + "-" + Short(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((Lock(server, "go.mod") ?? "") + (Lock(server, "go.sum") ?? "")))));
        if (kind == Pinning.Archive && Find(server, platform) is { } artifact) return server.Version + "-" + Short(artifact.Sha256);
        return server.Version;
    }

    /// <summary>Everything wrong with the managed catalog; empty when each managed provider is pinned as it declares.</summary>
    internal static IReadOnlyList<string> Audit(IEnumerable<LanguageServer> catalog, out IReadOnlyList<string> table)
    {
        var problems = new List<string>();
        var rows = new List<string>();
        foreach (var server in catalog)
        {
            var kind = PinningOf(server);
            var version = server.Version;
            var floating = version.Length == 0 || version is "latest" || version.IndexOfAny(['^', '~', '*', '>', '<', ' ']) >= 0;
            if (floating && kind != Pinning.None) problems.Add($"{server.Id}: version '{version}' is not exact.");
            var detail = "";
            if (kind == Pinning.Archive)
            {
                var items = Artifacts(server).ToArray();
                if (items.Length == 0) problems.Add($"{server.Id}: no artifacts.");
                foreach (var item in items)
                {
                    if (item.Sha256.Length != 64 || !item.Sha256.All(char.IsAsciiHexDigitLower)) problems.Add($"{server.Id} {item.Platform}: missing or malformed SHA-256.");
                    if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !item.Hosts.Contains(uri.Host)) problems.Add($"{server.Id} {item.Platform}: URL is not HTTPS on an allowed host.");
                    if (item.Source.Length == 0) problems.Add($"{server.Id} {item.Platform}: hash source not recorded.");
                    if (!item.Url.Contains(item.Version.Split('-')[0], StringComparison.Ordinal) && server.Id != "rust" || item.Version != server.Version) problems.Add($"{server.Id} {item.Platform}: artifact does not match the catalog version.");
                }
                detail = $"{items.Length} artifact(s), SHA-256 {items.Count(item => item.Sha256.Length == 64)}/{items.Length}";
            }
            else if (kind == Pinning.Lockfile)
            {
                var text = Lock(server);
                if (text is null) problems.Add($"{server.Id}: embedded package-lock.json is missing.");
                else
                {
                    using var document = JsonDocument.Parse(text);
                    var packages = document.RootElement.GetProperty("packages").EnumerateObject().Where(item => item.Name.Length > 0).ToArray();
                    var unhashed = packages.Count(item => !item.Value.TryGetProperty("integrity", out var integrity) || !(integrity.GetString() ?? "").StartsWith("sha512-", StringComparison.Ordinal) ||
                        !item.Value.TryGetProperty("resolved", out var resolved) || !(resolved.GetString() ?? "").StartsWith("https://registry.npmjs.org/", StringComparison.Ordinal));
                    if (unhashed > 0 || packages.Length == 0) problems.Add($"{server.Id}: {unhashed} locked package(s) lack integrity or an npm registry URL.");
                    foreach (var requested in server.Package.Split(' '))
                    {
                        var at = requested.LastIndexOf('@');
                        if (!document.RootElement.GetProperty("packages").TryGetProperty("node_modules/" + requested[..at], out var locked) || locked.GetProperty("version").GetString() != requested[(at + 1)..])
                            problems.Add($"{server.Id}: lockfile does not pin {requested}.");
                    }
                    detail = $"{packages.Length} locked package(s), integrity {packages.Length - unhashed}/{packages.Length}";
                }
            }
            else if (kind == Pinning.NuGet)
            {
                foreach (var platform in Platforms)
                {
                    var packages = NuGetFor(server, platform);
                    if (packages is null || packages.Length != 2) { problems.Add($"{server.Id} {platform}: no pinned NuGet runtime pack."); continue; }
                    foreach (var item in packages)
                        if (item.Sha512.Length != 128 || !item.Sha512.All(char.IsAsciiHexDigitLower) || item.Version != server.Version) problems.Add($"{server.Id} {platform}: {item.Id} has a missing or malformed SHA-512 or the wrong version.");
                }
                detail = $"{server.Spec.NuGetPackages.Count} package(s), SHA-512 {server.Spec.NuGetPackages.Count(item => item.Sha512.Length == 128)}/{server.Spec.NuGetPackages.Count}";
            }
            else if (kind == Pinning.GoModule)
            {
                var module = Lock(server, "go.mod");
                var sums = Lock(server, "go.sum");
                if (module is null || sums is null) problems.Add($"{server.Id}: embedded go.mod or go.sum is missing.");
                else
                {
                    var hashes = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Contains(" h1:", StringComparison.Ordinal));
                    if (!sums.Contains($"golang.org/x/tools/gopls {version} h1:", StringComparison.Ordinal) || !module.Contains($"golang.org/x/tools/gopls {version}", StringComparison.Ordinal)) problems.Add($"{server.Id}: go.mod/go.sum do not pin gopls {version}.");
                    detail = $"{hashes} go.sum hash(es)";
                }
            }
            rows.Add($"{server.Id,-11} {version,-20} {kind,-9} {detail}");
        }
        table = rows;
        return problems;
    }

    internal static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}

/// <summary>Downloads, verifies and safely unpacks one pinned artifact. Nothing is extracted or executed before the SHA-256 matches.</summary>
internal static class ArtifactInstaller
{
    private const long DownloadLimit = 1024L * 1024 * 1024;
    private const long ExtractLimit = 3L * 1024 * 1024 * 1024;

    internal static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Installs into <paramref name="root"/> unless it already holds this artifact; failure leaves no partial directory.</summary>
    internal static async Task InstallAsync(ManagedArtifact artifact, string root, HttpClient http, CancellationToken token)
    {
        if (artifact.Sha256.Length != 64 || !artifact.Sha256.All(char.IsAsciiHexDigitLower))
            throw new IOException($"{artifact.Server} has no pinned SHA-256 for {artifact.Platform}; it will not be installed.");
        var marker = Path.Combine(root, ".sharprail-artifact.json");
        if (File.Exists(marker))
        {
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(marker, token))?.GetValueOrDefault("sha256") == artifact.Sha256) return;
            throw new IOException($"The {artifact.Server} installation at {root} does not match the pinned artifact. Remove it and Retry.");
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!;
        Directory.CreateDirectory(parent);
        var staging = root + ".partial-" + Guid.NewGuid().ToString("N");
        var archive = staging + ".download";
        try
        {
            await DownloadAsync(http, artifact.Server, new Uri(artifact.Url), artifact.Hosts, archive, token);
            if (ManagedArtifacts.Hash(archive) != artifact.Sha256)
                throw new IOException($"{artifact.Server} download failed verification: SHA-256 does not match the pinned value. Nothing was installed.");
            Directory.CreateDirectory(staging);
            await ExtractAsync(archive, staging, artifact, token);
            await File.WriteAllTextAsync(Path.Combine(staging, ".sharprail-artifact.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["sha256"] = artifact.Sha256, ["url"] = artifact.Url }), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, root);
            if (!OperatingSystem.IsWindows())
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    if (new FileInfo(file).LinkTarget is null)
                        File.SetUnixFileMode(file, File.GetUnixFileMode(file) & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) { MakeWritable(staging); Directory.Delete(staging, true); }
        }
    }

    private static void MakeWritable(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            if (new FileInfo(file).LinkTarget is null) File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserWrite);
    }

    /// <summary>Downloads one NuGet package from the flat container into <paramref name="feed"/> and keeps it only when its SHA-512 matches the pinned value.</summary>
    internal static async Task FetchNuGetAsync(HttpClient http, LanguageServerNuGetPackage package, string feed, CancellationToken token)
    {
        if (package.Sha512.Length != 128 || !package.Sha512.All(char.IsAsciiHexDigitLower))
            throw new IOException($"{package.Id} has no pinned SHA-512; it will not be installed.");
        var id = package.Id.ToLowerInvariant();
        var destination = Path.Combine(feed, $"{package.Id}.{package.Version}.nupkg");
        await DownloadAsync(http, package.Id, new Uri($"https://api.nuget.org/v3-flatcontainer/{id}/{package.Version.ToLowerInvariant()}/{id}.{package.Version.ToLowerInvariant()}.nupkg"), ManagedArtifacts.NuGetHosts, destination, token);
        await using var input = File.OpenRead(destination);
        if (Convert.ToHexStringLower(await SHA512.HashDataAsync(input, token)) != package.Sha512)
        {
            input.Close();
            File.Delete(destination);
            throw new IOException($"{package.Id} download failed verification: SHA-512 does not match the pinned value. Nothing was installed.");
        }
    }

    private static async Task DownloadAsync(HttpClient http, string name, Uri start, string[] hosts, string destination, CancellationToken token)
    {
        var url = start;
        for (var hop = 0; hop < 6; hop++)
        {
            if (url.Scheme != "https" || !hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"{name} download refused: {url.Scheme}://{url.Host} is not an allowed HTTPS host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { url = location.IsAbsoluteUri ? location : new Uri(url, location); continue; }
            response.EnsureSuccessStatusCode();
            var final = response.RequestMessage?.RequestUri ?? url;
            if (final.Scheme != "https" || !hosts.Contains(final.Host, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"{name} download refused: redirected to {final.Host}.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyBoundedAsync(input, output, DownloadLimit, token);
            return;
        }
        throw new IOException($"{name} download refused: too many redirects.");
    }

    private static async Task ExtractAsync(string archive, string root, ManagedArtifact artifact, CancellationToken token)
    {
        var links = new List<(string Path, string Target)>();
        long remaining = ExtractLimit;
        var count = 0;
        string EntryPath(string name)
        {
            if (++count > 200_000 || name.Length == 0 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Contains('\0')) throw new IOException("Invalid archive path.");
            if (name.Split('/').Any(part => part is ".." or ".")) throw new IOException("Invalid archive path.");
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!LanguageProjectModel.Contains(root, path)) throw new IOException("Archive entry escapes the installation.");
            return path;
        }
        async Task Write(string path, Stream input, long size, int mode)
        {
            if (size < 0 || size > remaining) throw new IOException("Archive exceeds the extraction limit.");
            remaining -= size;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyBoundedAsync(input, output, size, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1ff) | UnixFileMode.UserRead);
        }
        if (artifact.Format == "gz")
        {
            // A single compressed executable.
            var path = Path.Combine(root, artifact.Server == "rust" ? "rust-analyzer" : artifact.Server);
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyBoundedAsync(gzip, output, ExtractLimit, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0x1ed);
            return;
        }
        if (artifact.Format == "tar.gz")
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (await reader.GetNextEntryAsync(false, token) is { } entry)
            {
                var path = EntryPath(entry.Name);
                if (entry.EntryType is TarEntryType.Directory) Directory.CreateDirectory(path);
                else if (entry.EntryType is TarEntryType.SymbolicLink) links.Add((path, entry.LinkName));
                else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data) await Write(path, data, entry.Length, (int)entry.Mode);
                else if (entry.EntryType is TarEntryType.ExtendedAttributes or TarEntryType.GlobalExtendedAttributes) continue;
                else throw new IOException("Unsupported archive entry.");
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
                if ((mode & 0xf000) is not (0 or 0x8000 or 0x4000 or 0xa000)) throw new IOException("Unsupported archive entry.");
                if ((mode & 0xf000) == 0xa000)
                {
                    if (entry.Length > 4096) throw new IOException("Invalid symbolic link.");
                    using var input = entry.Open();
                    using var text = new StreamReader(input);
                    links.Add((path, await text.ReadToEndAsync(token)));
                }
                else if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(path);
                else { using var input = entry.Open(); await Write(path, input, entry.Length, mode == 0 ? 0x1a4 : mode); }
            }
        }
        foreach (var (path, target) in links)
        {
            token.ThrowIfCancellationRequested();
            if (Path.IsPathFullyQualified(target) || target.Contains('\\') || target.Contains(':') || !LanguageProjectModel.Contains(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target))))
                throw new IOException("Archive symbolic link escapes the installation.");
            if (links.Any(link => link.Path != path && LanguageProjectModel.Contains(link.Path, path))) throw new IOException("Nested archive symbolic link.");
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
            if (limit < 0) throw new IOException("Download or archive exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}