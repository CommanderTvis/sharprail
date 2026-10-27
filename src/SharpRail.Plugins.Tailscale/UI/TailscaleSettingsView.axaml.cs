using System.Windows.Input;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

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
    private readonly TextBlock pendingHeading;
    private readonly TextBlock approvedHeading;
    private TailscaleStatus? previous;

    public TailscaleSettingsView(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        status = this.FindControl<TextBlock>("PairingStatus")!;
        name = this.FindControl<TextBlock>("PairingName")!;
        pending = this.FindControl<ItemsControl>("PairingPending")!;
        approved = this.FindControl<ItemsControl>("PairingApproved")!;
        pendingHeading = this.FindControl<TextBlock>("PairingPendingHeading")!;
        approvedHeading = this.FindControl<TextBlock>("PairingApprovedHeading")!;
        approved.ItemTemplate = new FuncDataTemplate<TailscaleApprovalRow>((row, _) => RememberedRow(row!));
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

    /// <summary>One dense line per remembered device: the text on the left, a small cross to revoke on the right.</summary>
    private static Control RememberedRow(TailscaleApprovalRow row)
    {
        var text = new TextBlock { Text = row.Caption, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(text, row.Caption);
        var revoke = new Button
        {
            Name = "RevokeAccess",
            Content = Ui.Icon("close", Ui.Muted, 14),
            Padding = new Thickness(7),
            MinWidth = 0,
            MinHeight = 0,
            Width = 28,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Command = row.Revoke
        };
        ToolTip.SetTip(revoke, "Revoke access");
        AutomationProperties.SetName(revoke, "Revoke access");
        revoke.Click += (_, _) => revoke.IsEnabled = false;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(revoke, 1);
        grid.Children.Add(text);
        grid.Children.Add(revoke);
        return new Border { Padding = new Thickness(0, 2), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.BorderBrush, Child = grid };
    }

    private void Apply(TailscaleStatus next)
    {
        status.Text = next.Detail;
        name.Text = next.HostName is { } host ? "Host name: " + host : "";
        if (previous is null || !previous.Pending.SequenceEqual(next.Pending)) pending.ItemsSource = next.Pending.Select(Row).ToArray();
        if (previous is null || !previous.Approved.SequenceEqual(next.Approved)) approved.ItemsSource = next.Approved.Select(Row).ToArray();
        previous = next;
        pendingHeading.IsVisible = pending.IsVisible = next.Pending.Count > 0;
        approvedHeading.IsVisible = approved.IsVisible = next.Approved.Count > 0;
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
        catch (PluginCallException error)
        {
            status.Text = error.Message;
            if (previous is not null) approved.ItemsSource = previous.Approved.Select(Row).ToArray();
        }
    }
}