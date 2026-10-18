using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguagePanel : UserControl
{
    private sealed record ServerRow(LanguageServerState Server, LanguageConfigurationView ConfigurationView, bool Expanded = false)
    {
        public bool IsBusy => Server.State is "installing" or "initializing";
        public bool ShowStatusIcon => !IsBusy;
        public bool ShowDetail => Expanded || Server.State is "error" or "unavailable" or "installing" or "initializing" or "actionRequired" or "requiresAcceptance" or "expired";
        public bool ShowLogs => Expanded || Server.State is "error" or "unavailable" or "expired";
        public bool HasSource => Server.Path.Length > 0;
        public bool RestartRequired => Server.ActiveConfiguration.Length > 0 && Server.Configuration != Server.ActiveConfiguration;
        public string SettingsTooltip => RestartRequired ? "Workspace settings · restart needed" : "Workspace settings";
        public Control SettingsIcon => Ui.Icon("settings", RestartRequired ? Ui.Accent : Ui.Muted);
        public bool CanStart => Server.State is "idle" or "stopped" or "error" or "unavailable" or "requiresAcceptance";
        public string Disclosure => CanStart ? Server.State is "error" or "unavailable" ? "Retry ▷" : "Start ▷" : Expanded ? "▾" : "▸";
        public Avalonia.Media.IBrush DetailBrush => Server.State is "error" or "unavailable" ? Ui.Danger : Ui.Muted;
        public string Detection => Server.Path.Length > 0 ? "Suggested by file: " + Server.Path : "No matching files detected.";
        public string Version => "Managed version: " + Server.Version;
        public Control Icon => Ui.Icon(Server.State switch
        {
            "ready" => "check",
            "installing" => "loader",
            "initializing" => "refresh",
            "stopped" or "disabled" => "close",
            "error" or "unavailable" or "actionRequired" or "requiresAcceptance" or "expired" => "alertWarning",
            _ => "checkboxBlankCircle"
        }, Server.State == "ready" ? Ui.Success : Server.State == "error" ? Ui.Danger : Ui.Muted);
        public string StatusDescription => Server.State switch
        {
            "requiresAcceptance" => "Accept EULA",
            "actionRequired" => "Needs configuration",
            _ => Server.State
        };
        public override string ToString() => LanguageServerNameConverter.Display(Server.Id);
    }
    private readonly IProjectServices host;
    private readonly string workspace;
    private readonly Func<LanguageItem, Task> navigate;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? lifetime;
    private bool busy;
    private IReadOnlyList<LanguageServerState> inventory = [];
    private readonly Dictionary<string, LanguageConfigurationView> configurations = [];
    private readonly HashSet<string> added = [];
    private readonly HashSet<string> expanded = [];

    internal LanguagePanel(IProjectServices host, string workspace, Func<LanguageItem, Task> navigate, SharpRail.UI.State.SharedState? state = null)
    {
        this.host = host; this.workspace = workspace; this.navigate = navigate;
        AvaloniaXamlLoader.Load(this);
        Name = "LspPanel";
        this.FindControl<TextBlock>("Workspace")!.Text = workspace;
        var servers = this.FindControl<ListBox>("Servers")!;
        servers.ContextRequested += ServerContextRequested;
        servers.Tapped += async (_, e) =>
        {
            var source = e.Source as Avalonia.Visual;
            if (source is Button || source?.FindAncestorOfType<Button>() is not null || source is LanguageConfigurationView || source?.FindAncestorOfType<LanguageConfigurationView>() is not null) return;
            var container = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
            if (container?.Content is ServerRow row) await ActivateAsync(row);
        };
        servers.KeyDown += async (_, e) =>
        {
            if (e.Source is Avalonia.Visual source && (source is Button || source.FindAncestorOfType<Button>() is not null || source is LanguageConfigurationView || source.FindAncestorOfType<LanguageConfigurationView>() is not null)) return;
            if (e.Key is Key.Enter or Key.Space && servers.SelectedItem is ServerRow row)
            { e.Handled = true; await ActivateAsync(row); }
        };
        var add = this.FindControl<ComboBox>("AddServer")!;
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedItem is not string id) return;
            added.Add(id);
            RefreshRows(id);
            add.SelectedIndex = -1;
        };
        this.FindControl<Button>("Refresh")!.Click += async (_, _) => await RunAsync("servers");
        timer.Tick += async (_, _) => { if (IsEffectivelyVisible) { await RunAsync("servers"); } };
        void ConfigurationChanged(HostState previous, HostState next)
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ConfigurationChanged(previous, next)); return; }
            var saved = next.WorkspaceLanguages.GetValueOrDefault(workspace) ?? "{}";
            if (saved == (previous.WorkspaceLanguages.GetValueOrDefault(workspace) ?? "{}")) return;
            var values = System.Text.Json.Nodes.JsonNode.Parse(saved);
            foreach (var (id, view) in configurations)
                view.UpdateSavedConfiguration(values?[id]?.ToJsonString() ?? "{}");
            _ = RunAsync("servers");
        }
        AttachedToVisualTree += (_, _) => { lifetime = new(); if (state is not null) state.Changed += ConfigurationChanged; timer.Start(); _ = RunAsync("servers"); };
        DetachedFromVisualTree += (_, _) => { if (state is not null) state.Changed -= ConfigurationChanged; foreach (var view in configurations.Values) view.SettingsWindow?.Close(); timer.Stop(); lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    private void SetStatus(string text)
    {
        var status = this.FindControl<TextBlock>("Status")!;
        status.Text = text;
        status.IsVisible = text.Length > 0;
    }

    private async void SourceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ServerRow row } || !row.HasSource) return;
        try { await navigate(new(row.Server.Path, 0, 0, 0, 0)); }
        catch (Exception error) { SetStatus(error.Message); }
    }

    private void LogsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerRow row }) ShowLogs(row);
        e.Handled = true;
    }

    private void ShowLogs(ServerRow row)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            new LanguageLogsWindow(host, workspace, row.Server.Id).Show(owner);
    }

    private async void SettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerRow row }) await row.ConfigurationView.ShowSettingsAsync((Button)sender);
        e.Handled = true;
    }

    private async Task ActivateAsync(ServerRow row)
    {
        if (row.CanStart)
        {
            await RunAsync("retry", row.Server);
            await RunAsync("servers");
        }
        else Toggle(row);
    }

    private void Toggle(ServerRow row)
    {
        if (!expanded.Add(row.Server.Id)) expanded.Remove(row.Server.Id);
        RefreshRows(row.Server.Id);
    }

    private void RefreshRows(string? selectedId = null)
    {
        var list = this.FindControl<ListBox>("Servers")!;
        selectedId ??= (list.SelectedItem as ServerRow)?.Server.Id;
        var rows = inventory.Where(server => server.Path.Length > 0 || added.Contains(server.Id) || server.Id != "intellij" && server.State is not ("idle" or "disabled") || server.Configured)
            .Select(server =>
            {
                if (!configurations.TryGetValue(server.Id, out var view))
                    configurations[server.Id] = view = new(host, workspace, server);
                else view.Update(server);
                return new ServerRow(server, view, expanded.Contains(server.Id));
            }).ToArray();
        if (!(list.ItemsSource?.Cast<ServerRow>() ?? []).SequenceEqual(rows))
        {
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(row => row.Server.Id == selectedId);
        }
        var add = this.FindControl<ComboBox>("AddServer")!;
        var choices = inventory.Where(server => !rows.Any(row => row.Server.Id == server.Id)).Select(server => server.Id).ToArray();
        if (!(add.ItemsSource?.Cast<string>() ?? []).SequenceEqual(choices)) add.ItemsSource = choices;
        add.IsEnabled = choices.Length > 0;
    }

    private void ServerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var list = this.FindControl<ListBox>("Servers")!;
        var source = e.Source as Avalonia.Visual;
        var container = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
        if (container is null && !e.TryGetPosition(list, out _))
            container = list.ContainerFromIndex(list.SelectedIndex) as ListBoxItem;
        if (container?.Content is not ServerRow row) return;
        list.SelectedItem = row;
        var start = new MenuItem { Header = "Start / Retry", IsEnabled = !busy };
        var stop = new MenuItem { Header = "Stop", IsEnabled = !busy && row.Server.State is not ("idle" or "stopped" or "disabled") };
        start.Click += async (_, _) => await RunAsync("retry", row.Server);
        stop.Click += async (_, _) => await RunAsync("stop", row.Server);
        var logs = new MenuItem { Header = "View logs" };
        logs.Click += (_, _) => ShowLogs(row);
        var menu = new ContextMenu { ItemsSource = new[] { start, stop, logs } };
        menu.Closed += (_, _) => container.ContextMenu = null;
        container.ContextMenu = menu;
        menu.Open(container);
        e.Handled = true;
    }

    private async Task RunAsync(string operation, LanguageServerState? target = null, bool acceptanceReviewed = false)
    {
        if (busy || lifetime is not { } current) return;
        var selected = (this.FindControl<ListBox>("Servers")!.SelectedItem as ServerRow)?.Server;
        if (operation is "retry" or "stop" && (target ?? selected) is null) return;
        busy = true;
        this.FindControl<Button>("Refresh")!.IsEnabled = false;
        try
        {
            var token = current.Token;
            if (operation == "retry" && !acceptanceReviewed && (target ?? selected) is { Id: "intellij" } intellij)
            {
                var saved = await host.LanguageAsync(new(workspace, Operation: "javaKotlinProvider"), token);
                if (current != lifetime || token.IsCancellationRequested) return;
                if (saved.Configuration != "intellij")
                {
                    SetStatus("Select IntelliJ in Settings → Code Intelligence → Java / Kotlin provider, then choose Apply.");
                    return;
                }
                var acknowledgement = await host.IntelliJTermsAsync(new(workspace), token);
                if (current != lifetime || token.IsCancellationRequested) return;
                if (!acknowledgement.Accepted)
                {
                    if (TopLevel.GetTopLevel(this) is Window owner)
                    {
                        var terms = new IntelliJTermsWindow(host, workspace, startAfterAcceptance: true);
                        terms.Closed += async (_, _) => { if (terms.StartRequested) await RunAsync("retry", intellij, acceptanceReviewed: true); };
                        terms.Show(owner);
                    }
                    return;
                }
            }
            var request = new LanguageRequest(workspace, Operation: operation, Server: operation is "retry" or "stop" ? (target ?? selected)!.Id : "");
            var result = await Task.Run(async () => await host.LanguageAsync(request, token), token);
            if (current != lifetime || token.IsCancellationRequested) return;
            SetStatus(result.State + ": " + result.Detail);
            if (operation == "servers")
            {
                inventory = result.Servers;
                RefreshRows(selected?.Id);
                if (result.State == "ready") SetStatus("");
            }
        }
        catch (OperationCanceledException) when (current != lifetime || current.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (current == lifetime) SetStatus(error.Message);
        }
        finally
        {
            busy = false;
            this.FindControl<Button>("Refresh")!.IsEnabled = true;
        }
    }
}