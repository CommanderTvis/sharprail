using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.AirContext.UI;

/// <summary>
/// Air Context's two sections of the Code Intelligence pages. The account section (Settings) holds what is global to
/// the JetBrains login: terms and download, sign-in and sign-out. The workspace section (the language servers pane)
/// holds what belongs to one workspace: enablement, indexing and logs.
/// </summary>
internal sealed partial class AirContextPane : UserControl
{
    /// <summary>How often a pane waiting for a sign-in terminal probes the CLI; the checks shorten it.</summary>
    internal static TimeSpan SignInPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    private readonly IPluginUIContext context;
    private readonly bool account;
    private bool checking;
    private DateTime lastProbe;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly string workspace;
    private CancellationTokenSource? lifetime;
    private IDisposable? subscription;
    private ConsentWindow? consentWindow;
    private LogsWindow? logsWindow;
    private bool checkingConsent;
    private bool contextBusy;
    private ContextOutcome? contextFailure;

    internal AirContextPane(IPluginUIContext context, string workspace, bool account)
    {
        this.context = context; this.workspace = workspace; this.account = account;
        AvaloniaXamlLoader.Load(this);
        Name = account ? "AirContextAccount" : "AirContextPane";
        this.FindControl<StackPanel>("AccountPart")!.IsVisible = account;
        this.FindControl<StackPanel>("WorkspacePart")!.IsVisible = !account;
        this.FindControl<TextBlock>("ContextBlurb")!.Text = account
            ? "Semantic code discovery through the JetBrains Context CLI. The CLI and its JetBrains login are shared by every workspace."
            : "Semantic code discovery for this project. Cloud indexing requires explicit enablement, which covers every workspace of the project; unchecking it here disables the whole project.";
        BrandIcon();
        timer.Tick += async (_, _) => { if (IsEffectivelyVisible) await RefreshAsync(false); };
        AttachedToVisualTree += (_, _) =>
        {
            lifetime = new();
            Subscribe();
            timer.Start();
            _ = RefreshAsync(true);
        };
        GotFocus += (_, _) => { if (account && DateTime.UtcNow - lastProbe > TimeSpan.FromSeconds(15)) _ = CheckAccountAsync(); };
        DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop(); subscription?.Dispose(); subscription = null;
            consentWindow?.Close(); logsWindow?.Close();
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        };
    }

    // The account section probes the CLI only when asked, on opening and on focus; the workspace section reads the host's cached state.
    private async Task RefreshAsync(bool probe)
    {
        if (account) { await RefreshConsentAsync(); if (probe) await CheckAccountAsync(); }
        else await RefreshWorkspaceAsync();
    }

    /// <summary>Opens the "What is Air Context?" page in the platform's browser; the launcher call is asynchronous and never blocks the UI thread.</summary>
    internal static async Task OpenAboutAsync(Control from)
    {
        if (TopLevel.GetTopLevel(from) is { } top)
            try { await top.Launcher.LaunchUriAsync(new Uri(AirContextContract.AboutUrl)); } catch (Exception) { }
    }

    private async void ContextAboutClicked(object? sender, RoutedEventArgs e) => await OpenAboutAsync(this);
    private void ContextRefreshClicked(object? sender, RoutedEventArgs e) => _ = RefreshAsync(true);
    private void ContextOpenSettingsClicked(object? sender, RoutedEventArgs e) => context.OpenSettings(UiPages.CodeIntelligence);

    private void ApplyAccount(ContextOutcome result)
    {
        var state = this.FindControl<TextBlock>("AccountState")!;
        state.Text = result.SignedIn switch { true => "Signed in to JetBrains.", false => "Not signed in. Sign in to index and search.", _ => result.State is "requiresAcceptance" ? "Review the terms and download the CLI first." : "Sign-in status not checked yet." };
        state.Foreground = result.SignedIn == false ? Ui.Warning : Ui.Muted;
        this.FindControl<Button>("ContextLogin")!.IsVisible = result.SignedIn != true;
        this.FindControl<Button>("ContextLogin")!.IsEnabled = !contextBusy;
        this.FindControl<Button>("ContextLogout")!.IsVisible = result.SignedIn == true;
        this.FindControl<Button>("ContextLogout")!.IsEnabled = !contextBusy;
    }

    private async Task CheckAccountAsync()
    {
        if (checking || contextBusy || lifetime is not { } current) return;
        checking = true; lastProbe = DateTime.UtcNow;
        try
        {
            var result = await context.RequestAsync(AirContextContract.Workspace, new(workspace, "check"), current.Token);
            if (lifetime == current && !current.IsCancellationRequested) ApplyAccount(result);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("AccountState")!.Text = error.Message; }
        finally { checking = false; }
    }

    // After the sign-in terminal opens: probe at a modest rate until the CLI accepts the login or the tab closes.
    private async Task WatchSignInAsync(string tabKey, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; attempt < 60 && !token.IsCancellationRequested; attempt++)
            {
                await Task.Delay(SignInPollInterval, token);
                await CheckAccountAsync();
                if (this.FindControl<Button>("ContextLogout")!.IsVisible) return;
                if (context.Host().Terminals.GetValueOrDefault(workspace)?.All(tab => tab.TabKey != tabKey) != false) { await CheckAccountAsync(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async void ContextLogoutClicked(object? sender, RoutedEventArgs e) => await RunAsync("logout");

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
        if (account) { ApplyAccount(result); return; }
        this.FindControl<CheckBox>("ContextEnabled")!.IsChecked = result.Enabled;
        this.FindControl<StackPanel>("SignInNotice")!.IsVisible = result.Enabled && (result.SignedIn == false || result.State == "requiresLogin");
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
        this.FindControl<Button>("ContextIndex")!.IsVisible = result.Enabled;
        this.FindControl<Button>("ContextStop")!.IsVisible = result.Enabled;
        this.FindControl<Button>("ContextIndex")!.IsEnabled = result.Enabled && !contextBusy && result.State != "indexing" && result.SignedIn != false;
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
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>(account ? "AccountState" : "ContextState")!.Text = error.Message; }
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
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>(account ? "AccountState" : "ContextState")!.Text = error.Message; }
        finally { contextBusy = false; if (lifetime == current) await RefreshWorkspaceAsync(); }
    }

    private static async Task<bool> ConfirmAsync(Window owner, CancellationToken dismiss)
    {
        var window = DialogWindow.Create("Enable Air Context for this project?", 520);
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = "Indexing sends the source chunks, paths and revision metadata of this project's workspaces to JetBrains, including workspaces created later. SharpRail will index automatically when you launch a SharpRail agent. It uses the CLI's existing JetBrains login and index cache, but never installs global agent hooks or configuration and never stops an outside watcher. Disabling it in any workspace's pane disables the project.";
        text.IsVisible = true;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        var cancel = Ui.Button("Cancel", () => window.Close(false));
        actions.Children.Add(cancel);
        var confirm = Ui.Button("Enable for this project", () => window.Close(true));
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
            if (account) { if (operation != "login") ApplyAccount(result); }
            else ApplyWorkspace(result);
            if (operation == "login" && result.State == "login" && result.Command.Length > 0)
            {
                var tab = await context.OpenTerminalAsync(workspace, new TerminalOpenOptions { Command = result.Command, TabKey = "air-context-sign-in" });
                _ = WatchSignInAsync(tab, current.Token);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>(account ? "AccountState" : "ContextState")!.Text = error.Message; }
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