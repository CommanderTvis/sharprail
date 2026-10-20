using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.UI;

namespace SharpRail.UI.Android;

/// <summary>Browser sign-in followed by the existing host-approval picker over the app's private node.</summary>
internal sealed partial class EmbeddedTailscaleView : UserControl
{
    private readonly EmbeddedTailscaleNetwork network;
    private readonly string pairingPath;
    private readonly bool autoConnect;
    private readonly TextBlock status;
    private readonly Button login;
    private readonly Button logout;
    private readonly Button retry;
    private readonly ContentControl picker;
    private CancellationTokenSource? lifetime;
    private bool signingIn;
    private bool mountedPicker;
    private string? openedUrl;
    private bool busy;
    internal event Action<TailscaleConnection>? Connected;
    internal event Action? SignedOut;

    internal EmbeddedTailscaleView(EmbeddedTailscaleNetwork network, string pairingPath, bool autoConnect)
    {
        this.network = network;
        this.pairingPath = pairingPath;
        this.autoConnect = autoConnect;
        AvaloniaXamlLoader.Load(this);
        status = this.FindControl<TextBlock>("EmbeddedStatus")!;
        login = this.FindControl<Button>("EmbeddedLogin")!;
        logout = this.FindControl<Button>("EmbeddedLogout")!;
        retry = this.FindControl<Button>("EmbeddedRetry")!;
        picker = this.FindControl<ContentControl>("EmbeddedPicker")!;
        login.Click += (_, _) => _ = LoginAsync();
        logout.Click += (_, _) => _ = LogoutAsync();
        retry.Click += (_, _) => Restart();
        AttachedToVisualTree += (_, _) => Restart();
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    private void Restart()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = new();
        retry.IsVisible = false;
        _ = WatchAsync(lifetime.Token);
    }

    private async Task WatchAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var current = await network.RefreshAsync(token);
                token.ThrowIfCancellationRequested();
                if (!busy)
                {
                    login.IsVisible = !current.Online;
                    logout.IsVisible = current.State != "NeedsLogin";
                    status.Text = current.Online ? "" : current.State switch
                    {
                        "Running" => "No network connection. Turn on Wi-Fi or mobile data to reconnect.",
                        "NeedsMachineAuth" => "Your tailnet administrator must approve this device before it can connect.",
                        "NeedsLogin" => signingIn ? "Finish signing in in your browser, then return here." : "",
                        _ => "Connecting to Tailscale…"
                    };
                    status.IsVisible = status.Text.Length > 0;
                    if (current.Online && !mountedPicker)
                    {
                        mountedPicker = true;
                        var view = new TailscaleConnectView(pairingPath, network, restorePairing: autoConnect);
                        view.Connected += connection => Connected?.Invoke(connection);
                        picker.Content = view;
                    }
                    else if (!current.Online && mountedPicker)
                    {
                        picker.Content = null;
                        mountedPicker = false;
                    }
                    if (signingIn && !current.Online && EmbeddedTailscaleNetwork.LoginUrl(current.AuthUrl) is { } url && openedUrl != url.AbsoluteUri)
                        await OpenLoginAsync(url);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Expected(error))
        {
            if (!token.IsCancellationRequested) { status.Text = error.Message; retry.IsVisible = true; }
        }
    }

    private async Task LoginAsync()
    {
        if (busy || lifetime is not { } current) return;
        busy = true;
        login.IsEnabled = false;
        try
        {
            signingIn = true;
            var next = await network.LoginAsync(current.Token);
            if (current.IsCancellationRequested) return;
            if (EmbeddedTailscaleNetwork.LoginUrl(next.AuthUrl) is { } url) await OpenLoginAsync(url);
            else status.Text = "Preparing sign-in. Your browser will open when Tailscale is ready.";
        }
        catch (Exception error) when (Expected(error)) { if (!current.IsCancellationRequested) status.Text = error.Message; }
        finally { busy = false; login.IsEnabled = true; }
    }

    private async Task OpenLoginAsync(Uri url)
    {
        openedUrl = url.AbsoluteUri;
        if (TopLevel.GetTopLevel(this) is not { } top || !await top.Launcher.LaunchUriAsync(url))
        {
            openedUrl = null;
            status.Text = "No browser could open Tailscale sign-in. Install or enable a browser and retry.";
        }
        login.Content = "Open sign-in page again";
    }

    private async Task LogoutAsync()
    {
        if (busy || lifetime is not { } current) return;
        busy = true;
        logout.IsEnabled = false;
        picker.Content = null;
        mountedPicker = false;
        try
        {
            await network.LogoutAsync(current.Token);
            await Task.Run(() =>
            {
                var saved = TailscaleClient.Load(pairingPath);
                var directory = Path.GetDirectoryName(pairingPath)!;
                if (HostEndpoint.Load(directory) is { } endpoint && saved?.Token == endpoint.Token)
                    (endpoint with { Token = "" }).Save(directory);
                if (saved is not null) TailscaleClient.Forget(pairingPath, saved.Token);
            });
            SignedOut?.Invoke();
            signingIn = false;
            openedUrl = null;
            login.Content = "Sign in to Tailscale";
            status.Text = "";
        }
        catch (Exception error) when (Expected(error)) { if (!current.IsCancellationRequested) status.Text = error.Message; }
        finally { busy = false; logout.IsEnabled = true; }
    }

    private static bool Expected(Exception error) => error is IOException or HttpRequestException or OperationCanceledException or
        UnauthorizedAccessException or System.Text.Json.JsonException or ObjectDisposedException;
}