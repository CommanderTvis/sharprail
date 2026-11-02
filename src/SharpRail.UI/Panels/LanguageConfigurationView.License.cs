using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

/// <summary>The license block of a licensed language server (IntelliJ): the plugin's published state and its actions, run on the host off the UI thread.</summary>
internal sealed partial class LanguageConfigurationView
{
    private CancellationTokenSource? licenseCall;
    private int openedPage;
    private string licenseBusy = "";

    private void WireLicense()
    {
        Find<Button>("StartTrial").Click += async (_, _) => await RunLicenseAsync("trial");
        Find<Button>("SignIn").Click += async (_, _) => await RunLicenseAsync("signIn");
        Find<Button>("RefreshLicense").Click += async (_, _) => await RunLicenseAsync("refresh");
        Find<Button>("RemoveLicense").Click += async (_, _) => await RunLicenseAsync("removeLicense");
        Find<Button>("SignOut").Click += async (_, _) => await RunLicenseAsync("signOut");
        Find<Button>("UseCode").Click += async (_, _) =>
        {
            if (await RunLicenseAsync("code", new() { ["activationCode"] = Find<TextBox>("ActivationCode").Text ?? "" })) Find<TextBox>("ActivationCode").Text = "";
        };
        Find<Button>("AddServer").Click += async (_, _) => await RunLicenseAsync("serverAdd", new() { ["serverAddress"] = Find<TextBox>("LicenseServer").Text ?? "" });
        Find<Button>("ReconnectServer").Click += async (_, _) => await RunLicenseAsync("serverReconnect");
        // Cancelling stops the pending host call, which ends the server's wait on the browser sign-in.
        Find<Button>("CancelLicense").Click += (_, _) => licenseCall?.Cancel();
        DetachedFromVisualTree += (_, _) => licenseCall?.Cancel();
    }

