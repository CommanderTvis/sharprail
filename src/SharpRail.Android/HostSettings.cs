using Avalonia.Controls;

using SharpRail.UI.Android;

namespace SharpRail.UI.Panels;

// Settings › Host on a client: the host it is connected to, and the way back to the connect screen.
public sealed partial class SettingsWindow
{
    private Control ServingSettings()
    {
        var page = Page("HostPage");
        var controls = PageControl<StackPanel>(page, "HostControls");
        controls.Children.Clear();
        PageControl<TextBlock>(page, "HostDescription").Text = ClientSession.Current?.Endpoint is { Uri: { } address }
            ? "This app is a client of the host at " + address.Authority + ". Its projects, settings, plugins and terminals live there."
            : "This app is not connected to a host.";
        var disconnect = new Button { Name = "HostDisconnect", Content = "Disconnect" };
        disconnect.Click += (_, _) =>
        {
            Close();
            ClientSession.Current?.Disconnect();
        };
        controls.Children.Add(new TextBlock { Text = "Disconnecting forgets the session token on this device.", Classes = { "settings-description" } });
        controls.Children.Add(disconnect);
        return page;
    }
}