using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.UI;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.UI;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        StartupPhases.Mark("app-initialize-start");
        AvaloniaXamlLoader.Load(this);
#if !ANDROID && !BROWSER
        if (OperatingSystem.IsMacOS()) ApplicationMenu.SetApplicationMenu(this);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.Args?.Contains("--tailscale", StringComparer.Ordinal) != true)
            PrepareStartup();
#endif
        // The kit has no theme of its own; the catalogue's default applies until a window resolves the host's.
        var theme = Themes.Resolve(Themes.DefaultId);
        if (ReferenceEquals(Ui.Theme, theme)) Ui.ApplyResources(this);
        else Ui.Apply(theme);
        ControlStates.Apply(Resources);
        // One style per typography role, so a control takes its type by class name.
        Styles.Add(Typography.Styles());
        Resources["TreeChevronMask"] = ((Border)Ui.Icon("arrowRight")).OpacityMask;
        foreach (var state in new[] { "Selected", "SelectedPointerOver", "SelectedPressed" })
            Resources["TreeViewItemBackground" + state] = Ui.Selected;
        // The reference's 20px chevron slot. Fluent's template sets its 12px margins directly, which outranks styles.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TreeViewItem>((_, e) =>
        {
            if (e.NameScope.Find<Panel>("PART_ExpandCollapseChevronContainer") is { } chevron) chevron.Margin = new Thickness(2, 0);
        });
        // A window does not inherit its owner's theme variant, and Fluent colours every stock control from it: a dialog,
        // Settings or plugin window would otherwise follow the OS appearance instead of the theme. Workbench windows
        // pass theirs on to owned windows when the theme changes.
        Window.OwnerProperty.Changed.AddClassHandler<Window>((window, _) =>
        {
            if (window.Owner is Window owner) window.RequestedThemeVariant = owner.RequestedThemeVariant;
        });
        StartupPhases.Mark("app-initialize-end");
    }

    // Creates terminal tabs for every window of this app, all attached to the app's one terminal host.
    public TerminalFactory Terminals { get; private set; } = launch => TerminalBackends.Unavailable("The app has not started.");

    public override void OnFrameworkInitializationCompleted()
    {
        StartupPhases.Mark("framework-ready");
#if !ANDROID && !BROWSER
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args?.Contains("--tailscale", StringComparer.Ordinal) == true)
            {
                var directory = TailscaleClientDirectory();
                var connect = new TailscaleDesktopWindow(Path.Combine(directory, "tailscale-client.json"));
                desktop.MainWindow = connect;
                connect.Connected += connection =>
                {
                    desktop.MainWindow = null;
                    StartDesktop(desktop, connection);
                    desktop.MainWindow?.Show();
                    connect.Close();
                };
            }
            else StartDesktop(desktop, null);
            IdleMemoryTrim.Start();
        }
#endif
        base.OnFrameworkInitializationCompleted();
    }

