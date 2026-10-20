using System.Windows.Input;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
        AttachedToVisualTree += (_, _) => subscription ??= context.Subscribe(TailscaleContract.Changed, Apply, new TailscaleStatusParams());
        DetachedFromVisualTree += (_, _) => { subscription?.Dispose(); subscription = null; };
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
        try { Apply(await context.RequestAsync(TailscaleContract.Decide, new(id, allow))); }
        catch (PluginCallException error) { status.Text = error.Message; }
    }

    private async Task RevokeAsync(string id)
    {
        try { Apply(await context.RequestAsync(TailscaleContract.Revoke, new(id))); }
        catch (PluginCallException error) { status.Text = error.Message; }
    }
}