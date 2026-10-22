using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

/// <summary>The host's view of one provider's server (H21): the spec a plugin registered, with the shapes the host code reads.</summary>
internal sealed record LanguageServer(LanguageServerSpec Spec)
{
    internal string Id => Spec.Id;
    internal string Version => Spec.Version;
    internal string Executable => Spec.Executable;
    internal string[] Arguments { get; } = [.. Spec.Arguments];
    internal IReadOnlyList<string> Extensions => Spec.Extensions;
    internal string Package => Spec.NpmPackages;
    internal string Implementation => Spec.Implementation;
}

internal sealed partial class LanguageServers(string directory, Func<string>? configuration = null, HttpClient? managedDownloads = null,
    Func<IReadOnlyList<LanguageServerSpec>>? catalog = null)
{
    internal string DirectoryPath => directory;

    internal string Storage => directory;
    internal LanguageServerOptions? Options(string id)
    {
        var settings = configuration?.Invoke();
        return string.IsNullOrWhiteSpace(settings) ? null :
            JsonSerializer.Deserialize<Dictionary<string, LanguageServerOptions>>(settings, JsonSerializerOptions.Web)?.GetValueOrDefault(id);
    }

    private static readonly ConditionalWeakTable<LanguageServerSpec, LanguageServer> Views = new();
    private static readonly Lazy<IReadOnlyList<LanguageServer>> Builtin = new(() => Wrap(Plugins.BuiltinPlugins.LanguageServers.Select(plugin => plugin.Spec).ToArray()));

    /// <summary>The servers the builtin plugins provide, whether or not they are enabled; a host without a plugin runtime serves these.</summary>
    internal static IReadOnlyList<LanguageServer> Catalog => Builtin.Value;

    private static readonly ConcurrentDictionary<string, LanguageServer> Seen = new(StringComparer.Ordinal);

    internal static IReadOnlyList<LanguageServer> Wrap(IReadOnlyList<LanguageServerSpec> specs) => [.. specs.Select(spec => Seen[spec.Id] = Views.GetValue(spec, item => new LanguageServer(item)))];

    /// <summary>
    /// A server this process knows: a builtin one, or one any plugin has provided. Saved settings and project files name
    /// servers this way, so they stay valid while the providing plugin is disabled.
    /// </summary>
    internal static LanguageServer? Known(string id) => Catalog.FirstOrDefault(server => server.Id == id) ?? Seen.GetValueOrDefault(id);

    /// <summary>The servers enabled plugins provide now, in roster order.</summary>
    internal IReadOnlyList<LanguageServer> Servers => catalog is null ? Catalog : Wrap(catalog());

    internal LanguageServer? Find(string id) => Servers.FirstOrDefault(server => server.Id == id);

    internal LanguageServer? Detect(string path) => Detect(Servers, path);

    internal static LanguageServer? Detect(IReadOnlyList<LanguageServer> servers, string path) =>
        servers.FirstOrDefault(server => server.Extensions.Contains(Path.GetExtension(path)))
        ?? servers.FirstOrDefault(server => server.Spec.FileNames.Contains(Path.GetFileName(path)));

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> installations = new();
    private readonly HttpClient managedHttp = managedDownloads ?? ArtifactInstaller.CreateClient();

    internal static string LanguageId(string path, LanguageServer server) => server.Spec.LanguageId(path) ?? server.Id;

    /// <param name="sessionDirectory">The launch's own directory, or <see langword="null"/> for a fresh one.</param>
    internal async Task<ProcessStartInfo> ProvisionAsync(LanguageServer server, string workspace, CancellationToken token, WorkspaceLanguageOptions? options = null, string? sessionDirectory = null)
    {
        var installation = new Installation(this, server, workspace, options?.InstallationPath ?? "", sessionDirectory);
        if (server.Spec.TermsAsync is { } terms)
        {
            // A server with terms is the provider's to install and launch, and starts only once these exact terms were accepted.
            LanguageServerTerms reviewed;
            try { reviewed = await terms(installation, false, token); }
            catch (IOException error) when (error is not LanguageServerExpiredException) { throw new LanguageTermsRequiredException(server); }
            if (!LanguageTerms.Accepted(directory, server.Id, reviewed)) throw new LanguageTermsRequiredException(server);
            installation.AcceptedTerms = reviewed;
            // Overrides cannot replace the acknowledged launcher or its mandatory protocol arguments.
            if (!server.Spec.AllowsOverride && Options(server.Id) is { Command.Length: > 0 })
                throw new IOException($"Use the {server.Id} workspace installation directory instead of an executable override.");
        }
        var start = await ProvisionCoreAsync(server, installation, token);
        ConfigureCaches(start, server, workspace);
        return start;
    }

    /// <summary>Lets the provider adjust any launch, managed, PATH or override: cache variables, a state directory argument.</summary>
    internal void ConfigureCaches(ProcessStartInfo start, LanguageServer server, string workspace)
    {
        var installation = new Installation(this, server, workspace);
        Directory.CreateDirectory(installation.ServerCache);
        server.Spec.ConfigureLaunch(installation, new() { Arguments = start.ArgumentList, Environment = start.Environment });
    }

    private async Task<ProcessStartInfo> ProvisionCoreAsync(LanguageServer server, Installation installation, CancellationToken token)
    {
        var spec = server.Spec;
        var workspace = installation.Workspace;
        var root = installation.Root;
        Directory.CreateDirectory(installation.WorkspaceState);
        Directory.CreateDirectory(installation.SharedCache);
        var custom = CustomOptions(server.Id);
        if (custom?.Disabled == true) throw new IOException($"{server.Id} is disabled. Enable it in Settings > Code Intelligence, then Retry.");
        if (spec.TermsAsync is not null && spec.LaunchAsync is { } own) return Start(await own(installation, token));
        if (!string.IsNullOrWhiteSpace(custom?.Command) && spec.AllowsOverride)
        {
            var explicitPath = ExplicitExecutable(custom.Command, workspace);
            return Start(explicitPath, custom.Arguments ?? server.Arguments);
        }
        if (spec.Pinning == LanguageServerPinning.None || custom?.AllowPath == true && spec.PathCapable)
        {
            if (custom?.AllowPath != true)
                throw new IOException($"{server.Id} needs an installation: SharpRail has no managed installation for it. Install the {server.Id} SDK and allow PATH for this server in Settings > Code Intelligence, then Retry.");
            var rejected = new List<string>();
            var installed = LanguageExecutables.FindOnPath(server.Executable, workspace, rejected);
            if (installed is not null && !spec.AcceptsPathExecutable(installed, workspace)) installed = null;
            if (installed is null && spec.FindFallbackAsync is { } fallback && await fallback(installation, token) is { Length: > 0 } found &&
                LanguageExecutables.Reject(found, workspace) is null) installed = found;
            return Start(installed ?? throw new IOException($"PATH is allowed for {server.Id} but no usable {server.Executable} was found on it." + (rejected.Count > 0 ? " " + rejected[0] : "")), server.Arguments);
        }
        var gate = installations.GetOrAdd(root, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            // File sharing also serializes provisioning by separate host processes.
            await using var lease = await InstallationLock(root + ".lock", token);
            if (spec.Pinning == LanguageServerPinning.Archive)
            {
                var artifact = ManagedArtifacts.Find(server, installation.Platform)
                    ?? throw new IOException($"{server.Id} needs an installation: there is no verified managed artifact for {installation.Platform}. Allow PATH for this server in Settings > Code Intelligence, or configure an executable.");
                await ArtifactInstaller.InstallAsync(artifact, root, managedHttp, token);
            }
            else
            {
                Directory.CreateDirectory(root);
                if (!File.Exists(Path.Combine(root, ".ready")) || !spec.Installed(root))
                {
                    if (spec.Pinning == LanguageServerPinning.Lockfile)
                    {
                        // The embedded lockfile pins every package by version and integrity hash; npm ci refuses anything else.
                        var lockfile = ManagedArtifacts.Lock(server) ?? throw new IOException($"{server.Id} has no embedded package-lock.json; it will not be installed.");
                        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), ManagedArtifacts.Lock(server, "package.json") ?? throw new IOException($"{server.Id} has no embedded package.json; it will not be installed."), token);
                        await File.WriteAllTextAsync(Path.Combine(root, "package-lock.json"), lockfile, token);
                        await RunAsync(Start(Require("npm", workspace), ["ci", "--prefix", root, "--no-audit", "--no-fund", "--ignore-scripts", "--registry", "https://registry.npmjs.org/"]), root, token);
                    }
                    else if (spec.InstallAsync is { } install) await install(installation, token);
                    await File.WriteAllTextAsync(Path.Combine(root, ".ready"), server.Version, token);
                }
            }
        }
        finally { gate.Release(); }

        if (spec.LaunchAsync is { } launch) return Start(await launch(installation, token));
        if (server.Package.Length > 0)
        {
            var packageRoot = Path.Combine(root, "node_modules");
            var package = server.Package.Split(' ')[0].Split('@')[0];
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packageRoot, package, "package.json"), token));
            var bin = manifest.RootElement.GetProperty("bin");
            var file = bin.ValueKind == JsonValueKind.String ? bin.GetString()! : bin.GetProperty(server.Executable).GetString()!;
            return Start(Require("node", workspace), [Path.Combine(packageRoot, package, file), .. server.Arguments]);
        }
        return Start(installation.FindInstalled(InstalledName(server)), server.Arguments);
    }

    /// <summary>A fresh per-launch directory inside the server's workspace state.</summary>
    internal string SessionDirectory(LanguageServer server, string workspace) =>
        Path.Combine(directory, "workspaces", Key(workspace), server.Id, Key(Guid.NewGuid().ToString("N")));

    /// <summary>The installation a provider's terms review reads for a workspace.</summary>
    internal ILanguageServerInstallation InstallationFor(LanguageServer server, string workspace, string installationPath) => new Installation(this, server, workspace, installationPath);

    /// <summary>The executable's file name inside a managed installation.</summary>
    internal static string InstalledName(LanguageServer server) => server.Spec.InstalledExecutable.Length > 0 ? server.Spec.InstalledExecutable : server.Executable;

    /// <summary>What the host lends a provider for one server and workspace (H21).</summary>
    private sealed class Installation(LanguageServers owner, LanguageServer server, string workspace, string installationPath = "", string? sessionDirectory = null) : ILanguageServerInstallation
    {
        public string InstallationPath => installationPath;
        public string SessionDirectory => sessionDirectory ??= owner.SessionDirectory(server, workspace);
        public LanguageServerTerms? AcceptedTerms { get; set; }
        public HttpClient Http => LanguageServers.Http;
        public string Root { get; } = Path.Combine(owner.DirectoryPath, "servers", server.Id, ManagedArtifacts.Directory(server, ManagedArtifacts.Platform()));
        public string Workspace => workspace;
        public string WorkspaceState { get; } = Path.Combine(owner.DirectoryPath, "workspaces", Key(workspace), server.Id);
        public string SharedCache { get; } = Path.Combine(owner.DirectoryPath, "cache");
        public string ServerCache { get; } = Path.Combine(owner.DirectoryPath, "cache", server.Id, server.Version);
        public string Platform { get; } = ManagedArtifacts.Platform();
        public string? FindTool(string executable) => LanguageExecutables.FindOnPath(executable, workspace);
        public string RequireTool(string executable) => Require(executable, workspace);
        public string? FindDotnet() => LanguageServers.FindDotnet(workspace);

        public string FindInstalled(string name)
        {
            var executable = Directory.GetFiles(Root, name + (OperatingSystem.IsWindows() ? ".exe" : ""), SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new IOException($"{server.Id} installation has no {name}. Remove {Root} and Retry.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            return executable;
        }

        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null,
            CancellationToken cancellationToken = default)
        {
            var start = Start(executable, [.. arguments]);
            Apply(start, environment);
            return LanguageServers.RunAsync(start, workingDirectory, cancellationToken);
        }

        public Task FetchNuGetAsync(LanguageServerNuGetPackage package, string feed, CancellationToken cancellationToken = default) =>
            ArtifactInstaller.FetchNuGetAsync(owner.managedHttp, package, feed, cancellationToken);
    }

    private static ProcessStartInfo Start(LanguageServerLaunch launch)
    {
        var start = Start(launch.Executable, [.. launch.Arguments]);
        Apply(start, launch.Environment);
        return start;
    }

    private static void Apply(ProcessStartInfo start, IReadOnlyDictionary<string, string?>? environment)
    {
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
    }

    /// <summary>Whether the process is a general runtime whose first arguments choose the program, so configured arguments must never replace them.</summary>
    internal static bool HostsRuntime(LanguageServer server) => server.Spec.HostsRuntime;

    internal static string Key(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    internal static ProcessStartInfo Start(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>A tool the installers need (npm, go, gem...). Found with the hardened PATH search, never inside the workspace.</summary>
    private static string Require(string executable, string workspace) => LanguageExecutables.FindOnPath(executable, workspace)
        ?? throw new IOException($"Install {executable} on the host (outside the workspace), then Retry.");
    private static string? FindDotnet(string workspace)
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", executable), LanguageExecutables.FindOnPath("dotnet", workspace)
        }.FirstOrDefault(path => path is not null && Path.IsPathFullyQualified(path) && LanguageExecutables.Reject(path, workspace) is null);
    }

    /// <summary>The user's own executable override from host state: a bare name is searched on the hardened PATH, a path is used as given; both are refused inside the workspace.</summary>
    internal static string ExplicitExecutable(string command, string workspace)
    {
        var resolved = command.IndexOfAny(['/', '\\']) < 0 ? LanguageExecutables.FindOnPath(command, workspace) : command;
        if (resolved is null) throw new IOException($"The executable override '{command}' was not found on PATH (empty, relative and workspace entries are ignored).");
        return LanguageExecutables.Reject(resolved, workspace) is { } problem ? throw new IOException("Executable override refused: " + problem) : resolved;
    }

    /// <summary>The host's choices for one server: host state, or the legacy <c>servers.json</c> file beside the installations.</summary>
    internal LanguageServerOptions? CustomOptions(string id)
    {
        if (Options(id) is { } stored) return stored;
        var config = Path.Combine(directory, "servers.json");
        if (!File.Exists(config)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        return json.RootElement.TryGetProperty(id, out var entry) ? entry.Deserialize<LanguageServerOptions>(JsonSerializerOptions.Web) : null;
    }

    private static async Task<FileStream> InstallationLock(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, token); }
        }
    }

    private static async Task<string> RunAsync(ProcessStartInfo start, string root, CancellationToken token)
    {
        start.WorkingDirectory = root; start.UseShellExecute = false;
        start.RedirectStandardOutput = start.RedirectStandardError = true; start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {start.FileName}.");
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var result = await output; var problem = await error;
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(start.FileName)} failed: {problem[..Math.Min(problem.Length, 3000)]}");
        return result;
    }
}