#if !ANDROID && !BROWSER
    private static string TailscaleClientDirectory() => Path.Combine(Environment.GetEnvironmentVariable("SHARPRAIL_PROFILE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail"), "tailscale-client");

    private static Task<DesktopStartup>? startup;

    internal static void PrepareStartup()
    {
        if (startup is not null) return;
        // Legacy profile migration can derive a theme pair. Prime the asset-backed catalogue on the
        // dispatcher before starting that worker; Avalonia's assembly asset cache is not concurrent.
        _ = Themes.All;
        startup = Task.Run(() => PrepareDesktop(null));
    }

    private sealed record DesktopStartup(string? Endpoint, string Token, string InitialRoot, ProfileStore Profile, HostStateStore Local, bool Remote,
        HostConnection Connection, IHostStateService StateService, RemoteTerminalAdapter? RemoteTerminals, PtyTerminalService? LocalTerminals,
        SharpRail.Host.Remote.LoopbackServer? Loopback, SharpRail.Host.Core.Plugins.PluginRuntime? Runtime, SharpRail.Host.Remote.HostListener? Listener,
        RemotePluginAdapter? RemotePlugins, IPluginService Plugins, Func<IProjectServices> Sessions, RemoteTerminalCatalogAdapter? RemoteCatalog, ITerminalCatalogService? Catalog);

    private static DesktopStartup PrepareDesktop(TailscaleConnection? chosen)
    {
        StartupPhases.Mark("desktop-prepare-start");
        var endpoint = chosen?.Address ?? Environment.GetEnvironmentVariable("SHARPRAIL_REMOTE");
        var initialRoot = Environment.GetEnvironmentVariable("SHARPRAIL_ROOT") ?? (chosen is null ? Directory.GetCurrentDirectory() : "");
        var profileDirectory = chosen is null ? Environment.GetEnvironmentVariable("SHARPRAIL_PROFILE") : TailscaleClientDirectory();
        var profile = profileDirectory is null ? ProfileStore.OpenDefault(initialRoot) : new ProfileStore(profileDirectory);
        var token = chosen?.Token ?? Environment.GetEnvironmentVariable("SHARPRAIL_TOKEN") ?? "";
        // The local host's state always migrates, so a remote session never leaves pre-host fields behind.
        var local = profile.OpenState();
        var remote = !string.IsNullOrEmpty(endpoint);
        // Every window's project session is the same client of the host as the state subscription.
        var connection = new HostConnection();
        IHostStateService stateService = remote ? new RemoteStateAdapter(new Uri(endpoint!), token, connection) : new LocalStateAdapter(local);
        var remoteTerminals = remote ? new RemoteTerminalAdapter(new Uri(endpoint!), token) : null;
        // Local sessions belong to the app's host: they survive their windows and end when the app quits.
        var localTerminals = remoteTerminals is null ? new PtyTerminalService(recordingsDirectory: Path.Combine(profile.DirectoryPath, "terminals"),
            replayBytes: () => local.Current.Settings.TerminalReplayKb * 1024, keepAfterExit: () => local.Current.Settings.TerminalKeepAfterExit) : null;
        // A removed workspace takes its shells with it, whichever window removed it.
        if (localTerminals is not null) local.WorkspaceRemoved += path => _ = Task.Run(async () => await localTerminals.CloseWorkspaceAsync(path));
        local.Terminals = localTerminals;
        // The app's own host runs its plugins in process; a remote host runs them where it runs.
        var loopback = remote ? null : new SharpRail.Host.Remote.LoopbackServer(localTerminals, local);
        var tailscale = new TailscaleServing();
        var runtime = loopback is null ? null : new SharpRail.Host.Core.Plugins.PluginRuntime(new()
        {
            StateDirectory = profile.DirectoryPath,
            State = local,
            PublicBaseUrl = () => loopback.BaseUrl,
            Terminals = localTerminals,
            Builtins = SharpRail.Host.Core.Plugins.BuiltinPlugins.Create(tailscale)
        });
        if (loopback is not null) loopback.Plugins = runtime;
        var listener = runtime is null ? null : new SharpRail.Host.Remote.HostListener(initialRoot, local, localTerminals, runtime);
        listener?.ConfigureTailscale(tailscale);
        runtime?.Start();
        var remotePlugins = remote ? new RemotePluginAdapter(new Uri(endpoint!), token) : null;
        IPluginService plugins = remotePlugins ?? (IPluginService)new LocalPluginAdapter(runtime!);
        Func<IProjectServices> sessions = remote
            ? () => new RemoteProjectAdapter(new Uri(endpoint!), token, connection)
            : () => new LocalProjectAdapter(new ProjectServices(initialRoot, local, runtime!.AllowsExternalFile));
        // Which terminal tabs exist is the host's to say, so every window and every other client shows the same ones.
        var remoteCatalog = remote ? new RemoteTerminalCatalogAdapter(new Uri(endpoint!), token) : null;
        ITerminalCatalogService? catalog = remoteCatalog is not null ? remoteCatalog : localTerminals is not null ? new LocalTerminalCatalogAdapter(localTerminals) : null;
        StartupPhases.Mark("desktop-prepare-end");
        return new(endpoint, token, initialRoot, profile, local, remote, connection, stateService, remoteTerminals, localTerminals, loopback, runtime, listener, remotePlugins, plugins, sessions, remoteCatalog, catalog);
    }

    private void StartDesktop(IClassicDesktopStyleApplicationLifetime desktop, TailscaleConnection? chosen)
    {
        var prepared = chosen is null && startup is not null ? startup.GetAwaiter().GetResult() : PrepareDesktop(chosen);
        var (endpoint, token, initialRoot, profile, local, remote, connection, stateService, remoteTerminals, localTerminals, loopback, runtime, listener, remotePlugins, plugins, sessions, remoteCatalog, catalog) = prepared;
        var state = new SharedState(stateService, profile.Data.Preferences, remote ? null : local.Current, connection);
        string Renderer() => profile.Data.Preferences.TerminalRenderer;
        Terminals = remoteTerminals is not null
            ? TerminalBackends.Ghostty(new RemoteTerminalConnection(new Uri(endpoint!), token, remoteTerminals), Renderer)
            : localTerminals is not null ? TerminalBackends.Ghostty(new LoopbackTerminals(new LocalTerminalAdapter(localTerminals), loopback!), Renderer)
            : launch => TerminalBackends.Unavailable("Embedded terminals currently require macOS.");
        var workbench = new Workbench(profile, state, Terminals, remote, sessions, plugins, catalog)
        {
            Endpoint = remote ? endpoint! : "local",
            Listener = listener,
            Notifier = OperatingSystem.IsMacOS() ? Notifications.MacNotifier.Create() : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? new Notifications.LinuxNotifier() : null
        };
        StartupPhases.Mark("app-services-created");
        if (OperatingSystem.IsMacOS()) ApplicationMenu.Install(this, workbench.Commands);
        foreach (var slot in profile.Data.Windows.ToArray())
        {
            var root = slot.LastProject.Length > 0 ? slot.LastProject : slot == profile.Data.Windows[0] ? initialRoot : "";
            var window = workbench.Open(slot, root);
            // `--link <location>` opens a serialized location in the first window once it has restored its own.
            if (desktop.MainWindow is null && desktop.Args is { } args && Array.IndexOf(args, "--link") is >= 0 and var at && at + 1 < args.Length)
                window.StartLink = args[at + 1];
            desktop.MainWindow ??= window;
            if (window != desktop.MainWindow) window.Show();
        }
        workbench.Commands.Shutdown = () => desktop.TryShutdown();
        desktop.ShutdownRequested += (_, _) => workbench.ShuttingDown = true;
        desktop.Exit += (_, _) =>
        {
            (stateService as IDisposable)?.Dispose(); remoteTerminals?.Dispose(); remoteCatalog?.Dispose(); remotePlugins?.Dispose();
            // Off the UI thread: ending shells awaits their exit, which a blocked dispatcher would never resume.
            // Plugins stop first, before the terminals and the loopback server they reach.
            Task.Run(async () =>
            {
                if (listener is not null) await listener.DisposeAsync();
                if (runtime is not null) await runtime.DisposeAsync();
                if (loopback is not null) await loopback.DisposeAsync();
                if (localTerminals is not null) await localTerminals.DisposeAsync();
                if (local is not null) await local.StopLanguagesAsync();
            }).GetAwaiter().GetResult();
        };
    }
#endif
}