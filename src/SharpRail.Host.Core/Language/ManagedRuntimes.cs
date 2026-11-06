using System.Collections.Concurrent;
using System.Text.Json;

using Tomlyn;

namespace SharpRail.Host.Core.Language;

/// <summary>Where the tools a language server needs (the .NET SDK today) come from: the host, the pinned runtimes in the cache, or both.</summary>
internal enum ToolchainSource
{
    /// <summary>Override, then <c>DOTNET_ROOT</c>-style variables, then PATH. The desktop default.</summary>
    Path,
    /// <summary>Only the pinned runtimes in the cache (a tool without a pinned runtime still follows <see cref="Path"/>). The headless default.</summary>
    Managed,
    /// <summary>The pinned runtimes first, then the <see cref="Path"/> order.</summary>
    ManagedThenPath
}

/// <summary>The <c>[toolchains] source</c> setting and the cache locations the managed runtimes use.</summary>
internal static class Toolchains
{
    internal const string SourceVariable = "SHARPRAIL_TOOLCHAINS";
    internal const string CacheVariable = "SHARPRAIL_MANAGED_CACHE";
    internal const string OfflineVariable = "SHARPRAIL_OFFLINE";

    internal static ToolchainSource? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "path" => ToolchainSource.Path,
        "managed" => ToolchainSource.Managed,
        "managed-then-path" => ToolchainSource.ManagedThenPath,
        _ => null
    };

    /// <summary>The source in force: <c>SHARPRAIL_TOOLCHAINS</c> first, then the host setting, then <see cref="ToolchainSource.Path"/> (desktop behaviour).</summary>
    internal static ToolchainSource Effective(string? setting) =>
        Parse(Environment.GetEnvironmentVariable(SourceVariable)) ?? Parse(setting) ?? ToolchainSource.Path;

    /// <summary>Reads <c>[toolchains] source = "..."</c> from a host TOML text; a problem is returned instead of thrown and leaves the setting unset.</summary>
    internal static string ReadSetting(string text, out string? problem)
    {
        problem = null;
        try
        {
            var value = (ProjectToml.Parse(text)["toolchains"] as System.Text.Json.Nodes.JsonObject)?["source"]?.GetValue<string>() ?? "";
            if (value.Length > 0 && Parse(value) is null) problem = $"toolchains.source '{value}' is not path, managed or managed-then-path; it is ignored.";
            return problem is null ? value : "";
        }
        catch (Exception error) when (error is TomlException or InvalidOperationException or FormatException)
        {
            problem = $"toolchains could not be read: {error.Message}";
            return "";
        }
    }

    internal static bool Offline => Environment.GetEnvironmentVariable(OfflineVariable) is "1" or "true" or "TRUE" or "True";
}

/// <summary>One pinned runtime archive. <see cref="Sha256"/> is what the host verifies; <see cref="Sha512"/> is the publisher's value, checked once when the pin was made (scripts/pin-dotnet-sdk.py).</summary>
internal sealed record ManagedRuntimePin(string Id, string Version, string Platform, string Url, string Sha256, string Sha512, string Format, string Source, string[]? Hosts = null);

/// <summary>A runtime kind: what it is called, which tools it provides and where they live inside the extracted archive.</summary>
internal sealed record ManagedRuntimeKind(string Id, string Name, string[] Tools, string BinDirectory, string RootVariable);

/// <summary>The runtimes language servers need, pinned by exact version and SHA-256 per platform and installed on demand into the cache, as <see cref="ManagedArtifacts"/> does for servers.</summary>
internal static class ManagedRuntimes
{
    internal static readonly string[] OfficialHosts = ["builds.dotnet.microsoft.com"];

    /// <summary>Adding Node, a JDK or Python is one entry here and its pins below.</summary>
    internal static readonly ManagedRuntimeKind[] Kinds =
    [
        new("dotnet-sdk", ".NET SDK", ["dotnet"], "", "DOTNET_ROOT")
    ];

    /// <summary>The pins in force; checks replace them with a fake archive.</summary>
    internal static IReadOnlyList<ManagedRuntimePin> Pins => replacement ?? Builtin;

