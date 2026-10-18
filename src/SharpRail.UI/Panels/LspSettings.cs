using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

public sealed partial class SettingsWindow
{
    private Control LspSettings()
    {
        var panel = Page("LspPage");
        var provider = new LanguageProviderView(window.Host, window.WorkspaceRoot);
        PageControl<ContentControl>(panel, "JvmProviderSettings").Content = provider;
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
        var website = PageControl<Button>(panel, "LspProviderWebsite");
        var repository = PageControl<Button>(panel, "LspProviderRepository");
        var links = PageControl<WrapPanel>(panel, "LspProviderLinks");
        website.Click += LanguageProviderLinks.Open;
        repository.Click += LanguageProviderLinks.Open;
        var schemas = new Dictionary<string, JsonNode>();
        LanguageServerState[] catalog = [];
        void Load()
        {
            provider.UpdateProvider(state.Current.Settings.JavaKotlinProvider);
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
            links.IsVisible = false;
            _ = LoadLinks(id);
        }
        async Task LoadLinks(string id)
        {
            try
            {
                if (!schemas.TryGetValue(id, out var schema))
                {
                    var result = await window.Host.LanguageAsync(new(window.WorkspaceRoot, Operation: "configurationSchema", Server: id), lifetime.Token);
                    if (result.State != "ready" || result.Configuration.Length == 0) return;
                    schema = JsonNode.Parse(result.Configuration)!;
                    schemas[id] = schema;
                }
                if (!lifetime.IsCancellationRequested && selector.SelectedItem is string selected && selected == id)
                    LanguageProviderLinks.Apply(schema, website, repository, links);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception failure) { if (!lifetime.IsCancellationRequested && selector.SelectedItem is string selected && selected == id) message.Text = "Provider links unavailable: " + failure.Message; }
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
        if (panel is StackPanel body) body.Children.Add(new SharpRail.UI.Plugins.PluginSlot(window.Workbench.PluginRegistry, SharpRail.Plugins.Api.UI.UiSlots.CodeIntelligenceSettings, window.WorkspaceRoot));
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