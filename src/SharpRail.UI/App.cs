using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.UI;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // The kit has no theme of its own; the catalogue's default applies until a window resolves the host's.
        Ui.Apply(Themes.Resolve(Themes.DefaultId));
        Ui.ApplyResources(this);
        foreach (var state in new[] { "Selected", "SelectedPointerOver", "SelectedPressed" })
            Resources["TreeViewItemBackground" + state] = Ui.Hover;
        // The reference's 20px chevron slot. Fluent's template sets its 12px margins directly, which outranks styles.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TreeViewItem>((_, e) =>
        {
            if (e.NameScope.Find<Panel>("PART_ExpandCollapseChevronContainer") is { } chevron) chevron.Margin = new Thickness(4, 0);
        });
    }

    // Creates terminal tabs for every window of this app, all attached to the app's one terminal host.
    public TerminalFactory Terminals { get; private set; } = launch => TerminalBackends.Unavailable("The app has not started.");

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var endpoint = Environment.GetEnvironmentVariable("SHARPRAIL_REMOTE");
            var initialRoot = Environment.GetEnvironmentVariable("SHARPRAIL_ROOT") ?? Directory.GetCurrentDirectory();
            var profileDirectory = Environment.GetEnvironmentVariable("SHARPRAIL_PROFILE");
            var profile = profileDirectory is null ? ProfileStore.OpenDefault(initialRoot) : new ProfileStore(profileDirectory);
            var token = Environment.GetEnvironmentVariable("SHARPRAIL_TOKEN") ?? "";
            // The local host's state always migrates, so a remote session never leaves pre-host fields behind.
            var local = profile.OpenState();
            var remote = !string.IsNullOrEmpty(endpoint);
            IHostStateService stateService = remote ? new RemoteStateAdapter(new Uri(endpoint!), token) : new LocalStateAdapter(local);
            var state = new SharedState(stateService, profile.Data.Preferences, remote ? null : local.Current);
            var remoteTerminals = remote ? new RemoteTerminalAdapter(new Uri(endpoint!), token) : null;
            // Local sessions belong to the app's host: they survive their windows and end when the app quits.
            var localTerminals = remoteTerminals is null && !OperatingSystem.IsWindows() ? new PtyTerminalService() : null;
            // The app's own host runs its plugins in process; a remote host runs them where it runs.
            var loopback = remote ? null : new SharpRail.Host.Remote.LoopbackServer(localTerminals);
            var runtime = loopback is null ? null : new SharpRail.Host.Core.Plugins.PluginRuntime(new()
            {
                StateDirectory = profile.DirectoryPath,
                State = local,
                PublicBaseUrl = () => loopback.BaseUrl,
                Terminals = localTerminals
            });
            if (loopback is not null) loopback.Plugins = runtime;
            runtime?.Start();
            var remotePlugins = remote ? new RemotePluginAdapter(new Uri(endpoint!), token) : null;
            IPluginService plugins = remotePlugins ?? (IPluginService)new LocalPluginAdapter(runtime!);
            Func<IProjectServices> sessions = remote
                ? () => new RemoteProjectAdapter(new Uri(endpoint!), token)
                : () => new LocalProjectAdapter(new ProjectServices(initialRoot, local, runtime!.AllowsExternalFile));
            var relay = localTerminals is null ? null : new LocalTerminalRelay(localTerminals, loopback);
            string Renderer() => profile.Data.Preferences.TerminalRenderer;
            Terminals = remoteTerminals is not null
                ? TerminalBackends.Ghostty(new RemoteTerminalConnection(new Uri(endpoint!), token, remoteTerminals), Renderer)
                : relay is not null ? TerminalBackends.Ghostty(new LocalTerminalAdapter(localTerminals!), relay.ConnectAsync, Renderer)
                : launch => TerminalBackends.Unavailable("Embedded terminals currently require macOS.");
            var workbench = new Workbench(profile, state, Terminals, remote, sessions, plugins) { Endpoint = remote ? endpoint! : "local" };
            foreach (var slot in profile.Data.Windows.ToArray())
            {
                var root = slot.LastProject.Length > 0 ? slot.LastProject : slot == profile.Data.Windows[0] ? initialRoot : "";
                var window = workbench.Open(slot, root);
                desktop.MainWindow ??= window;
                if (window != desktop.MainWindow) window.Show();
            }
            desktop.ShutdownRequested += (_, _) => workbench.ShuttingDown = true;
            desktop.Exit += (_, _) =>
            {
                (stateService as IDisposable)?.Dispose(); remoteTerminals?.Dispose(); remotePlugins?.Dispose();
                // Off the UI thread: ending shells awaits their exit, which a blocked dispatcher would never resume.
                // Plugins stop first, before the terminals and the loopback server they reach.
                Task.Run(async () =>
                {
                    if (runtime is not null) await runtime.DisposeAsync();
                    if (loopback is not null) await loopback.DisposeAsync();
                    if (relay is not null) await relay.DisposeAsync();
                    if (localTerminals is not null) await localTerminals.DisposeAsync();
                }).GetAwaiter().GetResult();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}