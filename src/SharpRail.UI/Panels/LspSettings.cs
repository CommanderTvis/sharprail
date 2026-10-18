using System.Text.Json;

using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

public sealed partial class SettingsWindow
{
    private Control LspSettings()
    {
        var panel = Page("LspPage");
        PageControl<ContentControl>(panel, "JvmProviderSettings").Content = new LanguageProviderView(window.Host, window.WorkspaceRoot);
        var showContext = PageControl<CheckBox>(panel, "ShowAirContext");
        showContext.IsChecked = !state.Current.Settings.AirContextHidden;
        showContext.Click += async (_, _) =>
        {
            showContext.IsEnabled = false;
            try { await state.ChangeAsync(HostStateChange.Setting("air-context-hidden", (showContext.IsChecked != true).ToString())); }
            catch (Exception failure) { PageControl<TextBlock>(panel, "LspMessage").Text = failure.Message; }
            finally { showContext.IsChecked = !state.Current.Settings.AirContextHidden; showContext.IsEnabled = true; }
        };
        var selector = PageControl<ListBox>(panel, "LspServer");
        var enabled = PageControl<CheckBox>(panel, "LspEnabled");
        var managed = PageControl<CheckBox>(panel, "LspManaged");
        var command = PageControl<TextBox>(panel, "LspCommand");
        var defaultArguments = PageControl<CheckBox>(panel, "LspDefaultArguments");
        var arguments = PageControl<TextBox>(panel, "LspArguments");
        var message = PageControl<TextBlock>(panel, "LspMessage");
        var save = PageControl<Button>(panel, "LspSave");
        var feature = PageControl<CheckBox>(panel, "LspFeatureEnabled");
        feature.IsChecked = state.Current.Settings.LspEnabled;
        feature.Click += async (_, _) =>
        {
            var value = feature.IsChecked == true;
            feature.IsEnabled = false;
            try { await Task.Run(async () => await state.ChangeAsync(HostStateChange.Setting("lsp-enabled", value.ToString())), lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception failure) { message.Foreground = Ui.Danger; message.Text = failure.Message; }
            finally { feature.IsChecked = state.Current.Settings.LspEnabled; feature.IsEnabled = true; }
        };
        LanguageServerState[] catalog = [];
        void Load()
        {
            showContext.IsChecked = !state.Current.Settings.AirContextHidden;
            feature.IsChecked = state.Current.Settings.LspEnabled;
            if (selector.SelectedItem is not string id) return;
            PageControl<TextBlock>(panel, "LspServerHeading").Text = id;
            var json = state.Current.Settings.LanguageServers;
            var options = string.IsNullOrWhiteSpace(json) ? new() :
                JsonSerializer.Deserialize<Dictionary<string, LanguageServerOptions>>(json, JsonSerializerOptions.Web)?.GetValueOrDefault(id) ?? new LanguageServerOptions();
            enabled.IsChecked = !options.Disabled; managed.IsChecked = options.Managed;
            defaultArguments.IsChecked = options.Arguments is null;
            arguments.IsEnabled = options.Arguments is not null;
            command.Text = options.Command; arguments.Text = string.Join('\n', options.Arguments ?? []);
            var server = catalog.First(server => server.Id == id);
            PageControl<TextBlock>(panel, "LspImplementation").Text = server.Detail.Length > 0 ? server.Detail : id;
            PageControl<TextBlock>(panel, "LspVersion").Text = server.Version == "toolchain"
                ? "Version: supplied by the host SDK" : "Pinned managed version: " + server.Version;
            message.Text = "";
        }
        void ArgumentControls()
        {
            defaultArguments.IsEnabled = !string.IsNullOrWhiteSpace(command.Text);
            arguments.IsEnabled = defaultArguments.IsEnabled && defaultArguments.IsChecked != true;
        }
        defaultArguments.IsCheckedChanged += (_, _) => ArgumentControls();
        command.TextChanged += (_, _) => ArgumentControls();
        selector.SelectionChanged += (_, _) => Load();
        refreshers.Add(Load);
        save.IsEnabled = false;
        save.Click += async (_, _) =>
        {
            if (selector.SelectedItem is not string id) return;
            var args = (arguments.Text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var options = new LanguageServerOptions(enabled.IsChecked != true, managed.IsChecked == true, (command.Text ?? "").Trim(), string.IsNullOrWhiteSpace(command.Text) || defaultArguments.IsChecked == true ? null : args);
            save.IsEnabled = false;
            try
            {
                var json = JsonSerializer.Serialize(options, JsonSerializerOptions.Web);
                await Task.Run(async () => await state.ChangeAsync(HostStateChange.Setting("language-server:" + id, json)), lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                message.Foreground = Ui.Muted; message.Text = "Saved. Applies on the next Start / Retry.";
            }
            catch (Exception failure) when (!lifetime.IsCancellationRequested) { message.Foreground = Ui.Danger; message.Text = failure.Message; }
            catch (OperationCanceledException) { }
            finally { save.IsEnabled = true; }
        };
        _ = LoadCatalog();
        return panel;

        async Task LoadCatalog()
        {
            try
            {
                var workspace = window.WorkspaceRoot;
                var result = await Task.Run(async () => await window.Host.LanguageAsync(new(workspace, Operation: "catalog"), lifetime.Token), lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                catalog = result.Servers.OrderBy(server => server.Id, StringComparer.Ordinal).ToArray();
                selector.ItemsSource = catalog.Select(server => server.Id).ToArray();
                selector.SelectedIndex = catalog.Length > 0 ? 0 : -1;
                PageControl<TextBlock>(panel, "LspStorage").Text = result.Detail;
                save.IsEnabled = catalog.Length > 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) { message.Foreground = Ui.Danger; message.Text = failure.Message; }
        }
    }
}