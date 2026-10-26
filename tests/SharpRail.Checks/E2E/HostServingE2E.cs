using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class HostServingE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "host-serving-ui"), serving: true);
        object? Session() => typeof(SharpRail.UI.WorkbenchWindow).GetField("host", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(app.Window);
        var host = Session();
        var document = app.Find<Control>("FilesTree");
        using var peer = app.NewWindow();
        var settings = new SettingsWindow(app.Window, () => { }, section: "Host");
        var other = new SettingsWindow(peer.Window, () => { }, section: "Host");
        settings.Show(app.Window); other.Show(peer.Window); Settle();
        T Field<T>(SettingsWindow view, string name) where T : Control => view.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Sections(SettingsWindow view)
        {
            var connect = Field<Border>(view, "HostConnectSection");
            var share = Field<Border>(view, "HostShareSection");
            Require(connect.GetLogicalDescendants().Contains(Field<Button>(view, "HostTailscaleConnect")) &&
                share.GetLogicalDescendants().Contains(Field<StackPanel>(view, "HostControls")) &&
                !connect.GetLogicalDescendants().Contains(Field<StackPanel>(view, "HostControls")),
                "Connect and listener actions must belong to separate Host cards.");
            Require(string.Equals(Field<TextBlock>(view, "HostShareHeading").Text, "Share this app as a host", StringComparison.OrdinalIgnoreCase) &&
                connect.GetLogicalDescendants().OfType<TextBlock>().Any(text => string.Equals(text.Text, "Connect to another host", StringComparison.OrdinalIgnoreCase)),
                "Host cards must distinguish using another computer from sharing this app.");
            var connectPoint = connect.TranslatePoint(default, view)!.Value;
            var sharePoint = share.TranslatePoint(default, view)!.Value;
            Require(sharePoint.Y >= connectPoint.Y + connect.Bounds.Height + 16 && connect.BorderThickness.Top > 0 && share.BorderThickness.Top > 0,
                "Host cards must remain visibly separated by borders and spacing.");
        }
        Sections(settings);
        using (var unavailable = new E2eWorkspace(Path.Combine(root, "host-unavailable-ui")))
        {
            var unavailableSettings = new SettingsWindow(unavailable.Window, () => { }, section: "Host");
            unavailableSettings.Show(unavailable.Window); Settle();
            Sections(unavailableSettings);
            Require(!Field<StackPanel>(unavailableSettings, "HostControls").IsVisible &&
                Field<TextBlock>(unavailableSettings, "HostDescription").Text == "This workbench has no embedded host to serve.",
                "A workbench without a listener must explain sharing availability without hiding the connect card.");
            unavailableSettings.Close();
        }
        var start = Field<Button>(settings, "HostToggle");
        var otherStart = Field<Button>(other, "HostToggle");
        Require(app.Workbench.Listener!.Endpoint is null && Field<TextBox>(settings, "HostAddress").Text == "127.0.0.1" &&
            Field<TextBox>(settings, "HostToken").PasswordChar != default, "Serving must begin off, on loopback, with a masked token.");
        Field<TextBox>(settings, "HostAddress").Text = "invalid";
        app.Click(start);
        Require(Field<TextBlock>(settings, "SettingsError").IsVisible && app.Workbench.Listener.Endpoint is null,
            "Invalid listener settings must remain editable and start nothing.");
        Field<TextBox>(settings, "HostAddress").Text = "127.0.0.1";
        Field<TextBox>(settings, "HostPort").Text = "0";
        app.Click(start);
        Until(() => start.IsEnabled && Equals(start.Content, "Stop listening") && Equals(otherStart.Content, "Stop listening"));
        Require(ReferenceEquals(host, Session()) && ReferenceEquals(document, app.Find<Control>("FilesTree")) && !app.Workbench.Remote,
            "Starting serving must retain the direct session and mounted local controls.");
        using (var client = new E2eWorkspace(app.Workbench.Listener.Endpoint!, app.Workbench.Listener.Token,
            app.Root, Path.Combine(root, "host-remote-profile"), app.Root))
        {
            var remoteSettings = new SettingsWindow(client.Window, () => { }, section: "Host");
            remoteSettings.Show(client.Window); Settle();
            Sections(remoteSettings);
            Require(client.Workbench.Remote && !Field<StackPanel>(remoteSettings, "HostControls").IsVisible &&
                Field<TextBlock>(remoteSettings, "HostDescription").Text == "This app is connected to a remote host. Start serving from the app running the embedded host.",
                "A remote desktop client must explain where sharing starts without presenting listener controls.");
            remoteSettings.Close();
        }
        using var remote = new RemoteStateAdapter(app.Workbench.Listener.Endpoint!, app.Workbench.Listener.Token);
        var changed = Task.Run(async () => await remote.ChangeAsync([HostStateChange.Setting("file-width", "97")]));
        Until(() => changed.IsCompleted && app.Workbench.State.Preferences.FileLineWidth == 97);
        Require(changed.IsCompletedSuccessfully && peer.Workbench.State.Preferences.FileLineWidth == 97,
            "Remote changes must reach both local windows through the existing shared-state subscription.");
        Settle(500);
        document = app.Find<Control>("FilesTree");
        app.Click(otherStart);
        Until(() => app.Workbench.Listener.Endpoint is null && start.IsEnabled && Equals(start.Content, "Start listening"));
        Require(ReferenceEquals(host, Session()) && ReferenceEquals(document, app.Find<Control>("FilesTree")),
            "Stopping serving from another window must preserve the embedded UI and session.");
        app.Click(start);
        Until(() => start.IsEnabled && app.Workbench.Listener.Endpoint is not null);
        settings.Close();
        Require(app.Workbench.Listener.Endpoint is not null, "Closing Settings must not stop an active listener.");
        other.Close();
        Console.WriteLine("PASS Host Settings: separate connect/share cards, geometry, unavailable and remote states, validation, runtime start/stop across windows, remote state updates, retained local session and controls, serving outlives Settings");
    }
}