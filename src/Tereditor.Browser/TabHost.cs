using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Pi.Host;
using SharpRail.UI.State;

namespace SharpRail.UI.Browser;

/// <summary>
/// The host that runs inside this tab: the same in-process composition as the desktop app's own host, on the runtime's
/// virtual disk, with no processes. Plugins that need the operating system are refused as unavailable on a pure web host.
/// </summary>
internal sealed class TabHost : IDisposable
{
    /// <summary>The folder the tab's first project lives in.</summary>
    public const string ProjectRoot = "/workspace/project";

    private readonly PluginRuntime runtime;
    private readonly HostStateStore local;

    public HostConnection Connection { get; } = new();
    public IHostStateService States { get; }
    public IPluginService Plugins { get; }
    public HostState Current => local.Current;

    public TabHost(ProfileStore profile)
    {
        // Pi's credentials live on the tab's disk; Settings › Model providers and the in-tab agent read the same file.
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", TabPiLink.AgentDirectory);
        Directory.CreateDirectory(TabPiLink.AgentDirectory);
        PiLinks.InTabVersion = TabPiLink.VersionAsync;
        LanguageLinks.InTab = (server, _, cancellationToken) => TabLanguageServer.StartAsync(server, cancellationToken);
        ProcessLinks.TabUsage = TabLanguageServer.UsageOf;
        ProcessLinks.TabRefresh = TabLanguageServer.RefreshUsageAsync;
        PiLinks.InTab = (cwd, _, model, thinking, cancellationToken) => TabPiLink.StartAsync(cwd, model, thinking, cancellationToken);
        local = profile.OpenState();
        // Writes reach the disk through the page's thread, which also reads the state: never while its lock is held.
        local.WriteInBackground = true;
        runtime = new PluginRuntime(new()
        {
            StateDirectory = profile.DirectoryPath,
            State = local,
            Kind = HostKind.PureWeb,
            Builtins = BuiltinPlugins.Create(null)
        });
        runtime.Start();
        States = new LocalStateAdapter(local);
        Plugins = new LocalPluginAdapter(runtime);
    }

    /// <summary>Creates the tab's first project as a repository, on a first visit.</summary>
    public static async Task<bool> EnsureProjectAsync()
    {
        if (Directory.Exists(ProjectRoot)) return false;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(ProjectRoot);
            File.WriteAllText(Path.Combine(ProjectRoot, "README.md"), "# Project\n\nThis project lives in your browser tab.\n");
        });
        await TabGit.InitializeAsync(ProjectRoot);
        return true;
    }

    /// <summary>Turns Pi on unless it was ever configured: it is off by default elsewhere, but is what this host is for.</summary>
    public Task EnableDefaultsAsync() => Task.Run(async () =>
    {
        if (!Current.PluginSettings.ContainsKey("pi")) await States.ChangeAsync([HostStateChange.PluginEnabled("pi", true)]);
    });

    public IProjectServices Session() => new LocalProjectAdapter(new ProjectServices(ProjectRoot, local, runtime.AllowsExternalFile));

    public void Dispose() => Task.Run(async () => await runtime.DisposeAsync()).GetAwaiter().GetResult();
}
