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
            var root = profile.Data.LastProject.Length > 0 ? profile.Data.LastProject : initialRoot;
            var token = Environment.GetEnvironmentVariable("SHARPRAIL_TOKEN") ?? "";
            IProjectServices host = string.IsNullOrEmpty(endpoint)
                ? new LocalProjectAdapter(new ProjectServices(root))
                : new RemoteProjectAdapter(new Uri(endpoint), token);
            var remoteTerminals = string.IsNullOrEmpty(endpoint) ? null : new RemoteTerminalAdapter(new Uri(endpoint), token);
            var terminals = TerminalBackends.Ghostty(remoteTerminals is null ? null : new RemoteTerminalConnection(new Uri(endpoint!), token, remoteTerminals));
            var window = new WorkbenchWindow(host, root, profile, terminals, !string.IsNullOrEmpty(endpoint));
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => { (host as IDisposable)?.Dispose(); remoteTerminals?.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
