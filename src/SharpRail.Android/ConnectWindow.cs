using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using Grpc.Core;

using SharpRail.Host.Client;
using SharpRail.UI.Android.Windowing;
using SharpRail.UI.State;

namespace SharpRail.UI.Android;

/// <summary>The client's one screen of its own: the host's address and session token.</summary>
public sealed partial class ConnectWindow : Window
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TextBox address;
    private readonly TextBox token;
    private readonly TextBlock error;
    private readonly Button submit;
    private readonly Button scan;
    private CancellationTokenSource? attempt;
    private bool abandoned;

    /// <summary>Raised once the host answered with the entered token.</summary>
    public event Action<HostEndpoint>? Connected;

    public ConnectWindow(HostEndpoint? remembered)
    {
        // Stock controls and the system bars take their colours from the variant, which follows the app's theme.
        RequestedThemeVariant = Ui.Theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        AvaloniaXamlLoader.Load(this);
        address = this.FindControl<TextBox>("ConnectAddress")!;
        token = this.FindControl<TextBox>("ConnectToken")!;
        error = this.FindControl<TextBlock>("ConnectError")!;
        submit = this.FindControl<Button>("ConnectSubmit")!;
        this.FindControl<ContentControl>("ConnectBrand")!.Content = Ui.Icon("brand", Ui.Accent, 40);
        address.Text = remembered?.Address ?? "";
        token.Text = remembered?.Token ?? "";
        TextInputOptions.SetContentType(address, TextInputContentType.Url);
        TextInputOptions.SetReturnKeyType(address, TextInputReturnKeyType.Next);
        TextInputOptions.SetReturnKeyType(token, TextInputReturnKeyType.Go);
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { token.Focus(); e.Handled = true; } };
        token.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = ConnectAsync(); e.Handled = true; } };
        address.TextChanged += (_, _) => Refresh();
        token.TextChanged += (_, _) => Refresh();
        scan = this.FindControl<Button>("ConnectScan")!;
        scan.Content = Ui.Row("qrScan", "Scan QR code", Ui.TextBrush);
        scan.Click += (_, _) => _ = ScanAsync();
        submit.Click += (_, _) => { if (attempt is null) _ = ConnectAsync(); else Abandon(); };
        Closed += (_, _) => Abandon();
        Refresh();
    }

    /// <summary>Tries the remembered host without waiting for a tap.</summary>
    public void ConnectNow() => _ = ConnectAsync();

    private void Refresh()
    {
        var connecting = attempt is not null;
        address.IsEnabled = token.IsEnabled = scan.IsEnabled = !connecting;
        submit.Content = connecting ? "Cancel" : "Connect";
        submit.IsEnabled = connecting || !string.IsNullOrWhiteSpace(address.Text) && !string.IsNullOrWhiteSpace(token.Text);
    }

    private async Task ConnectAsync()
    {
        if (attempt is not null) return;
        var endpoint = new HostEndpoint(address.Text?.Trim() ?? "", token.Text?.Trim() ?? "");
        error.IsVisible = false;
        if (endpoint.Uri is not { } uri) { Fail("Enter the host's address, like 192.168.1.20:54123."); return; }
        if (endpoint.Token.Length == 0) { Fail("Enter the host's session token."); return; }
        var current = attempt = new CancellationTokenSource(Timeout);
        abandoned = false;
        Refresh();
        string? failure = null;
        try
        {
            // The adapter's channel connects and authenticates on its first call.
            await Task.Run(async () =>
            {
                using var probe = new RemoteStateAdapter(uri, endpoint.Token);
                await probe.GetHandshakeAsync(current.Token);
            });
        }
        catch (RpcException fault) when (fault.StatusCode == StatusCode.Unauthenticated) { failure = "The host rejected this session token."; }
        catch (Exception fault) when (fault is RpcException or OperationCanceledException or IOException or HttpRequestException)
        {
            failure = "No host answered at " + uri.Authority + ". Check the address, and that the host is listening on a network this device can reach.";
        }
        attempt = null;
        current.Dispose();
        if (!IsVisible) return;
        Refresh();
        if (abandoned) return;
        if (failure is not null) Fail(failure);
        else Connected?.Invoke(endpoint);
    }

    /// <summary>Shows why the app is back on this screen, such as a host that stopped answering.</summary>
    public void Report(string message) => Fail(message);

    // The desktop app's Settings › Host draws the address and token as one code.
    private async Task ScanAsync()
    {
        if (AndroidWindows.Instance.Activity is not { } activity) return;
        error.IsVisible = false;
        string? scanned;
        try { scanned = await QrScanner.ScanAsync(activity); }
        catch (IOException) { Fail("This device has no QR scanner. Type the address and token instead."); return; }
        if (scanned is null || !IsVisible) return;
        if (!HostLink.TryParse(scanned, out var host, out var session)) { Fail("That code is not a SharpRail host."); return; }
        address.Text = host; token.Text = session;
        await ConnectAsync();
    }

    private void Abandon()
    {
        abandoned = true;
        attempt?.Cancel();
    }

    private void Fail(string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }
}