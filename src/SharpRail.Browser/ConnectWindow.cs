using System.Net;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using SharpRail.Host.Client;
using SharpRail.UI.State;

namespace SharpRail.UI.Browser;

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
    private bool closed;
    private bool completed;
    private string? approvedToken;
    private readonly bool localPage = HostEndpoint.IsLocal(HostEndpoint.Parse(BrowserInterop.Origin()));

    /// <summary>Raised once the host answered with the entered token.</summary>
    public event Action<HostEndpoint>? Connected;

    // Compiled XAML never uses the runtime loader; this public constructor only makes Avalonia register the view (AVLN3001).
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Obsolete("Construct ConnectWindow with its arguments.", true)]
    public ConnectWindow() => throw new System.NotSupportedException("ConnectWindow needs its constructor arguments.");

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
        token.Text = remembered?.LocalApproval == true ? "" : remembered?.Token ?? "";
        approvedToken = remembered is { LocalApproval: true, Token.Length: > 0 } ? remembered.Token : null;
        TextInputOptions.SetContentType(address, TextInputContentType.Url);
        TextInputOptions.SetReturnKeyType(address, TextInputReturnKeyType.Next);
        TextInputOptions.SetReturnKeyType(token, TextInputReturnKeyType.Go);
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { token.Focus(); e.Handled = true; } };
        token.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = ConnectAsync(); e.Handled = true; } };
        address.TextChanged += (_, _) => { approvedToken = null; Refresh(); };
        token.TextChanged += (_, _) => { approvedToken = null; Refresh(); };
        scan = this.FindControl<Button>("ConnectScan")!;
        scan.Content = Ui.Row("qrScan", "Scan QR code", Ui.TextBrush);
        scan.IsVisible = false;
        submit.Click += (_, _) => { if (attempt is null) _ = ConnectAsync(); else Abandon(); };
        Closed += (_, _) => { closed = true; Abandon(); };
        var tailscale = this.FindControl<Button>("ConnectTailscale")!;
        tailscale.Click += (_, _) => ShowTailscale(autoConnect: false);
        this.FindControl<Button>("ConnectManualBack")!.Click += (_, _) =>
        {
            this.FindControl<ContentControl>("ConnectTailscaleBody")!.Content = null;
            this.FindControl<StackPanel>("ConnectManual")!.IsVisible = true;
            tailscale.IsVisible = true;
            this.FindControl<Button>("ConnectManualBack")!.IsVisible = false;
        };
        Refresh();
    }

    /// <summary>Tries the remembered host without waiting for a tap; a tailnet host re-pairs through the browser's node.</summary>
    public void ConnectNow(bool tailnet = false)
    {
        if (tailnet) ShowTailscale(autoConnect: true);
        else _ = ConnectAsync();
    }

    // The node and its download live in tailscale.js, imported only from here on.
    private void ShowTailscale(bool autoConnect)
    {
        if (closed) return;
        Abandon();
        var view = new BrowserTailscaleView(autoConnect);
        view.SignedOut += () => { token.Text = ""; approvedToken = null; };
        view.Connected += next =>
        {
            if (closed || completed || !IsVisible) return;
            completed = true;
            Connected?.Invoke(new HostEndpoint(next.Address, next.Token, Tailnet: true));
        };
        this.FindControl<StackPanel>("ConnectManual")!.IsVisible = false;
        this.FindControl<Button>("ConnectTailscale")!.IsVisible = false;
        this.FindControl<ContentControl>("ConnectTailscaleBody")!.Content = view;
        this.FindControl<Button>("ConnectManualBack")!.IsVisible = true;
    }

    private void Refresh()
    {
        var connecting = attempt is not null;
        address.IsEnabled = token.IsEnabled = scan.IsEnabled = !connecting;
        submit.Content = connecting ? "Cancel" : "Connect";
        var local = localPage && HostEndpoint.IsLocal(HostEndpoint.Parse(address.Text ?? ""));
        this.FindControl<TextBlock>("ConnectLocalHint")!.IsVisible = local;
        submit.IsEnabled = connecting || !string.IsNullOrWhiteSpace(address.Text) && (local || !string.IsNullOrWhiteSpace(token.Text));
    }

    private async Task ConnectAsync()
    {
        if (attempt is not null) return;
        var endpoint = new HostEndpoint(address.Text?.Trim() ?? "", approvedToken ?? token.Text?.Trim() ?? "", approvedToken is not null);
        error.IsVisible = false;
        if (endpoint.Uri is not { } uri) { Fail("Enter the host's address, like localhost:54124."); return; }
        var requestApproval = endpoint.Token.Length == 0 && localPage && HostEndpoint.IsLocal(uri);
        if (endpoint.Token.Length == 0 && !requestApproval) { Fail("Enter the host's session token."); return; }
        var current = attempt = new CancellationTokenSource(requestApproval ? TimeSpan.FromSeconds(125) : Timeout);
        this.FindControl<TextBlock>("ConnectProgress")!.IsVisible = requestApproval;
        abandoned = false;
        Refresh();
        string? failure = null;
        try
        {
            if (requestApproval)
                endpoint = endpoint with { Token = await LocalBrowserApprovalClient.RequestAsync(uri, current.Token), LocalApproval = true };
            // The adapter's channel connects and authenticates on its first call.
            using var probe = new RemoteStateAdapter(uri, endpoint.Token);
            await probe.GetHandshakeAsync(current.Token);
        }
        catch (HttpRequestException fault) when (requestApproval && fault.StatusCode is not null)
        {
            failure = fault.StatusCode switch
            {
                HttpStatusCode.Forbidden => "The connection was not approved in SharpRail.",
                HttpStatusCode.TooManyRequests => "Please wait a moment before requesting approval again.",
                HttpStatusCode.RequestTimeout => "The approval request expired. Try again.",
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "This host does not offer app approval. Enter its session token.",
                _ => "The host could not approve this connection. Try again or enter a session token."
            };
        }
        catch (Exception fault) when (RemoteErrors.IsUnauthenticated(fault))
        {
            if (endpoint.LocalApproval) { token.Text = ""; approvedToken = null; failure = "Browser access expired or was revoked. Connect again to request approval in SharpRail."; }
            else failure = "The host rejected this session token.";
        }
        catch (Exception fault) when (RemoteErrors.IsTransport(fault) || fault is OperationCanceledException or IOException or HttpRequestException)
        {
            failure = "No host answered at " + uri.Authority + ". Check the address, and that the host is listening on a network this device can reach.";
        }
        catch (Exception fault) { Console.Error.WriteLine(fault); failure = fault.Message; }
        this.FindControl<TextBlock>("ConnectProgress")!.IsVisible = false;
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