    private static IReadOnlyList<ManagedRuntimePin>? replacement;

    /// <summary>Replaces the pins for a check; null restores the shipped ones.</summary>
    internal static void UsePins(IReadOnlyList<ManagedRuntimePin>? pins) => replacement = pins;

    internal static ManagedRuntimeKind? KindOf(string tool) => Kinds.FirstOrDefault(kind => kind.Tools.Contains(tool));

    /// <summary>The cache root: <c>SHARPRAIL_MANAGED_CACHE</c> when set to an absolute path (the container points it at /cache), else the host's own directory.</summary>
    internal static string CacheRoot(string directory) =>
        Environment.GetEnvironmentVariable(Toolchains.CacheVariable) is { Length: > 0 } cache && Path.IsPathFullyQualified(cache) ? cache : directory;

    internal static string RuntimesRoot(string directory) => Path.Combine(CacheRoot(directory), "runtimes");

    internal static string InstallDirectory(string directory, ManagedRuntimePin pin) => Path.Combine(RuntimesRoot(directory), pin.Id, pin.Version + "-" + pin.Sha256[..8]);

    internal static bool IsInstalled(string directory, ManagedRuntimePin pin) => File.Exists(Path.Combine(InstallDirectory(directory, pin), ".sharprail-artifact.json"));

    internal static IReadOnlyList<ManagedRuntimePin> For(string id, string platform) =>
        [.. Pins.Where(pin => pin.Id == id && pin.Platform == platform).OrderByDescending(pin => Numbers(pin.Version), VersionOrder.Instance)];

    private sealed class VersionOrder : IComparer<int[]>
    {
        internal static readonly VersionOrder Instance = new();
        public int Compare(int[]? left, int[]? right) => left!.Zip(right!, (a, b) => a.CompareTo(b)).FirstOrDefault(order => order != 0);
    }

    private static int[] Numbers(string version) => [.. version.Split('-')[0].Split('.').Select(part => int.TryParse(part, out var number) ? number : 0).Concat(Enumerable.Repeat(0, 3)).Take(3)];

