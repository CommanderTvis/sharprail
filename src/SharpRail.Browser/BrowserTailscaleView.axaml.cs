using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.UI;

namespace SharpRail.UI.Browser;

/// <summary>
/// The browser's Tailscale option: an explicit download of the node (progress, cancel, cached per build), then sign-in in
/// a new tab and the shared host-approval picker over the tab's own node.
/// </summary>
internal sealed partial class BrowserTailscaleView : UserControl
{
    // The pairing secret lives in the page's in-memory file system for the shared picker, mirrored to localStorage.
    internal const string PairingPath = "/sharprail/tailscale-client.json";
    private const string PairingKey = "tailscale-client";
    private readonly BrowserTailscaleNetwork network = BrowserTailscaleNetwork.Instance;
    private readonly TailscaleWasmModule module = BrowserTailscaleNetwork.Module;
    private readonly bool autoConnect;
    private readonly StackPanel downloadPanel;
    private readonly Button download;
    private readonly ProgressBar progress;
    private readonly Button cancelDownload;
    private readonly TextBlock status;
    private readonly Button login;
    private readonly Button logout;
    private readonly Button retry;
    private readonly ContentControl picker;
    private CancellationTokenSource? lifetime;
    private bool signingIn;
    private bool mountedPicker;
    private CancellationToken? nodeToken;
    private string? openedUrl;
    private string? authUrl;
    private bool busy;
    internal event Action<TailscaleConnection>? Connected;
    internal event Action? SignedOut;

    internal BrowserTailscaleView(bool autoConnect)
    {
        this.autoConnect = autoConnect;
        AvaloniaXamlLoader.Load(this);
        downloadPanel = this.FindControl<StackPanel>("TailscaleDownloadPanel")!;
        download = this.FindControl<Button>("TailscaleDownload")!;
        progress = this.FindControl<ProgressBar>("TailscaleDownloadProgress")!;
        cancelDownload = this.FindControl<Button>("TailscaleDownloadCancel")!;
        status = this.FindControl<TextBlock>("TailscaleStatus")!;
        login = this.FindControl<Button>("TailscaleLogin")!;
        logout = this.FindControl<Button>("TailscaleLogout")!;
        retry = this.FindControl<Button>("TailscaleRetry")!;
        picker = this.FindControl<ContentControl>("TailscalePicker")!;
        download.Click += (_, _) => _ = module.DownloadAsync();
        cancelDownload.Click += (_, _) => module.Cancel();
        login.Click += (_, _) => _ = LoginAsync();
        logout.Click += (_, _) => _ = LogoutAsync();
        retry.Click += (_, _) => Restart();
        AttachedToVisualTree += (_, _) => { module.Changed += OnModuleChanged; Restart(); };
        DetachedFromVisualTree += (_, _) =>
        {
            module.Changed -= OnModuleChanged;
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        };
    }

    private void OnModuleChanged() => Dispatcher.UIThread.Post(Render);

