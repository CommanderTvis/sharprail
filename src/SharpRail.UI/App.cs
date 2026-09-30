using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI.State;
using SharpRail.UI.Rendering;
using SharpRail.UI.Terminal;

namespace SharpRail.UI;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Ui.ApplyResources(this);
        foreach (var state in new[] { "Selected", "SelectedPointerOver", "SelectedPressed" })
            Resources["TreeViewItemBackground" + state] = Ui.Hover;
    }

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
            Func<IProjectServices> sessions = remote
                ? () => new RemoteProjectAdapter(new Uri(endpoint!), token)
                : () => new LocalProjectAdapter(new ProjectServices(initialRoot, local));
            var remoteTerminals = remote ? new RemoteTerminalAdapter(new Uri(endpoint!), token) : null;
            var terminals = TerminalBackends.Ghostty(remoteTerminals is null ? null : new RemoteTerminalConnection(new Uri(endpoint!), token, remoteTerminals));
            var workbench = new Workbench(profile, state, terminals, remote, sessions);
            foreach (var slot in profile.Data.Windows.ToArray())
            {
                var root = slot.LastProject.Length > 0 ? slot.LastProject : slot == profile.Data.Windows[0] ? initialRoot : "";
                var window = workbench.Open(slot, root);
                desktop.MainWindow ??= window;
                if (window != desktop.MainWindow) window.Show();
            }
            desktop.ShutdownRequested += (_, _) => workbench.ShuttingDown = true;
            desktop.Exit += (_, _) => { (stateService as IDisposable)?.Dispose(); remoteTerminals?.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