    /// <summary>The SDK version a workspace asks for in its <c>global.json</c> and how far it may roll forward; null when it asks for none.</summary>
    internal static (string Version, string RollForward)? Requested(string workspace)
    {
        try
        {
            var file = Path.Combine(workspace, "global.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 64 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!document.RootElement.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object || !sdk.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String) return null;
            var roll = sdk.TryGetProperty("rollForward", out var policy) && policy.ValueKind == JsonValueKind.String ? policy.GetString()! : "latestPatch";
            return (version.GetString()!, roll);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The pin a workspace should use: the newest when it asks for no version, else the newest the roll-forward policy allows. Null when none fits.</summary>
    internal static ManagedRuntimePin? Choose(string id, string platform, string workspace)
    {
        var pins = For(id, platform);
        if (id != "dotnet-sdk" || Requested(workspace) is not { } request) return pins.FirstOrDefault();
        var wanted = Numbers(request.Version);
        var policy = request.RollForward.ToLowerInvariant();
        bool Fits(int[] have) => policy switch
        {
            "disable" => have.SequenceEqual(wanted),
            "major" or "latestmajor" => VersionOrder.Instance.Compare(have, wanted) >= 0,
            "minor" or "latestminor" => have[0] == wanted[0] && VersionOrder.Instance.Compare(have, wanted) >= 0,
            "feature" or "latestfeature" => have[0] == wanted[0] && have[1] == wanted[1] && VersionOrder.Instance.Compare(have, wanted) >= 0,
            _ => have[0] == wanted[0] && have[1] == wanted[1] && have[2] / 100 == wanted[2] / 100 && have[2] >= wanted[2]
        };
        return pins.FirstOrDefault(pin => Numbers(pin.Version).SequenceEqual(wanted)) ?? pins.FirstOrDefault(pin => Fits(Numbers(pin.Version)));
    }

    /// <summary>Why no pin was chosen, naming what is pinned, for the <c>Needs ...</c> text.</summary>
    internal static string NotPinned(ManagedRuntimeKind kind, string platform, string workspace)
    {
        var pinned = For(kind.Id, platform);
        var list = pinned.Count == 0 ? $"no {kind.Name} is pinned for {platform}" : "pinned: " + string.Join(", ", pinned.Select(pin => pin.Version));
        return Requested(workspace) is { } request
            ? $"The project asks for {kind.Name} {request.Version} (rollForward {request.RollForward}), which is not pinned ({list}). Change global.json, or use toolchains.source = managed-then-path or path."
            : $"No {kind.Name} is pinned for {platform} ({list}).";
    }

    /// <summary>The installed tool for a workspace, or null.</summary>
    internal static string? FindInstalled(string directory, string tool, string platform, string workspace)
    {
        if (KindOf(tool) is not { } kind || Choose(kind.Id, platform, workspace) is not { } pin || !IsInstalled(directory, pin)) return null;
        var file = Path.Combine(InstallDirectory(directory, pin), kind.BinDirectory, tool + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return File.Exists(file) ? file : null;
    }

    /// <summary>
    /// The environment of one server run when the toolchain is managed: PATH of the managed bin directories plus a fixed system part
    /// (not the host PATH unless <paramref name="inheritPath"/>), the runtime roots and per-cache homes so nothing is written outside the cache.
    /// Null when no managed runtime is installed for the workspace.
    /// </summary>
    internal static Dictionary<string, string>? RunEnvironment(string directory, string platform, string workspace, bool inheritPath = false)
    {
        var bins = new List<string>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in Kinds)
            if (Choose(kind.Id, platform, workspace) is { } pin && IsInstalled(directory, pin))
            {
                var root = InstallDirectory(directory, pin);
                bins.Add(Path.GetFullPath(Path.Combine(root, kind.BinDirectory)));
                variables[kind.RootVariable] = root;
            }
        if (bins.Count == 0) return null;
        var cache = CacheRoot(directory);
        var system = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.System) : "/usr/bin:/bin";
        variables["PATH"] = string.Join(Path.PathSeparator, bins) + Path.PathSeparator + (inheritPath ? Environment.GetEnvironmentVariable("PATH") ?? system : system);
        variables["DOTNET_CLI_HOME"] = Path.Combine(cache, "dotnet-home");
        variables["NUGET_PACKAGES"] = Path.Combine(cache, "nuget");
        variables["DOTNET_NOLOGO"] = "1";
        variables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return variables;
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>Makes sure a pin is installed. Offline, a missing artifact fails at once and clearly; online it is downloaded, verified against the pinned SHA-256 and installed atomically.</summary>
    internal static async Task<string> EnsureAsync(string directory, ManagedRuntimePin pin, HttpClient http, Action<string>? progress, bool offline, CancellationToken token)
    {
        var kind = Kinds.First(item => item.Id == pin.Id);
        var root = InstallDirectory(directory, pin);
        if (IsInstalled(directory, pin)) { progress?.Invoke($"{kind.Name} {pin.Version} is already installed."); return root; }
        if (offline)
            throw new IOException($"Offline: the pinned {kind.Name} {pin.Version} for {pin.Platform} is not in the cache at {root}. Run warm with network access first, or unset {Toolchains.OfflineVariable}.");
        var gate = Gates.GetOrAdd(root, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            await using var lease = await LanguageServers.InstallationLock(root + ".lock", token);
            progress?.Invoke($"Downloading {kind.Name} {pin.Version} ({pin.Platform}) from {new Uri(pin.Url).Host}.");
            var artifact = new ManagedArtifact(pin.Id, pin.Version, pin.Platform, pin.Url, pin.Sha256, pin.Format, pin.Source, pin.Hosts ?? OfficialHosts);
            await ArtifactInstaller.InstallAsync(artifact, root, http, token);
            progress?.Invoke($"Installed {kind.Name} {pin.Version} at {root}, SHA-256 verified.");
        }
        finally { gate.Release(); }
        return root;
    }

    /// <summary>Pre-warms the cache: installs every pinned runtime of the platform (or only <paramref name="version"/>). Used by the container build and the headless warm command.</summary>
    internal static async Task WarmAsync(string directory, string platform, HttpClient http, Action<string>? progress, bool offline, CancellationToken token, string? version = null)
    {
        var pins = Pins.Where(pin => pin.Platform == platform && (version is null || pin.Version == version)).ToArray();
        if (pins.Length == 0) throw new IOException($"No runtime is pinned for {platform}" + (version is null ? "." : $" at version {version}."));
        foreach (var pin in pins) await EnsureAsync(directory, pin, http, progress, offline, token);
    }

    private static readonly ManagedRuntimePin[] Builtin =
    [
    new("dotnet-sdk", "10.0.401", "linux-x86_64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz",
        "137268c8ad939c064ff1ee2a6fdf0899d8725377114ea012fbd1ad5fa2550418", "51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-09-08), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.401", "linux-aarch64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-arm64.tar.gz",
        "91d3d67f2ed065909bd2e1ba50a37192aff6df15178304939d5b55634d72da6a", "58ace73ced6b4360754689a686bdfb8a317f4da6cb8bb416dbc7d0ba9f47e43e3c09f5eb1f1a1cfaacbd10df9558da4882bf2a5e195d6ab56a02c1f9f76102ed", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-09-08), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.401", "macos-aarch64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-osx-arm64.tar.gz",
        "d143ccec4474dce17d7b778854598137a2599b81828bce51e809897fe8bdf035", "69f64eb00dc045398755c440b152225d544301a345a146a16e86a56a0c52b7c94b2c331520e976dbb821f18d31930aafbd25bb85961e3517e0665414ce0cbcff", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-09-08), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.403", "linux-x86_64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-linux-x64.tar.gz",
        "cd7224fe8e8ba57c15f1601439ff8785a1a72b54ae5c89dd5302a32dcfbddf18", "b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-10-25), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.403", "linux-aarch64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-linux-arm64.tar.gz",
        "21fe1a0963984206bfaeb19968da07e7a61d30c9bc4621b7372a762a30c88679", "0966aa2b70f4677b8319be9df447a78a0dcd6164836425b55a68fd714f4023feb87b4cac21b9a996b79472ad54487cd0adcd2a9dfd570e2b2a1d26423a2baa9b", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-10-25), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.403", "macos-aarch64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-osx-arm64.tar.gz",
        "826d7ff91fa83b16bccd7edd7822581fe32b5228a4501aabe61d864168129d73", "5893c66b48ea3674c7da18501059d606e96f3672fabe4d5e41762e692c7c83df3ee0b2b9dc5a0786191da2dc413d7335158101c14c935ab217f31ac2529770f3", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-10-25), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.401", "macos-x86_64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-osx-x64.tar.gz",
        "0616332aa0b5edfa19443a9d217d14d086d2e9605d7c72979cf8ebbb494ac792", "33401b4a2da8554e3306db6072ea8569d9fcc608509c271e0aa4b39e7cc432da3631f14e7e1e2445d67d72550d18ce44a8bbd2382a756867ad2edab6b1c963c0", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-09-08), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.401", "windows-x86_64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip",
        "c1b96dea223e1ab2af8a51187e0cc96338d14c68834be1656136be18953cea09", "24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430", "zip",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-09-08), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.403", "macos-x86_64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-osx-x64.tar.gz",
        "cccca37f1ce3a69f99982b80c9546019d149dd2ad26c4ecc01b3b3ab87abeb0e", "ce95d225b86d2401f111c2aa6df0f5e6855c375b7b34ab4a73df90c56f2b0fc2a44398cfe455cb689670bc8590012e943b64c7a590122af98cdc067721b12223", "tar.gz",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-10-25), verified against the download; SHA-256 computed from that download"),
    new("dotnet-sdk", "10.0.403", "windows-x86_64", "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip",
        "f398979a9cac2dba760771c4e01f16e431dcd6bcfb2213e3e8f5b3d354095cc0", "7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48", "zip",
        "SHA-512 from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json (release 2026-10-25), verified against the download; SHA-256 computed from that download"),
    ];
}