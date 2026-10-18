using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.AirContext.UI;

/// <summary>One workspace's Air Context controls: the CLI's terms and download, cloud enablement, sign-in, indexing and logs.</summary>
internal sealed partial class AirContextPane : UserControl
{
    private readonly IPluginUIContext context;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string workspace;
    private CancellationTokenSource? lifetime;
    private IDisposable? subscription;
    private ConsentWindow? consentWindow;
    private LogsWindow? logsWindow;
    private bool checkingConsent;
    private bool contextBusy;
    private ContextOutcome? contextFailure;

    internal AirContextPane(IPluginUIContext context, string workspace)
    {
        this.context = context; this.workspace = workspace;
        AvaloniaXamlLoader.Load(this);
        Name = "AirContextPane";
        this.FindControl<TextBlock>("Workspace")!.Text = workspace;
        BrandIcon();
        timer.Tick += async (_, _) => { if (IsEffectivelyVisible) { await RefreshConsentAsync(); await RefreshWorkspaceAsync(); } };
        AttachedToVisualTree += (_, _) =>
        {
            lifetime = new();
            Subscribe();
            timer.Start();
            _ = RefreshConsentAsync(); _ = RefreshWorkspaceAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop(); subscription?.Dispose(); subscription = null;
            consentWindow?.Close(); logsWindow?.Close();
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        };
    }

    internal bool Retarget(string next)
    {
        workspace = next;
        this.FindControl<TextBlock>("Workspace")!.Text = next;
        contextFailure = null;
        if (lifetime is null) return true;
        subscription?.Dispose(); Subscribe();
        _ = RefreshWorkspaceAsync();
        return true;
    }

    // Enabling in another window or client reaches this pane through the plugin's channel.
    private void Subscribe() =>
        subscription = context.Subscribe(AirContextContract.Changed, outcome =>
        {
            if (outcome.WorkspaceId == workspace && !contextBusy) ApplyWorkspace(outcome);
        }, new ContextCall(workspace));

    private async void BrandIcon()
    {
        try
        {
            var svg = await context.ReadAssetAsync("air.svg");
            this.FindControl<ContentControl>("ContextBrand")!.Content = new SvgAsset(svg, 24);
        }
        catch (Exception error) when (error is IOException or PluginCallException or InvalidOperationException) { }
    }

    private void ApplyConsent(ContextConsentResult result)
    {
        var button = this.FindControl<Button>("ContextSetup")!;
        button.Content = result.State == "downloaded" ? "View installation…" : result.State == "repair" ? "Repair Context CLI…" : "Review terms and download…";
        ToolTip.SetTip(button, result.Detail);
        button.IsEnabled = consentWindow is null;
    }

