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
        Console.WriteLine("PASS Host Settings: validation, runtime start/stop across windows, remote state updates, retained local session and controls, serving outlives Settings");
    }
}