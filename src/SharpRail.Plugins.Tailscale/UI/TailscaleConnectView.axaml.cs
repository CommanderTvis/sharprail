using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace SharpRail.Plugins.Tailscale.UI;

/// <summary>The builtin's pre-connection surface; it runs without a host projection or plugin RPC.</summary>
public sealed partial class TailscaleConnectView : UserControl
{
    private readonly string statePath;
    private readonly TextBox host;
    private readonly TextBlock progress;
    private readonly Button pair;
    private readonly Button discover;
    private readonly Button cancel;
    private readonly ComboBox hosts;
    private CancellationTokenSource? attempt;
    private TailscaleRemembered? remembered;
    private bool restored;
    private readonly bool scannedHost;
    public event Action<TailscaleConnection>? Connected;

    public TailscaleConnectView(string statePath, string? initialHost = null)
    {
        this.statePath = statePath;
        AvaloniaXamlLoader.Load(this);
        host = this.FindControl<TextBox>("TailscaleHost")!;
        scannedHost = !string.IsNullOrWhiteSpace(initialHost);
        if (scannedHost) host.Text = initialHost;
        progress = this.FindControl<TextBlock>("TailscaleProgress")!;
        pair = this.FindControl<Button>("TailscalePair")!;
        discover = this.FindControl<Button>("TailscaleDiscover")!;
        cancel = this.FindControl<Button>("TailscaleCancel")!;
        hosts = this.FindControl<ComboBox>("TailscaleHosts")!;
        discover.IsVisible = !OperatingSystem.IsAndroid();
        pair.Click += (_, _) => _ = PairAsync();
        discover.Click += (_, _) => _ = DiscoverAsync();
        cancel.Click += (_, _) => attempt?.Cancel();
        hosts.SelectionChanged += (_, _) => { if (hosts.SelectedItem is TailscalePeer peer) host.Text = peer.Address; };
        DetachedFromVisualTree += (_, _) => attempt?.Cancel();
        AttachedToVisualTree += (_, _) => _ = RestoreAsync();
    }

    private async Task RestoreAsync()
    {
        if (restored) return;
        restored = true;
        remembered = await Task.Run(() => TailscaleClient.Load(statePath));
        if (TopLevel.GetTopLevel(this) is null) return;
        if (scannedHost) await PairAsync();
        else if (remembered is { } saved && string.IsNullOrWhiteSpace(host.Text))
        {
            host.Text = saved.Host;
            await PairAsync();
        }
    }

    private void Busy(bool busy)
    {
        host.IsEnabled = pair.IsEnabled = discover.IsEnabled = hosts.IsEnabled = !busy;
        cancel.IsVisible = busy;
    }

    private async Task DiscoverAsync()
    {
        if (attempt is not null) return;
        using var current = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        attempt = current;
        Busy(true);
        progress.Text = "Looking for SharpRail hosts…";
        try
        {
            using var client = new TailscaleClient();
            var found = await Task.Run(() => client.DiscoverAsync(current.Token));
            if (current.IsCancellationRequested) return;
            hosts.ItemsSource = found;
            hosts.IsVisible = found.Count > 0;
            progress.Text = found.Count > 0 ? "Choose a host below." : "No hosts found. Check that the host's Tailscale plugin is enabled.";
        }
        catch (Exception error) when (Expected(error)) { progress.Text = current.IsCancellationRequested ? "Search cancelled." : error.Message; }
        finally { attempt = null; Busy(false); }
    }

    private async Task PairAsync()
    {
        if (attempt is not null || string.IsNullOrWhiteSpace(host.Text)) return;
        var pairing = remembered is { } saved && saved.Host == host.Text.Trim() ? saved : TailscaleClient.NewPairing(host.Text);
        using var current = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        attempt = current;
        Busy(true);
        try
        {
            using var client = new TailscaleClient();
            var connection = await Task.Run(() => client.PairAsync(pairing, text => Dispatcher.UIThread.Post(() =>
            {
                if (attempt == current) progress.Text = text;
            }), current.Token));
            current.Token.ThrowIfCancellationRequested();
            await Task.Run(() => TailscaleClient.Save(statePath, pairing), current.Token);
            remembered = pairing;
            Connected?.Invoke(connection);
        }
        catch (Exception error) when (Expected(error))
        {
            progress.Text = current.IsCancellationRequested ? "Pairing cancelled or timed out." : "Could not pair: " + error.Message;
            remembered = null;
        }
        finally { attempt = null; Busy(false); }
    }

    private static bool Expected(Exception error) => error is IOException or HttpRequestException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or UnauthorizedAccessException or KeyNotFoundException or FormatException or InvalidOperationException;
}