    private async Task RefreshConsentAsync()
    {
        if (checkingConsent || lifetime is not { } current || consentWindow is not null) return;
        checkingConsent = true;
        try
        {
            var result = await context.RequestAsync(AirContextContract.Consent, new("status"), current.Token);
            if (lifetime == current && !current.IsCancellationRequested && consentWindow is null) ApplyConsent(result);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) ToolTip.SetTip(this.FindControl<Button>("ContextSetup")!, error.Message); }
        finally { checkingConsent = false; }
    }

    private void ContextSetupClicked(object? sender, RoutedEventArgs e)
    {
        if (consentWindow is not null || TopLevel.GetTopLevel(this) is not Window owner) return;
        consentWindow = new ConsentWindow(context);
        consentWindow.Updated += result =>
        {
            ApplyConsent(result);
            if (result.State == "downloaded" && contextFailure?.State == "requiresAcceptance") contextFailure = null;
        };
        this.FindControl<Button>("ContextSetup")!.IsEnabled = false;
        consentWindow.Closed += async (_, _) =>
        {
            consentWindow = null;
            this.FindControl<Button>("ContextSetup")!.IsEnabled = true;
            await RefreshConsentAsync();
            await RefreshWorkspaceAsync();
        };
        consentWindow.Show(owner);
    }

    private void ApplyWorkspace(ContextOutcome result)
    {
        this.FindControl<CheckBox>("ContextEnabled")!.IsChecked = result.Enabled;
        var label = result.State switch
        {
            "requiresLogin" => "Sign in required",
            "requiresAcceptance" => "Review terms to continue",
            "indexing" => "Indexing",
            "ready" => "Ready",
            "idle" => "Ready to index",
            "stopped" => "Stopped",
            "disabled" => "Not enabled",
            "login" => "Sign in",
            "error" => "Something went wrong",
            _ => "Unavailable"
        };
        var state = this.FindControl<TextBlock>("ContextState")!;
        state.Text = result.Detail.Length > 0 ? label + " · " + result.Detail : label;
        state.Foreground = result.State is "error" or "requiresAcceptance" ? Ui.Danger : result.State == "requiresLogin" ? Ui.Warning : Ui.Muted;
        state.IsVisible = result.Enabled || result.State is not "disabled";
        this.FindControl<WrapPanel>("ContextControls")!.IsVisible = result.Enabled;
        this.FindControl<Button>("ContextLogin")!.IsEnabled = result.Enabled && !contextBusy;
        this.FindControl<Button>("ContextIndex")!.IsEnabled = result.Enabled && !contextBusy && result.State != "indexing";
        this.FindControl<Button>("ContextStop")!.IsEnabled = result.Enabled && !contextBusy;
        this.FindControl<CheckBox>("ContextEnabled")!.IsEnabled = !contextBusy;
    }

    private async Task RefreshWorkspaceAsync()
    {
        if (contextBusy || lifetime is not { } current) return;
        var target = workspace;
        try
        {
            var result = await context.RequestAsync(AirContextContract.Workspace, new(target), current.Token);
            if (lifetime == current && !current.IsCancellationRequested && !contextBusy && target == workspace)
                ApplyWorkspace(contextFailure is { } failure && result.State is "disabled" or "idle"
                    ? failure with { Enabled = result.Enabled } : result);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("ContextState")!.Text = error.Message; }
    }

    private async void ContextEnableClicked(object? sender, RoutedEventArgs e)
    {
        if (contextBusy || lifetime is not { } current || TopLevel.GetTopLevel(this) is not Window owner) return;
        var enable = this.FindControl<CheckBox>("ContextEnabled")!.IsChecked == true;
        contextBusy = true;
        try
        {
            if (enable && !await ConfirmAsync(owner, current.Token))
            { this.FindControl<CheckBox>("ContextEnabled")!.IsChecked = false; return; }
            var result = await context.RequestAsync(AirContextContract.Workspace, new(workspace, enable ? "enable" : "disable"), current.Token);
            if (lifetime != current || current.IsCancellationRequested) return;
            contextFailure = result.State is "error" or "requiresAcceptance" or "unavailable" ? result : null;
            ApplyWorkspace(result);
            if (result.State == "requiresAcceptance") ContextSetupClicked(sender, e);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("ContextState")!.Text = error.Message; }
        finally { contextBusy = false; if (lifetime == current) await RefreshWorkspaceAsync(); }
    }

    private static async Task<bool> ConfirmAsync(Window owner, CancellationToken dismiss)
    {
        var window = DialogWindow.Create("Enable Air Context for this workspace?", 520);
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = "Indexing sends this workspace's source chunks, paths and revision metadata to JetBrains. SharpRail will index automatically when you launch a SharpRail agent. It uses the CLI's existing JetBrains login and index cache, but never installs global agent hooks or configuration and never stops an outside watcher. New workspaces require their own enablement.";
        text.IsVisible = true;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        var cancel = Ui.Button("Cancel", () => window.Close(false));
        actions.Children.Add(cancel);
        var confirm = Ui.Button("Enable for this workspace", () => window.Close(true));
        confirm.Classes.Add("primary");
        confirm.Name = "EnableContextConfirm";
        actions.Children.Add(confirm);
        window.Opened += (_, _) => cancel.Focus();
        using var dismissal = dismiss.Register(() => window.Close(false));
        return await window.ShowDialog<bool>(owner);
    }

    private async Task RunAsync(string operation)
    {
        if (contextBusy || lifetime is not { } current) return;
        contextBusy = true;
        try
        {
            var result = await context.RequestAsync(AirContextContract.Workspace, new(workspace, operation), current.Token);
            if (lifetime != current || current.IsCancellationRequested) return;
            contextFailure = result.State is "error" or "requiresAcceptance" or "unavailable" ? result : null;
            ApplyWorkspace(result);
            if (operation == "login" && result.State == "login" && result.Command.Length > 0)
                await context.OpenTerminalAsync(workspace, new TerminalOpenOptions { Command = result.Command, TabKey = "air-context-sign-in" });
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("ContextState")!.Text = error.Message; }
        finally { contextBusy = false; if (lifetime == current) await RefreshWorkspaceAsync(); }
    }

    private async void ContextLoginClicked(object? sender, RoutedEventArgs e) => await RunAsync("login");
    private async void ContextIndexClicked(object? sender, RoutedEventArgs e) => await RunAsync("index");
    private async void ContextStopClicked(object? sender, RoutedEventArgs e) => await RunAsync("stop");

    private void ContextLogsClicked(object? sender, RoutedEventArgs e)
    {
        if (logsWindow is { } existing) { existing.Activate(); return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        logsWindow = new(context, workspace);
        logsWindow.Closed += (_, _) => logsWindow = null;
        logsWindow.Show(owner);
    }
}