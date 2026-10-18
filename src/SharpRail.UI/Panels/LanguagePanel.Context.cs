using Avalonia.Controls;
using Avalonia.Interactivity;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguagePanel
{
    private readonly Func<string, Task>? contextLogin;
    private bool contextBusy;
    private ContextResult? contextFailure;
    private LanguageLogsWindow? contextLogs;

    private void ApplyContextWorkspace(ContextResult result)
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
        this.FindControl<TextBlock>("ContextState")!.Text = result.Detail.Length > 0 ? label + " · " + result.Detail : label;
        this.FindControl<TextBlock>("ContextState")!.Foreground = result.State is "error" or "requiresAcceptance" ? Ui.Danger : result.State == "requiresLogin" ? Ui.Warning : Ui.Muted;
        this.FindControl<TextBlock>("ContextState")!.IsVisible = result.Enabled || result.State is not "disabled";
        this.FindControl<WrapPanel>("ContextControls")!.IsVisible = result.Enabled;
        this.FindControl<Button>("ContextLogin")!.IsEnabled = result.Enabled && !contextBusy && contextLogin is not null;
        this.FindControl<Button>("ContextIndex")!.IsEnabled = result.Enabled && !contextBusy && result.State != "indexing";
        this.FindControl<Button>("ContextStop")!.IsEnabled = result.Enabled && !contextBusy;
        this.FindControl<CheckBox>("ContextEnabled")!.IsEnabled = !contextBusy;
    }

    private async Task RefreshContextWorkspaceAsync()
    {
        if (contextBusy || lifetime is not { } current) return;
        try
        {
            var result = await host.ContextAsync(new(workspace), current.Token);
            if (lifetime == current && !current.IsCancellationRequested && !contextBusy)
                ApplyContextWorkspace(contextFailure is { } failure && result.State is "disabled" or "idle"
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
            if (enable && !await Dialogs.Confirm(owner, "Enable Air Context for this workspace?",
                "Indexing sends this workspace's source chunks, paths and revision metadata to JetBrains. SharpRail will index automatically when you launch a SharpRail agent. It uses the CLI's existing JetBrains login and index cache, but never installs global agent hooks or configuration and never stops an outside watcher. New workspaces require their own enablement.",
                "Enable for this workspace", "EnableContextConfirm", current.Token))
            { this.FindControl<CheckBox>("ContextEnabled")!.IsChecked = false; return; }
            var result = await host.ContextAsync(new(workspace, enable ? "enable" : "disable"), current.Token);
            if (lifetime != current || current.IsCancellationRequested) return;
            contextFailure = result.State is "error" or "requiresAcceptance" or "unavailable" ? result : null;
            ApplyContextWorkspace(result);
            if (result.State == "requiresAcceptance") ContextSetupClicked(sender, e);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("ContextState")!.Text = error.Message; }
        finally { contextBusy = false; if (lifetime == current) await RefreshContextWorkspaceAsync(); }
    }

    private async Task RunContextAsync(string operation)
    {
        if (contextBusy || lifetime is not { } current) return;
        contextBusy = true;
        try
        {
            var result = await host.ContextAsync(new(workspace, operation), current.Token);
            if (lifetime != current || current.IsCancellationRequested) return;
            contextFailure = result.State is "error" or "requiresAcceptance" or "unavailable" ? result : null;
            ApplyContextWorkspace(result);
            if (operation == "login" && result.State == "login" && contextLogin is { } open && result.Command.Length > 0)
                await open(result.Command);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("ContextState")!.Text = error.Message; }
        finally { contextBusy = false; if (lifetime == current) await RefreshContextWorkspaceAsync(); }
    }

    private async void ContextLoginClicked(object? sender, RoutedEventArgs e) => await RunContextAsync("login");
    private async void ContextIndexClicked(object? sender, RoutedEventArgs e) => await RunContextAsync("index");
    private async void ContextStopClicked(object? sender, RoutedEventArgs e) => await RunContextAsync("stop");
    private void ContextLogsClicked(object? sender, RoutedEventArgs e)
    {
        if (contextLogs is { } existing) { existing.Activate(); return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        contextLogs = new(host, workspace, "context", context: true);
        contextLogs.Closed += (_, _) => contextLogs = null;
        contextLogs.Show(owner);
    }
}