    private void Restart()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = new();
        retry.IsVisible = false;
        Show("");
        if (module.State is TailscaleWasmState.Cached) _ = RunNodeAsync(lifetime.Token);
        else if (module.State is not TailscaleWasmState.Downloading) _ = module.CheckAsync(lifetime.Token);
        Render();
    }

    private void Show(string text)
    {
        status.Text = text;
        status.IsVisible = text.Length > 0;
    }

    private void Render()
    {
        if (lifetime is not { } current) return;
        var state = module.State;
        downloadPanel.IsVisible = state is TailscaleWasmState.NotDownloaded or TailscaleWasmState.Downloading or TailscaleWasmState.Failed && module.Manifest is not null;
        var downloading = state == TailscaleWasmState.Downloading;
        download.IsVisible = !downloading;
        download.Content = state == TailscaleWasmState.Failed ? "Retry download" : "Download Tailscale (" + module.Manifest?.TransferLabel + ")";
        progress.IsVisible = cancelDownload.IsVisible = downloading;
        progress.Value = module.Progress;
        switch (state)
        {
            case TailscaleWasmState.Checking: Show("Checking for Tailscale…"); break;
            case TailscaleWasmState.Downloading: Show("Downloading Tailscale… " + (int)(module.Progress * 100) + "%"); break;
            case TailscaleWasmState.NotDownloaded: Show(""); break;
            case TailscaleWasmState.Failed:
                Show(module.Error ?? "The Tailscale download failed.");
                retry.IsVisible = module.Manifest is null;
                break;
            case TailscaleWasmState.Cached when nodeToken != current.Token: _ = RunNodeAsync(current.Token); break;
        }
    }

    private async Task RunNodeAsync(CancellationToken token)
    {
        if (nodeToken == token) return;
        nodeToken = token;
        Show("Starting Tailscale…");
        try
        {
            await RestorePairingAsync();
            await network.StartAsync(token);
            await WatchAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Expected(error))
        {
            if (token.IsCancellationRequested) return;
            Show(error.Message);
            retry.IsVisible = true;
        }
        finally { if (nodeToken == token) nodeToken = null; }
    }

    private async Task WatchAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var current = await network.StatusAsync(token);
            token.ThrowIfCancellationRequested();
            authUrl = current.AuthUrl;
            if (!busy)
            {
                login.IsVisible = !current.Online && current.State is "NeedsLogin" or "NoState" or "Stopped";
                logout.IsVisible = current.State is not ("NeedsLogin" or "NoState");
                Show(current.Online ? "" : current.Error is { Length: > 0 } failure ? "Tailscale could not start: " + failure : current.State switch
                {
                    "NeedsMachineAuth" => "Your tailnet administrator must approve this device before it can connect.",
                    "NeedsLogin" => signingIn ? "Finish signing in in the Tailscale tab, then return here." : "",
                    _ => "Connecting to Tailscale…"
                });
                if (current.Online && !mountedPicker)
                {
                    mountedPicker = true;
                    signingIn = false;
                    var view = new TailscaleConnectView(PairingPath, network, restorePairing: autoConnect);
                    view.Connected += connection => _ = ConnectAsync(connection);
                    picker.Content = view;
                }
                else if (!current.Online && mountedPicker)
                {
                    picker.Content = null;
                    mountedPicker = false;
                }
                if (signingIn && !current.Online && TailscalePeers.LoginUrl(current.AuthUrl) is { } url && openedUrl != url.AbsoluteUri) Open(url);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private async Task ConnectAsync(TailscaleConnection connection)
    {
        await SavePairingAsync();
        Connected?.Invoke(connection);
    }

    private async Task LoginAsync()
    {
        if (busy || lifetime is not { } current) return;
        // A sign-in page already known opens straight from the click, so pop-up blockers see the gesture.
        if (TailscalePeers.LoginUrl(authUrl) is { } known) { Open(known); return; }
        busy = true;
        login.IsEnabled = false;
        try
        {
            signingIn = true;
            await network.LoginAsync(current.Token);
            Show("Preparing sign-in. Tailscale opens in a new tab when it is ready.");
        }
        catch (Exception error) when (Expected(error)) { if (!current.IsCancellationRequested) Show(error.Message); }
        finally { busy = false; login.IsEnabled = true; }
    }

    private void Open(Uri url)
    {
        openedUrl = url.AbsoluteUri;
        try { BrowserInterop.OpenLink(url.AbsoluteUri); }
        catch (Exception error) when (error is System.Runtime.InteropServices.JavaScript.JSException) { Show("The browser blocked the sign-in tab. Select “Open sign-in page again”."); }
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
            await ForgetPairingAsync();
            SignedOut?.Invoke();
            signingIn = false;
            openedUrl = null;
            authUrl = null;
            login.Content = "Sign in to Tailscale";
            Show("");
        }
        catch (Exception error) when (Expected(error)) { if (!current.IsCancellationRequested) Show(error.Message); }
        finally { busy = false; logout.IsEnabled = true; }
    }

    /// <summary>Forgets this browser's SharpRail pairing secret (memory file system and localStorage).</summary>
    internal static async Task ForgetPairingAsync()
    {
        BrowserInterop.Save(PairingKey, "");
        await Task.Run(() => { if (File.Exists(PairingPath)) File.Delete(PairingPath); });
    }

    private static async Task RestorePairingAsync()
    {
        var saved = BrowserInterop.Load(PairingKey);
        if (string.IsNullOrEmpty(saved)) return;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PairingPath)!);
            File.WriteAllText(PairingPath, saved);
        });
    }

    private static async Task SavePairingAsync()
    {
        var text = await Task.Run(() => File.Exists(PairingPath) ? File.ReadAllText(PairingPath) : "");
        BrowserInterop.Save(PairingKey, text);
    }

    private static bool Expected(Exception error) => error is IOException or HttpRequestException or OperationCanceledException or
        UnauthorizedAccessException or System.Text.Json.JsonException or ObjectDisposedException or System.Runtime.InteropServices.JavaScript.JSException;
}