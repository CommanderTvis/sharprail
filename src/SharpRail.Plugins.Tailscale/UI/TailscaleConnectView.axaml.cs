using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace SharpRail.Plugins.Tailscale.UI;

/// <summary>The builtin's pre-connection surface; it runs without a host projection or plugin RPC.</summary>
public sealed partial class TailscaleConnectView : UserControl
{
    private readonly string statePath;
    private readonly Func<TailscaleClient> createClient;
    private readonly bool embedded;
    private readonly bool restorePairing;
    private readonly TextBox host;
    private readonly TextBlock progress;
    private readonly Button pair;
    private readonly Button discover;
    private readonly Button cancel;
    private readonly ComboBox hosts;
    private readonly TextBlock hostLabel;
    private readonly Button manual;
    private bool selecting;
    private CancellationTokenSource? attempt;
    private TailscaleRemembered? remembered;
    private bool restored;
    public event Action<TailscaleConnection>? Connected;

    // Compiled XAML never uses the runtime loader; this public constructor only makes Avalonia register the view (AVLN3001).
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Obsolete("Construct TailscaleConnectView with its arguments.", true)]
    public TailscaleConnectView() => throw new System.NotSupportedException("TailscaleConnectView needs its constructor arguments.");

    public TailscaleConnectView(string statePath, ITailscaleClientNetwork? network = null, bool restorePairing = true)
    {
        this.statePath = statePath;
        this.restorePairing = restorePairing;
        embedded = network is not null;
        createClient = network is null ? () => new TailscaleClient() : () => new TailscaleClient(network);
        AvaloniaXamlLoader.Load(this);
        host = this.FindControl<TextBox>("TailscaleHost")!;
        progress = this.FindControl<TextBlock>("TailscaleProgress")!;
        pair = this.FindControl<Button>("TailscalePair")!;
        discover = this.FindControl<Button>("TailscaleDiscover")!;
        cancel = this.FindControl<Button>("TailscaleCancel")!;
        hosts = this.FindControl<ComboBox>("TailscaleHosts")!;
        discover.IsVisible = network is not null || !OperatingSystem.IsAndroid();
        pair.Click += (_, _) => _ = PairAsync();
        discover.Click += (_, _) => _ = DiscoverAsync();
        cancel.Click += (_, _) => attempt?.Cancel();
        hostLabel = this.FindControl<TextBlock>("TailscaleHostLabel")!;
        manual = this.FindControl<Button>("TailscaleManual")!;
        manual.Click += (_, _) => { manual.IsVisible = false; ShowHostField(true); };
        if (embedded)
        {
            this.FindControl<TextBlock>("TailscaleTitle")!.IsVisible = false;
            this.FindControl<TextBlock>("TailscaleHint")!.IsVisible = false;
        }
        hosts.SelectionChanged += (_, _) => { if (!selecting && hosts.SelectedItem is TailscalePeer peer) host.Text = peer.Address; };
        DetachedFromVisualTree += (_, _) => attempt?.Cancel();
        AttachedToVisualTree += (_, _) => _ = RestoreAsync();
    }

    private async Task RestoreAsync()
    {
        if (restored) return;
        restored = true;
        remembered = await Task.Run(() => TailscaleClient.Load(statePath));
        if (TopLevel.GetTopLevel(this) is null) return;
        if (remembered is { } saved && string.IsNullOrWhiteSpace(host.Text))
        {
            host.Text = saved.Host;
            if (restorePairing) await PairAsync();
            else if (embedded) await DiscoverAsync();
        }
        else if (embedded) await DiscoverAsync();
    }

    private void Busy(bool busy)
    {
        host.IsEnabled = manual.IsEnabled = pair.IsEnabled = discover.IsEnabled = hosts.IsEnabled = !busy;
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
            using var client = createClient();
            var found = await Task.Run(() => client.DiscoverAsync(current.Token));
            if (current.IsCancellationRequested) return;
            selecting = true;
            hosts.ItemsSource = found;
            var match = Match(found, host.Text);
            hosts.SelectedItem = match ?? found.FirstOrDefault();
            selecting = false;
            if (match is null && hosts.SelectedItem is TailscalePeer first) host.Text = first.Address;
            hosts.IsVisible = found.Count > 0;
            if (embedded && found.Count > 0)
            {
                ShowHostField(false);
                manual.IsVisible = true;
            }
            progress.Text = found.Count switch
            {
                0 => "No hosts found. Check that the host's Tailscale plugin is enabled.",
                1 => "Found " + found[0].Name + ".",
                _ => "Found " + found.Count + " hosts."
            };
        }
        catch (Exception error) when (Expected(error)) { progress.Text = current.IsCancellationRequested ? "Search cancelled." : error.Message; }
        finally { attempt = null; Busy(false); }
    }

    private void ShowHostField(bool visible) => hostLabel.IsVisible = host.IsVisible = visible;

    private static TailscalePeer? Match(IReadOnlyList<TailscalePeer> peers, string? text)
    {
        var name = text?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(name)) return null;
        bool Same(string value) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(value + ".", StringComparison.OrdinalIgnoreCase);
        return peers.FirstOrDefault(peer => Same(peer.Name) || Same(peer.Address));
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
            using var client = createClient();
            var connection = await Task.Run(() => client.PairAsync(pairing, text => Dispatcher.UIThread.Post(() =>
            {
                if (attempt == current) progress.Text = text;
            }), current.Token));
            current.Token.ThrowIfCancellationRequested();
            await Task.Run(() => TailscaleClient.Save(statePath, pairing, current.Token), current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (TopLevel.GetTopLevel(this) is null || attempt != current) return;
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