    private JsonObject? LicenseContents()
    {
        if (server.PluginState.Length == 0) return null;
        try { return JsonNode.Parse(server.PluginState) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private void ShowLicense()
    {
        var state = LicenseContents();
        Find<StackPanel>("LicensePanel").IsVisible = state is not null && state["kind"] is not null;
        if (state is null || state["kind"] is null) return;
        var busy = licenseCall is not null ? licenseBusy : state["busy"]?.ToString() ?? "";
        var kind = state["kind"]!.ToString();
        var details = new List<string> { state["message"]!.ToString() };
        if (state["holder"]?.ToString() is { Length: > 0 } holder && kind is "licensed" or "session-expired" or "grace") details.Add("Licensed to " + holder + ".");
        if (state["daysLeft"] is JsonValue days && days.TryGetValue<int>(out var left) && kind == "licensed")
            details.Add(left == 1 ? "1 day left" + Through(state) + "." : left + " days left" + Through(state) + ".");
        if (state["account"]?["displayName"]?.ToString() is { Length: > 0 } account) details.Add("Signed in as " + account + ".");
        if (state["serverAddress"]?.ToString() is { Length: > 0 } address) details.Add("License server: " + address + ".");
        Find<TextBlock>("LicenseState").Text = string.Join(' ', details);
        var notice = Find<TextBlock>("LicenseNotice");
        notice.Text = state["notice"]?.ToString() ?? "";
        notice.IsVisible = notice.Text.Length > 0;
        notice.Foreground = kind is "parked" or "unlicensed" ? SharpRail.Plugins.UI.Kit.Ui.Danger : SharpRail.Plugins.UI.Kit.Ui.Warning;
        var problem = Find<TextBlock>("LicenseMessage");
        problem.Text = state["environment"]?.ToString() is { Length: > 0 } variable
            ? "The license is set through " + variable + ", which overrides any license activated here."
            : state["error"]?.ToString() ?? "";
        problem.IsVisible = problem.Text.Length > 0;
        var page = Find<TextBox>("LicensePage");
        if (state["page"] is JsonObject shown && shown["uri"]?.ToString() is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var link) && link.Scheme == "https" && busy.Length > 0)
        {
            page.Text = uri; page.IsVisible = true;
            if (shown["id"]?.GetValue<int>() is { } id && id != openedPage && TopLevel.GetTopLevel(this) is { } top)
            {
                openedPage = id;
                _ = LanguageProviderLinks.Launch(top, link);
            }
        }
        else page.IsVisible = false;
        var idle = busy.Length == 0;
        var licensed = kind is "licensed" or "session-expired" or "grace";
        var environment = state["environment"] is not null;
        Find<Button>("StartTrial").IsEnabled = idle && !environment;
        Find<Button>("SignIn").IsEnabled = idle && !environment;
        Find<Button>("UseCode").IsEnabled = idle && !environment;
        Find<Button>("AddServer").IsEnabled = idle && !environment;
        Find<Button>("RefreshLicense").IsEnabled = idle;
        Find<Button>("RemoveLicense").IsVisible = licensed && state["source"]?.ToString() is "subscription" or "activationCode" or "licenseServer";
        Find<Button>("SignOut").IsVisible = state["account"] is not null;
        Find<Button>("ReconnectServer").IsVisible = state["serverAddress"] is not null;
        Find<Button>("CancelLicense").IsVisible = !idle;
        Find<Button>("CancelLicense").Content = busy == "signIn" ? "Cancel sign-in" : "Cancel";
        var offered = Find<StackPanel>("Subscriptions");
        offered.Children.Clear();
        if (!licensed && state["subscriptions"] is JsonArray subscriptions && idle)
            foreach (var item in subscriptions.OfType<JsonObject>().Where(item => item["status"]?.ToString() == "Valid" && item["assetId"] is not null))
            {
                var button = new Button { Content = "Use " + item["product"] + " for " + item["licensedTo"] + ", valid through " + item["validThrough"], HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
                var asset = item["assetId"]!.ToString();
                button.Click += async (_, _) => await RunLicenseAsync("useSubscription", new() { ["assetId"] = asset });
                offered.Children.Add(button);
            }
    }

    private static string Through(JsonObject state) => state["validThrough"]?.ToString() is { Length: > 0 } through ? " (valid through " + through + ")" : "";

    private async Task<bool> RunLicenseAsync(string action, JsonObject? arguments = null)
    {
        if (licenseCall is not null || lifetime is not { } current) return false;
        var call = CancellationTokenSource.CreateLinkedTokenSource(current.Token);
        licenseCall = call; licenseBusy = action;
        ShowLicense();
        var message = Find<TextBlock>("ConfigurationMessage");
        message.Text = action == "signIn" ? "Waiting for the sign-in in your browser…" : "Working on the license…";
        // The browser page arrives in the plugin's state while the call waits, so the inventory is polled meanwhile.
        using var polling = new CancellationTokenSource();
        var poll = Task.Run(async () =>
        {
            while (!polling.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, polling.Token);
                    var rows = await host.LanguageAsync(new(workspace, Operation: "servers"), polling.Token);
                    if (rows.Servers.FirstOrDefault(row => row.Id == server.Id) is { } row)
                        Dispatcher.UIThread.Post(() => { if (licenseCall == call) { server = server with { PluginState = row.PluginState }; ShowLicense(); } });
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }
        });
        var succeeded = false;
        try
        {
            var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "action", Server: server.Id, Query: action, Configuration: arguments?.ToJsonString()), call.Token), call.Token);
            if (lifetime != current) return false;
            var outcome = result.State == "ready" ? JsonNode.Parse(result.Detail) as JsonObject : null;
            succeeded = outcome?["ok"]?.GetValue<bool>() == true;
            message.Text = outcome is null ? result.Detail : succeeded ? "License updated." : outcome["message"]?.ToString() ?? "";
        }
        catch (OperationCanceledException) { if (lifetime == current) message.Text = action == "signIn" ? "Sign-in cancelled." : "Cancelled."; }
        catch (Exception error) { if (lifetime == current) message.Text = error.Message; }
        finally
        {
            polling.Cancel();
            await poll;
            licenseCall = null; licenseBusy = "";
            call.Dispose();
        }
        if (lifetime == current)
        {
            try
            {
                var rows = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "servers"), current.Token), current.Token);
                if (rows.Servers.FirstOrDefault(row => row.Id == server.Id) is { } row) { server = server with { PluginState = row.PluginState, State = row.State, Detail = row.Detail }; }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException) { }
            ShowLicense();
        }
        return succeeded;
    }
}
