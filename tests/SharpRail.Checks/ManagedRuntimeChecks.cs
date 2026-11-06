using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

using SharpRail.Host.Core.Language;

namespace SharpRail.Checks;

/// <summary>Managed runtimes: a fake HTTPS source serves a tiny archive; nothing here touches the network or the real pins.</summary>
internal static class ManagedRuntimeChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeSource(byte[] body) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    /// <summary>A tar.gz whose entries are named ./name, like the real SDK archives, with an executable <c>dotnet</c> that prints its version.</summary>
    private static byte[] Archive()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./") { Mode = (UnixFileMode)0x1ed });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./dotnet") { Mode = (UnixFileMode)0x1ed, DataStream = new MemoryStream("#!/bin/sh\necho 10.0.1\n"u8.ToArray()) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./empty.txt") { Mode = (UnixFileMode)0x1a4 });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./sdk/10.0.1/marker.txt") { Mode = (UnixFileMode)0x1a4, DataStream = new MemoryStream("sdk"u8.ToArray()) });
        }
        return output.ToArray();
    }

    private static ManagedRuntimePin Pin(string version, byte[] archive, string? sha256 = null) =>
        new("dotnet-sdk", version, ManagedArtifacts.Platform(), $"https://fake.test/dotnet-sdk-{version}.tar.gz", sha256 ?? Convert.ToHexStringLower(SHA256.HashData(archive)),
            Convert.ToHexStringLower(SHA512.HashData(archive)), "tar.gz", "check fixture", ["fake.test"]);

    private static void Env(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    internal static void Run(string fixture)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP managed runtime checks use shell stubs"); return; }
        var root = Path.Combine(fixture, "managed-runtimes");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var saved = new[] { Toolchains.SourceVariable, Toolchains.CacheVariable, Toolchains.OfflineVariable, "DOTNET_ROOT", "PATH" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Env(Toolchains.SourceVariable, null); Env(Toolchains.CacheVariable, null); Env(Toolchains.OfflineVariable, null);
        try { Install(root); Resolution(root); Settings(root); }
        finally { foreach (var (name, value) in saved) Env(name, value); ManagedRuntimes.UsePins(null); }
    }

    private static void Install(string root)
    {
        var archive = Archive();
        var platform = ManagedArtifacts.Platform();
        var good = Pin("10.0.1", archive);
        var bad = Pin("10.0.2", archive, new string('0', 64));
        var source = new FakeSource(archive);
        using var http = new HttpClient(source);
        var progress = new List<string>();

        var directory = Path.Combine(root, "install");
        try { ManagedRuntimes.EnsureAsync(directory, good, http, null, offline: true, default).GetAwaiter().GetResult(); Require(false, "An offline install of a missing artifact succeeded."); }
        catch (IOException error) { Require(error.Message.Contains("Offline", StringComparison.Ordinal) && error.Message.Contains("10.0.1", StringComparison.Ordinal) && source.Requests == 0, "Offline failure was not clear or touched the network: " + error.Message); }
        Console.WriteLine("PASS offline with a missing artifact fails at once, naming it, without a request");

        var installed = ManagedRuntimes.EnsureAsync(directory, good, http, progress.Add, offline: false, default).GetAwaiter().GetResult();
        Require(installed == ManagedRuntimes.InstallDirectory(directory, good) && File.Exists(Path.Combine(installed, "dotnet")) && File.Exists(Path.Combine(installed, "sdk", "10.0.1", "marker.txt")) && File.Exists(Path.Combine(installed, "empty.txt")), "A matching archive was not installed.");
        Require(progress.Any(line => line.StartsWith("Downloading", StringComparison.Ordinal)) && progress.Any(line => line.StartsWith("Installed", StringComparison.Ordinal)), "Progress did not report the download and install.");
        Require(Directory.GetFileSystemEntries(Path.GetDirectoryName(installed)!).All(entry => !entry.Contains(".partial-", StringComparison.Ordinal) && !entry.EndsWith(".download", StringComparison.Ordinal)), "A partial directory or download was left.");
        Console.WriteLine("PASS a matching SHA-256 installs atomically and reports progress");

        var before = source.Requests;
        ManagedRuntimes.EnsureAsync(directory, good, http, null, offline: false, default).GetAwaiter().GetResult();
        ManagedRuntimes.EnsureAsync(directory, good, http, null, offline: true, default).GetAwaiter().GetResult();
        Require(source.Requests == before, "A repeated install downloaded again.");
        Console.WriteLine("PASS a repeated install is a no-op, also offline");

        try { ManagedRuntimes.EnsureAsync(directory, bad, http, null, offline: false, default).GetAwaiter().GetResult(); Require(false, "A mismatching SHA-256 installed."); }
        catch (IOException error) { Require(error.Message.Contains("SHA-256", StringComparison.Ordinal), "The mismatch was not named: " + error.Message); }
        var badRoot = ManagedRuntimes.InstallDirectory(directory, bad);
        Require(!Directory.Exists(badRoot) && Directory.GetFileSystemEntries(Path.GetDirectoryName(badRoot)!).All(entry => !entry.Contains(".partial-", StringComparison.Ordinal) && !entry.EndsWith(".download", StringComparison.Ordinal)), "A mismatch left a directory behind.");
        Console.WriteLine("PASS a mismatching SHA-256 is refused and leaves no partial directory");

        ManagedRuntimes.UsePins([good]);
        var warm = Path.Combine(root, "warm");
        ManagedRuntimes.WarmAsync(warm, platform, http, null, offline: false, default).GetAwaiter().GetResult();
        Require(ManagedRuntimes.IsInstalled(warm, good), "Warm did not install the pin.");
        Console.WriteLine("PASS warm installs every pin of the platform");
    }

    private static string Tool(string directory, string name, string output)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, $"#!/bin/sh\necho {output}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0x1ed);
        return path;
    }

    private static void Resolution(string root)
    {
        var archive = Archive();
        var pin = Pin("10.0.3", archive);
        ManagedRuntimes.UsePins([Pin("10.0.1", archive), pin]);
        var directory = Path.Combine(root, "resolve");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        using var http = new HttpClient(new FakeSource(archive));
        var servers = new LanguageServers(directory, null, http);
        var home = Path.Combine(root, "home-sdk");
        var onPath = Path.Combine(root, "path-bin");
        var custom = Tool(Path.Combine(root, "custom"), "dotnet", "override");
        Tool(home, "dotnet", "home");
        Tool(onPath, "dotnet", "path");
        Env("DOTNET_ROOT", home); Env("PATH", onPath);
        string Source() => servers.ProbeTool("dotnet", workspace, "DOTNET_ROOT", fresh: true) is { State: "found" } probe ? probe.Source : "missing";

        Require(Source() == "home" && servers.ManagedEnvironment(workspace) is null, "The default (path) toolchain was not DOTNET_ROOT first, or built a managed environment.");
        ManagedRuntimes.EnsureAsync(directory, pin, http, null, false, default).GetAwaiter().GetResult();
        Require(Source() == "home", "An installed managed runtime changed the desktop default.");
        Console.WriteLine("PASS the desktop default is unchanged: DOTNET_ROOT, then PATH, and no managed environment");

        servers.ToolchainSetting = () => "managed-then-path";
        Require(Source() == "managed", "managed-then-path did not prefer the managed runtime.");
        var env = servers.ManagedEnvironment(workspace) ?? throw new InvalidOperationException("No managed environment.");
        var installed = ManagedRuntimes.InstallDirectory(directory, pin);
        Require(env["DOTNET_ROOT"] == installed && env["PATH"].StartsWith(installed, StringComparison.Ordinal) && env["PATH"].EndsWith(onPath, StringComparison.Ordinal), "managed-then-path did not put the managed runtime first on the inherited PATH: " + env["PATH"]);
        servers.ToolchainSetting = () => "managed";
        env = servers.ManagedEnvironment(workspace)!;
        Require(env["PATH"] == installed + Path.PathSeparator + "/usr/bin:/bin" && !env["PATH"].Contains(onPath, StringComparison.Ordinal), "managed kept the host PATH: " + env["PATH"]);
        var cache = ManagedRuntimes.CacheRoot(directory);
        Require(env["DOTNET_CLI_HOME"] == Path.Combine(cache, "dotnet-home") && env["NUGET_PACKAGES"] == Path.Combine(cache, "nuget") && env["DOTNET_NOLOGO"] == "1" && env["DOTNET_CLI_TELEMETRY_OPTOUT"] == "1", "The managed environment lacks the cache homes or the opt-outs.");
        Console.WriteLine("PASS managed puts the runtime first; managed-only uses PATH = bin + /usr/bin:/bin and cache homes");

        servers.ToolchainSetting = () => "managed-then-path";
        servers.ToolPaths = () => $"{{\"dotnet\":\"{custom}\"}}";
        var probe = servers.ProbeTool("dotnet", workspace, "DOTNET_ROOT", fresh: true);
        Require(probe is { State: "found", Source: "override" } && probe.Path == custom && servers.ManagedEnvironment(workspace) is null, "An explicit override did not win over the managed runtime.");
        servers.ToolPaths = () => "";
        Console.WriteLine("PASS resolution order: override, then managed, then DOTNET_ROOT, then PATH");

        // Nothing installed yet, managed only: missing and named; managed-then-path falls through to DOTNET_ROOT.
        var empty = new LanguageServers(Path.Combine(root, "empty"), null, http) { ToolchainSetting = () => "managed" };
        var missing = empty.ProbeTool("dotnet", workspace, "DOTNET_ROOT", fresh: true);
        Require(missing is { State: "missing", Source: "managed" } && missing.Problem.Contains("10.0.3", StringComparison.Ordinal), "An empty managed cache was not reported missing with the pinned version: " + missing);
        empty.ToolchainSetting = () => "managed-then-path";
        Require(empty.ProbeTool("dotnet", workspace, "DOTNET_ROOT", fresh: true).Source == "home", "managed-then-path did not fall back to DOTNET_ROOT.");
        Env(Toolchains.SourceVariable, "path");
        Require(empty.ProbeTool("dotnet", workspace, "DOTNET_ROOT", fresh: true).Source == "home" && empty.Toolchain == ToolchainSource.Path, "SHARPRAIL_TOOLCHAINS did not override the setting.");
        Env(Toolchains.SourceVariable, "managed");
        Require(empty.Toolchain == ToolchainSource.Managed, "SHARPRAIL_TOOLCHAINS=managed was not honoured.");
        Env(Toolchains.SourceVariable, null);
        Console.WriteLine("PASS an empty managed cache is reported with the pinned version and the variable overrides the setting");

        // A project that asks for an unpinned SDK: the error names what is pinned. Exact, roll-forward and disabled policies.
        File.WriteAllText(Path.Combine(workspace, "global.json"), """{"sdk":{"version":"10.0.2","rollForward":"latestPatch"}}""");
        Require(ManagedRuntimes.Choose("dotnet-sdk", pin.Platform, workspace)?.Version == "10.0.3", "latestPatch did not roll 10.0.2 forward to the pinned 10.0.3.");
        File.WriteAllText(Path.Combine(workspace, "global.json"), """{"sdk":{"version":"10.0.2","rollForward":"disable"}}""");
        Require(ManagedRuntimes.Choose("dotnet-sdk", pin.Platform, workspace) is null, "rollForward disable accepted a different version.");
        File.WriteAllText(Path.Combine(workspace, "global.json"), """{"sdk":{"version":"10.0.1","rollForward":"disable"}}""");
        Require(ManagedRuntimes.Choose("dotnet-sdk", pin.Platform, workspace)?.Version == "10.0.1", "An exact pinned version was not chosen.");
        File.WriteAllText(Path.Combine(workspace, "global.json"), """{"sdk":{"version":"9.0.100"}}""");
        empty.ToolchainSetting = () => "managed";
        var unpinned = empty.ProbeTool("dotnet", workspace, "DOTNET_ROOT", fresh: true);
        Require(unpinned.Problem.Contains("9.0.100", StringComparison.Ordinal) && unpinned.Problem.Contains("pinned: 10.0.3, 10.0.1", StringComparison.Ordinal), "The unpinned request was not named with the pinned versions: " + unpinned.Problem);
        File.Delete(Path.Combine(workspace, "global.json"));
        Console.WriteLine("PASS global.json picks the pin by its roll-forward policy and an unpinned request names the pinned versions");
    }

    private static void Settings(string root)
    {
        Require(Toolchains.ReadSetting("version = 1\n[toolchains]\nsource = \"managed\"\n", out var problem) == "managed" && problem is null, "[toolchains] source was not read.");
        Require(Toolchains.ReadSetting("[toolchains]\nsource = \"docker\"\n", out problem) == "" && problem is not null, "A bad source was accepted.");
        Require(Toolchains.ReadSetting("", out problem) == "" && problem is null && Toolchains.Effective("") == ToolchainSource.Path, "The default is not path.");
        Env(Toolchains.CacheVariable, Path.Combine(root, "cache"));
        Require(ManagedRuntimes.RuntimesRoot(Path.Combine(root, "other")) == Path.Combine(root, "cache", "runtimes"), "SHARPRAIL_MANAGED_CACHE did not move the runtimes folder.");
        Env(Toolchains.CacheVariable, null);
        Console.WriteLine("PASS [toolchains] source parses, defaults to path, and the cache directory can be moved by SHARPRAIL_MANAGED_CACHE");

        // The shipped pins: every platform entry is complete and shaped like the others.
        ManagedRuntimes.UsePins(null);
        foreach (var pin in ManagedRuntimes.Pins)
            Require(pin.Sha256.Length == 64 && pin.Sha512.Length == 128 && pin.Url.StartsWith("https://builds.dotnet.microsoft.com/dotnet/Sdk/" + pin.Version + "/", StringComparison.Ordinal) && pin.Source.Length > 0, $"Pin {pin.Version} {pin.Platform} is incomplete.");
        Require(ManagedRuntimes.Pins.Select(pin => pin.Version).Distinct().Count() == 2, "Expected two pinned SDK versions.");
        Console.WriteLine("PASS the shipped pins are complete");
    }
}