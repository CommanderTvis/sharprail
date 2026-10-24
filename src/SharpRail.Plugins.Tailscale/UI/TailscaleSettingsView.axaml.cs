using System.Windows.Input;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Tailscale.UI;

internal sealed class PairingCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

internal sealed record TailscaleApprovalRow(string Caption, string Code, ICommand Approve, ICommand Decline, ICommand Revoke);

internal sealed partial class TailscaleSettingsView : UserControl
{
    private readonly IPluginUIContext context;
    private IDisposable? subscription;
    private readonly TextBlock status;
    private readonly TextBlock name;
    private readonly ItemsControl pending;
    private readonly ItemsControl approved;
    private TailscaleStatus? previous;

    public TailscaleSettingsView(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        status = this.FindControl<TextBlock>("PairingStatus")!;
        name = this.FindControl<TextBlock>("PairingName")!;
        pending = this.FindControl<ItemsControl>("PairingPending")!;
        approved = this.FindControl<ItemsControl>("PairingApproved")!;
        poll.Tick += (_, _) => { if (previous is { } current && TailscaleContract.IsUnavailable(current)) Refresh(); };
        AttachedToVisualTree += (_, e) =>
        {
            subscription ??= context.Subscribe(TailscaleContract.Changed, Apply, new TailscaleStatusParams());
            if (TopLevel.GetTopLevel(this) is Window window) { activatedWindow = window; window.Activated += OnActivated; }
            poll.Start();
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            poll.Stop();
            refreshing?.Cancel();
            if (activatedWindow is not null) activatedWindow.Activated -= OnActivated;
            activatedWindow = null;
            subscription?.Dispose();
            subscription = null;
        };
    }

    private readonly DispatcherTimer poll = new() { Interval = PollInterval };
    private Window? activatedWindow;
    private CancellationTokenSource? refreshing;
    private DateTime lastRefresh;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActivationThrottle = TimeSpan.FromSeconds(3);

    private void OnActivated(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - lastRefresh >= ActivationThrottle) Refresh();
    }

    /// <summary>Asks the host to detect Tailscale again; the reply arrives on the status channel. A newer request supersedes an older one.</summary>
    private void Refresh()
    {
        lastRefresh = DateTime.UtcNow;
        refreshing?.Cancel();
        var current = refreshing = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = Task.Run(async () =>
        {
            try { var next = await context.RequestAsync(TailscaleContract.Refresh, new TailscaleStatusParams(), current.Token); Dispatcher.UIThread.Post(() => { if (!current.IsCancellationRequested && subscription is not null) Apply(next); }); }
            catch (Exception error) when (error is PluginCallException or OperationCanceledException) { }
        });
    }

    private TailscaleApprovalRow Row(TailscaleDevice device) => new(device.Name + " · " + device.User, device.Code,
        new PairingCommand(() => _ = DecideAsync(device.Id, true)), new PairingCommand(() => _ = DecideAsync(device.Id, false)),
        new PairingCommand(() => _ = RevokeAsync(device.Id)));

    private void Apply(TailscaleStatus next)
    {
        status.Text = next.Detail;
        name.Text = next.HostName is { } host ? "Host name: " + host : "";
        if (previous is null || !previous.Pending.SequenceEqual(next.Pending)) pending.ItemsSource = next.Pending.Select(Row).ToArray();
        if (previous is null || !previous.Approved.SequenceEqual(next.Approved)) approved.ItemsSource = next.Approved.Select(Row).ToArray();
        previous = next;
    }

    private async Task DecideAsync(string id, bool allow)
    {
        try { Apply(await DecideAsync(context, id, allow)); if (TailscaleContract.IsUnavailable(previous!)) Refresh(); }
        catch (PluginCallException error) { status.Text = error.Message; }
    }

    /// <summary>Answers a pending pairing request on the host; Settings and the incoming-request prompt both answer through it.</summary>
    internal static ValueTask<TailscaleStatus> DecideAsync(IPluginUIContext context, string id, bool allow) =>
        context.RequestAsync(TailscaleContract.Decide, new TailscaleDecision(id, allow));

    private async Task RevokeAsync(string id)
    {
        try { Apply(await context.RequestAsync(TailscaleContract.Revoke, new(id))); if (TailscaleContract.IsUnavailable(previous!)) Refresh(); }
        catch (PluginCallException error) { status.Text = error.Message; }
    }
}