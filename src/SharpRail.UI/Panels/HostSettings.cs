using System.Globalization;
using System.Net;

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace SharpRail.UI.Panels;

public sealed partial class SettingsWindow
{
    private void ListenerChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!lifetime.IsCancellationRequested && section == "Host")
            foreach (var refresh in refreshers) refresh();
    });

    private Control ServingSettings()
    {
        var page = Page("HostPage");
        if (window.Workbench.Listener is not { } listener)
        {
            PageControl<StackPanel>(page, "HostControls").IsVisible = false;
            PageControl<TextBlock>(page, "HostDescription").Text = window.Workbench.Remote
                ? "This app is connected to a remote host. Start serving from the app running the embedded host."
                : "This workbench has no embedded host to serve.";
            return page;
        }
        var address = PageControl<TextBox>(page, "HostAddress");
        var port = PageControl<TextBox>(page, "HostPort");
        var token = PageControl<TextBox>(page, "HostToken");
        var status = PageControl<TextBlock>(page, "HostStatus");
        var toggle = PageControl<Button>(page, "HostToggle");
        var copyEndpoint = PageControl<Button>(page, "HostCopyEndpoint");
        var copyToken = PageControl<Button>(page, "HostCopyToken");
        address.Text = listener.Address.ToString();
        port.Text = listener.Port.ToString(CultureInfo.InvariantCulture);
        token.Text = listener.Token;
        var busy = false;
        void Refresh()
        {
            var running = listener.Endpoint is not null;
            address.IsEnabled = port.IsEnabled = token.IsEnabled = !running && !busy;
            toggle.IsEnabled = !busy;
            toggle.Content = busy ? running ? "Stopping…" : "Starting…" : running ? "Stop listening" : "Start listening";
            status.Text = running ? "Listening at " + listener.Endpoint : "Not listening";
            copyEndpoint.IsEnabled = copyToken.IsEnabled = running && !busy;
            if (running)
            {
                address.Text = listener.Address.ToString();
                port.Text = listener.Port.ToString(CultureInfo.InvariantCulture);
                token.Text = listener.Token;
            }
        }
        refreshers.Add(Refresh);
        Refresh();
        toggle.Click += async (_, _) =>
        {
            error.IsVisible = false;
            var stopping = listener.Endpoint is not null;
            if (!stopping && (!IPAddress.TryParse(address.Text, out _) ||
                !int.TryParse(port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 65535 ||
                string.IsNullOrWhiteSpace(token.Text)))
            {
                error.Text = "Enter an IP address, a port from 0 to 65535, and a session token.";
                error.IsVisible = true;
                return;
            }
            busy = true; Refresh();
            try
            {
                if (stopping) await listener.StopAsync(lifetime.Token);
                else await listener.StartAsync(IPAddress.Parse(address.Text!), int.Parse(port.Text!, CultureInfo.InvariantCulture), token.Text!, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception failure)
            {
                error.Text = "The host could not " + (stopping ? "stop" : "start") + " listening: " + failure.Message;
                error.IsVisible = true;
            }
            finally { busy = false; Refresh(); }
        };
        copyEndpoint.Click += async (_, _) =>
        {
            if (listener.Endpoint is { } endpoint && Clipboard is { } clipboard) await clipboard.SetTextAsync(endpoint.ToString());
        };
        copyToken.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(listener.Token);
        };
        return page;
    }
}