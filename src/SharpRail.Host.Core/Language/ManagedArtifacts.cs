using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

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

/// <summary>One downloadable platform artifact of a managed server. <see cref="Source"/> records where the hash was obtained.</summary>
/// <summary>One NuGet package of a managed tool with its SHA-512 (hex), taken from the package's NuGet catalog entry (<c>packageHash</c>).</summary>
internal sealed record ManagedNuGetPackage(string Id, string Version, string Platform, string Sha512);

internal sealed record ManagedArtifact(string Server, string Version, string Platform, string Url, string Sha256, string Format, string Source, string[] Hosts);

internal static class ManagedArtifacts
{
    private static readonly string[] GitHub = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    private static readonly string[] Eclipse = ["download.eclipse.org"];
    private static readonly string[] JetBrains = ["download.jetbrains.com", "download-cdn.jetbrains.com"];
    internal static readonly string[] NuGetHosts = ["api.nuget.org", "globalcdn.nuget.org"];
    private static readonly string[] Platforms = ["macos-aarch64", "macos-x86_64", "linux-aarch64", "linux-x86_64", "windows-aarch64", "windows-x86_64"];

    internal static string Platform() => (OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux") + "-" +
        (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x86_64");

    /// <summary>Servers installed from verified archives.</summary>
    internal static readonly string[] ArchiveServers = ["clangd", "java", "kotlin", "powershell", "rust"];

    /// <summary>Servers installed with <c>npm ci</c> from an embedded lockfile.</summary>
    internal static readonly string[] LockfileServers = ["typescript", "python", "php", "css", "shell"];

    internal static Pinning PinningOf(string id) => ArchiveServers.Contains(id) ? Pinning.Archive : LockfileServers.Contains(id) ? Pinning.Lockfile :
        id == "csharp" ? Pinning.NuGet : id == "go" ? Pinning.GoModule :
        id is "swift" or "dart" or "intellij" ? Pinning.None : Pinning.Partial;

    /// <summary>The honest one-line description of what pinning a provider has; shown in Settings and the specs.</summary>
    internal static string Describe(string id) => id switch
    {
        "go" => "Managed and pinned: exact gopls version with an embedded go.mod/go.sum (h1 hashes of the whole module graph), built read-only; Go's checksum database stays on.",
        "ruby" => "Managed, partially pinned: exact gem version; RubyGems does not verify a stored hash and dependencies are resolved at install.",
        "scala" => "Managed, partially pinned: exact Metals version; Coursier resolves dependencies at install without a stored hash.",
        "r" => "Managed, partially pinned: exact CRAN package version; CRAN publishes no verified hash and dependencies are resolved at install.",
        "csharp" => "Managed and pinned: exact version and a SHA-512 per NuGet package (tool and runtime pack) verified before a local-feed install; NuGet signature verification also applies.",
        "swift" or "dart" => "No managed installation: runs the host SDK's executable only when allowed for this server.",
        "intellij" => "Managed and fully pinned: exact build, SHA-256 per platform, verified before extraction; acceptance of its terms is required.",
        _ when LockfileServers.Contains(id) => "Managed and pinned: exact versions and npm integrity hashes for the whole dependency graph (package-lock.json), install scripts disabled.",
        _ => "Managed and pinned: exact version and a SHA-256 per platform artifact, verified before extraction."
    };

    internal static readonly ManagedArtifact[] Artifacts =
    [
        .. new[]
        {
            ("macos-aarch64", "mac", "1082e6638223b785ca2daf0939f13afcd0bb95c84ee9a4bbaff4745365159253"),
            ("macos-x86_64", "mac", "1082e6638223b785ca2daf0939f13afcd0bb95c84ee9a4bbaff4745365159253"),
            ("linux-x86_64", "linux", "e53b1a96196095faedb7642cf64964f7fb9ad4a0c1f00dd2c172a3d9dcbafdfd"),
            ("windows-x86_64", "windows", "23412a240756a162e7b98a282f36aa2a23a88db5ce16a0cbc4fef7253768c810")
        }.Select(item => new ManagedArtifact("clangd", "23.1.0", item.Item1, $"https://github.com/clangd/clangd/releases/download/23.1.0/clangd-{item.Item2}-23.1.0.zip", item.Item3, "zip",
            "https://api.github.com/repos/clangd/clangd/releases/tags/23.1.0 (asset digest)", GitHub)),
        .. new[]
        {
            ("macos-aarch64", "aarch64-apple-darwin", "gz", "a2e45e828b37bfc4b507c2ca456e8b61096e0bdfbae225f3d33295546d1eeacf"),
            ("macos-x86_64", "x86_64-apple-darwin", "gz", "56a5698a2e0d495c3ec28155416db310c879f21a70f6f38db549166cf157cfab"),
            ("linux-aarch64", "aarch64-unknown-linux-gnu", "gz", "d7219d5d8f5537bb227b521157aebddfe7e4a90f4a85a74f31e53722f5691025"),
            ("linux-x86_64", "x86_64-unknown-linux-gnu", "gz", "040cab4e2abc490fadd95c3f6cbc60b941a78761dbc3acf0ec66fd7a97f31db3"),
            ("windows-aarch64", "aarch64-pc-windows-msvc", "zip", "b053585692c2848acfa8e9ddf5f85bdc3741f3b23c51b72717ba0b391910b5fe"),
            ("windows-x86_64", "x86_64-pc-windows-msvc", "zip", "77f7a907f0e6f179281bac38e2d08435c974db9a5b99de52c5c963aff52883ec")
        }.Select(item => new ManagedArtifact("rust", "2026-10-12", item.Item1, $"https://github.com/rust-lang/rust-analyzer/releases/download/2026-10-12/rust-analyzer-{item.Item2}.{item.Item3}", item.Item4, item.Item3,
            "https://api.github.com/repos/rust-lang/rust-analyzer/releases/tags/2026-10-12 (asset digest)", GitHub)),
        new("powershell", "4.7.0", "any", "https://github.com/PowerShell/PowerShellEditorServices/releases/download/v4.7.0/PowerShellEditorServices.zip",
            "5084f0326cc88539e9d880b08f6c52ce5be4672bb8ecee8921e35f8895a6b9d7", "zip", "https://api.github.com/repos/PowerShell/PowerShellEditorServices/releases/tags/v4.7.0 (asset digest)", GitHub),
        new("java", "1.61.0", "any", "https://download.eclipse.org/jdtls/milestones/1.61.0/jdt-language-server-1.61.0-202609031315.tar.gz",
            "338e7e73d61836651ba2453919a0d34fa763eb4e7c03342092309bffb8934c64", "tar.gz", "https://download.eclipse.org/jdtls/milestones/1.61.0/jdt-language-server-1.61.0-202609031315.tar.gz.sha256", Eclipse),
        .. new[]
        {
            ("macos-x86_64", "", ".sit", "zip", "e69e0c9d27b915b2db9ee692ec08d097df1a394d9f21190457ef199f2905f77e"),
            ("macos-aarch64", "-aarch64", ".sit", "zip", "ebef2e13cd4adc4ec9e04084b848000a3ec7a9d2917c64f269574ce2efe9ecad"),
            ("linux-x86_64", "", ".tar.gz", "tar.gz", "ab8ca4455dc2fc5fe1a24db2bccc46c104254d2c465155c4251ee65df8f3f7cc"),
            ("linux-aarch64", "-aarch64", ".tar.gz", "tar.gz", "50999901ef8bcfa1e58561b6a8d782a72dea5620fcf92a64130807f8924a56fc"),
            ("windows-x86_64", "", ".win.zip", "zip", "72148fa0832c2a45a4b26c55f56197d8d8886bcdb732b910607efc5e2693b9df"),
            ("windows-aarch64", "-aarch64", ".win.zip", "zip", "290f48cb564295239bb678bb66b1ebbd06755b14fd4890f3cdf822f41af011ef")
        }.Select(item => new ManagedArtifact("kotlin", "263.6379.0", item.Item1,
            $"https://download.jetbrains.com/language-server/kotlin-server/263.6379.0/kotlin-server-263.6379.0{item.Item2}{item.Item3}", item.Item5, item.Item4,
            $"https://download.jetbrains.com/language-server/kotlin-server/263.6379.0/kotlin-server-263.6379.0{item.Item2}{item.Item3}.sha256", JetBrains))
    ];

    private const string RoslynVersion = "5.13.0-1.26507.4";

    /// <summary>The roslyn-language-server packages: the pointer package for every platform, then the platform's runtime pack. SHA-512 values are the NuGet catalog <c>packageHash</c> of each package.</summary>
    internal static readonly ManagedNuGetPackage[] NuGetPackages =
    [
        new("roslyn-language-server", RoslynVersion, "any", "2a8d6137b84bacd70c1ac7aedfcd07d824fd6c9b534a6df11205561cd09233516d1506caa50d61c01bbbcb8d86b951af1464b6a3ce8c33bea68e007f4c8b14a2"),
        new("roslyn-language-server.osx-arm64", RoslynVersion, "macos-aarch64", "bce48bacb85855e5514f08264e8d0fa80fc70b4ae5ad032ab5528982f3c7ac24185e65935d44cba55e16918ae5927257b9afa3eead752f8f849d5f7c9c4151c8"),
        new("roslyn-language-server.osx-x64", RoslynVersion, "macos-x86_64", "c42e3763f10a410bb8010a46a18815fcc08adf1c654dbb5d208bf1ea133bb2331b05fafadb86aa477b0b479e6c036530b099288646840607079bf19e3c4ff787"),
        new("roslyn-language-server.linux-x64", RoslynVersion, "linux-x86_64", "3254f932367167bf4c523e33a86b1430d68c6defce2a8643c5ffd30d642a3268167a9d58619bfe8ff2b01be3c6d2def1c98d007fddb5b8e25fc61db7ccd21d59"),
        new("roslyn-language-server.linux-arm64", RoslynVersion, "linux-aarch64", "3c12c729d46b8edfa8ff0f07a2fc0714633c61b3bce44fc34b371ae64c753bbda01312468816524ad6172331f53b31ea3b0a43dcccd74935200b00b94499587a"),
        new("roslyn-language-server.win-x64", RoslynVersion, "windows-x86_64", "78c65815370a89cf0fa5c2293f45b16e9523e1debf0a9ad6963d7baf4a1711867c5e6d4e38d9d58a973bfcd386d3742dddd636f569bf542da2881f5609e8e3ad"),
        new("roslyn-language-server.win-arm64", RoslynVersion, "windows-aarch64", "2c4693fba6baa715ab7295a5c7400b283a320fbb9cbcd9f1c2b3181eb965e91a4c9dc30c60bc5cfda5e5a94f2d2b5ca18bc5ee86a81a467bb7d2a10ac4953b1d")
    ];

    /// <summary>The verified packages to install on a platform, or null when no runtime pack is pinned for it.</summary>
    internal static ManagedNuGetPackage[]? NuGetFor(string platform) =>
        NuGetPackages.Any(item => item.Platform == platform) ? [.. NuGetPackages.Where(item => item.Platform is "any" || item.Platform == platform)] : null;

    internal static ManagedArtifact? Find(string server, string platform) =>
        Artifacts.FirstOrDefault(item => item.Server == server && (item.Platform == platform || item.Platform == "any"));

    /// <summary>The embedded npm lockfile text of a server, or null.</summary>
    internal static string? Lock(string server, string file = "package-lock.json")
    {
        using var stream = typeof(ManagedArtifacts).Assembly.GetManifestResourceStream($"Locks/{server}/{file}");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Short(string hash) => hash[..8];

    /// <summary>The installation directory name: version plus the first digits of the pinned hash, so a bump or a different artifact never reuses a directory.</summary>
    internal static string Directory(LanguageServer server, string platform)
    {
        if (LockfileServers.Contains(server.Id))
            return server.Version + "-" + Short(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Lock(server.Id) ?? ""))));
        if (server.Id == "csharp" && NuGetFor(platform) is { } packages) return server.Version + "-" + Short(packages[^1].Sha512);
        if (server.Id == "go") return server.Version + "-" + Short(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((Lock("go", "go.mod") ?? "") + (Lock("go", "go.sum") ?? "")))));
        if (ArchiveServers.Contains(server.Id) && Find(server.Id, platform) is { } artifact) return server.Version + "-" + Short(artifact.Sha256);
        return server.Version;
    }

    /// <summary>Everything wrong with the managed catalog; empty when each managed provider is pinned as it declares.</summary>
    internal static IReadOnlyList<string> Audit(IEnumerable<LanguageServer> catalog, out IReadOnlyList<string> table)
    {
        var problems = new List<string>();
        var rows = new List<string>();
        foreach (var server in catalog)
        {
            var kind = PinningOf(server.Id);
            var version = server.Version;
            var floating = version.Length == 0 || version is "latest" || version.IndexOfAny(['^', '~', '*', '>', '<', ' ']) >= 0;
            if (floating && kind != Pinning.None) problems.Add($"{server.Id}: version '{version}' is not exact.");
            var detail = "";
            if (kind == Pinning.Archive)
            {
                var items = Artifacts.Where(item => item.Server == server.Id).ToArray();
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
                var text = Lock(server.Id);
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
                    var packages = NuGetFor(platform);
                    if (packages is null || packages.Length != 2) { problems.Add($"{server.Id} {platform}: no pinned NuGet runtime pack."); continue; }
                    foreach (var item in packages)
                        if (item.Sha512.Length != 128 || !item.Sha512.All(char.IsAsciiHexDigitLower) || item.Version != server.Version) problems.Add($"{server.Id} {platform}: {item.Id} has a missing or malformed SHA-512 or the wrong version.");
                }
                detail = $"{NuGetPackages.Length} package(s), SHA-512 {NuGetPackages.Count(item => item.Sha512.Length == 128)}/{NuGetPackages.Length}";
            }
            else if (kind == Pinning.GoModule)
            {
                var module = Lock(server.Id, "go.mod");
                var sums = Lock(server.Id, "go.sum");
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
    internal static async Task FetchNuGetAsync(HttpClient http, ManagedNuGetPackage package, string feed, CancellationToken token)
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