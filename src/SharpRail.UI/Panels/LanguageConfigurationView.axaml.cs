using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguageConfigurationView : UserControl
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IProjectServices host;
    private readonly string workspace;
    private LanguageServerState server;
    private CancellationTokenSource? lifetime;
    private bool loading;
    private bool dirty;
    private bool saving;
    private readonly Dictionary<string, string?> loadedText = [];
    private bool? loadedAutoStart;
    private bool? loadedDefaultArguments;

    internal LanguageConfigurationView(IProjectServices host, string workspace, LanguageServerState server)
    {
        this.host = host; this.workspace = workspace; this.server = server;
        AvaloniaXamlLoader.Load(this);
        Find<TextBlock>("ConfigurationScope").Text = "This workspace only";
        ToolTip.SetTip(Find<TextBlock>("ConfigurationScope"), workspace);
        Find<StackPanel>("IntelliJOptions").IsVisible = server.Id == "intellij";
        Find<Button>("ReviewIntelliJTerms").Click += async (_, _) =>
        {
            await SaveAsync(true, forceReview: true);
        };
        Find<CheckBox>("AutoStart").IsEnabled = true;
        Find<Button>("RestartConfiguration").IsEnabled = true;
        foreach (var name in new[] { "IntelliJInstallation", "IntelliJProjectJdk", "IntelliJBuildTool" })
            Find<TextBox>(name).TextChanged += (_, _) => { if (!loading) dirty = true; };
        Find<StackPanel>("ClangOptions").IsVisible = server.Id == "clangd";
        foreach (var name in new[] { "Arguments", "IncludePaths", "CompilerFlags", "CompilationDatabase", "InitializationOptions", "ServerSettings" })
            Find<TextBox>(name).TextChanged += (_, _) => { if (!loading) dirty = true; };
        Find<CheckBox>("AutoStart").IsCheckedChanged += (_, _) => { if (!loading) dirty = true; };
        Find<CheckBox>("DefaultArguments").IsCheckedChanged += (_, _) =>
        {
            Find<TextBox>("Arguments").IsEnabled = Find<CheckBox>("DefaultArguments").IsChecked != true;
            if (!loading) dirty = true;
        };
        Find<Button>("CloseConfiguration").Click += (_, _) => SettingsWindow?.Close();
        Find<Button>("ReloadConfiguration").Click += async (_, _) => await RefreshSavedAsync(true);
        Find<Button>("SaveConfiguration").Click += async (_, _) => await SaveAsync(false);
        Find<Button>("RestartConfiguration").Click += async (_, _) => await SaveAsync(true);
        AttachedToVisualTree += (_, _) => lifetime = new();
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
        Load();
        AttachedToVisualTree += async (_, _) => await LoadFieldsAsync();
        foreach (var name in new[] { "InitializationOptions", "ServerSettings" })
            Find<TextBox>(name).TextChanged += (_, _) => { if (!loading) RefreshFields(); };
    }

    internal LanguageSettingsWindow? SettingsWindow { get; private set; }

    internal async Task ShowSettingsAsync(Control target)
    {
        if (SettingsWindow is { } current) { current.Activate(); return; }
        Find<Expander>("ConfigurationExpander").IsExpanded = true;
        var window = new LanguageSettingsWindow(this, server.Id, workspace);
        SettingsWindow = window;
        window.Closed += (_, _) => SettingsWindow = null;
        if (TopLevel.GetTopLevel(target) is Window owner) window.Show(owner);
        await RefreshSavedAsync(false);
    }

    private async Task RefreshSavedAsync(bool discardDraft)
    {
        if (lifetime is not { } current) return;
        try
        {
            var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "configuration", Server: server.Id), current.Token), current.Token);
            if (lifetime != current) return;
            if (result.State != "ready") { Find<TextBlock>("ConfigurationMessage").Text = result.Detail; return; }
            var next = server with { Configuration = result.Configuration, ActiveConfiguration = result.ActiveConfiguration };
            if (discardDraft) { server = next; Load(); }
            else Update(next);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = error.Message; }
    }

    internal void UpdateSavedConfiguration(string configuration) => Update(server with { Configuration = configuration });

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)!;
    internal void Update(LanguageServerState next)
    {
        if (next == server) { if (!dirty && !saving) ShowState(); return; }
        dirty = invalidFields.Count > 0 || loadedText.Any(item => Find<TextBox>(item.Key).Text != item.Value) ||
            Find<CheckBox>("AutoStart").IsChecked != loadedAutoStart ||
            Find<CheckBox>("DefaultArguments").IsChecked != loadedDefaultArguments;
        var changed = server.Configuration != next.Configuration;
        server = next;
        if (changed && !dirty && !saving) Load();
        else if (!dirty && !saving) ShowState();
        else if (changed) Find<TextBlock>("ConfigurationMessage").Text = "Saved settings changed elsewhere. Reload saved to discard your edits, or Save to replace them.";
    }

    private void Load()
    {
        loading = true;
        try
        {
            var options = JsonNode.Parse(server.Configuration.Length == 0 ? "{}" : server.Configuration)!;
            string ReadLines(string name) => string.Join('\n', options[name]?.AsArray().Select(value => value!.GetValue<string>()) ?? []);
            Find<TextBlock>("IntelliJTermsStatus").Text = "Setup checks the selected installation’s agreement. Existing acceptance is reused; changed terms require acknowledgement.";
            Find<TextBox>("IntelliJInstallation").Text = options["installationPath"]?.GetValue<string>() ?? "";
            Find<TextBox>("IntelliJProjectJdk").Text = options["projectJdk"]?.GetValue<string>() ?? "";
            Find<TextBox>("IntelliJBuildTool").Text = options["buildTool"]?.GetValue<string>() ?? "";
            Find<CheckBox>("AutoStart").IsChecked = options["autoStart"]?.GetValue<bool>() ?? false;
            Find<CheckBox>("DefaultArguments").IsChecked = options["arguments"] is null;
            Find<TextBox>("Arguments").Text = ReadLines("arguments");
            Find<TextBox>("IncludePaths").Text = ReadLines("includePaths");
            Find<TextBox>("CompilerFlags").Text = ReadLines("compilerFlags");
            Find<TextBox>("CompilationDatabase").Text = options["compilationDatabasePath"]?.GetValue<string>() ?? "";
            Find<TextBox>("InitializationOptions").Text = Pretty(options["initializationOptions"]?.ToJsonString() ?? "{}");
            Find<TextBox>("ServerSettings").Text = Pretty(options["settings"]?.ToJsonString() ?? "{}");
            foreach (var name in new[] { "Arguments", "IncludePaths", "CompilerFlags", "CompilationDatabase", "InitializationOptions", "ServerSettings", "IntelliJInstallation", "IntelliJProjectJdk", "IntelliJBuildTool" })
                loadedText[name] = Find<TextBox>(name).Text;
            loadedAutoStart = Find<CheckBox>("AutoStart").IsChecked;
            loadedDefaultArguments = Find<CheckBox>("DefaultArguments").IsChecked;
            dirty = false;
            RefreshFields();
            ShowState();
        }
        finally { loading = false; }
    }

    private void ShowState()
    {
        var pending = server.ActiveConfiguration.Length > 0 && server.Configuration != server.ActiveConfiguration;
        Find<Expander>("ConfigurationExpander").Header = pending ? "Workspace settings · restart needed" : "Workspace settings";
        Find<TextBlock>("ConfigurationMessage").Text = server.State is "error" or "expired" or "disabled" ? server.Detail : pending ? "Saved settings differ from the running server. Restart to apply them." : server.Id == "intellij" ? "Review the agreement and start IntelliJ after choosing it in Settings → LSP." : "Applies on next start.";
        Find<Expander>("ActiveConfigurationExpander").IsVisible = server.ActiveConfiguration.Length > 0;
        Find<TextBlock>("ActiveConfiguration").Text = server.ActiveConfiguration.Length > 0 ? Pretty(server.ActiveConfiguration) : "";
    }

    private static string Pretty(string json) => JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json), Json);
    private string[] Lines(string name) => (Find<TextBox>(name).Text ?? "").Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
    private async Task SaveAsync(bool restart, bool acceptanceReviewed = false, bool forceReview = false)
    {
        if (saving || lifetime is not { } current) return;
        if (invalidFields.Count > 0)
        {
            Find<TextBlock>("ConfigurationMessage").Text = "Correct invalid settings before saving: " + string.Join(", ", invalidFields.Select(field => field.Label));
            return;
        }
        saving = true;
        Find<TextBlock>("ConfigurationMessage").Text = restart ? "Starting language server…" : "Saving settings…";
        Find<Button>("ReviewIntelliJTerms").IsEnabled = false;
        Find<Button>("SaveConfiguration").IsEnabled = Find<Button>("RestartConfiguration").IsEnabled = false;
        try
        {
            var selected = false;
            if (server.Id == "intellij")
            {
                var currentOptions = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "configuration", Server: "intellij"), current.Token), current.Token);
                if (lifetime != current) return;
                selected = JsonNode.Parse(string.IsNullOrEmpty(currentOptions.Configuration) ? "{}" : currentOptions.Configuration)?["useForJavaAndKotlin"]?.GetValue<bool>() == true;
            }
            if (restart && server.Id == "intellij" && !selected)
            {
                Find<TextBlock>("ConfigurationMessage").Text = "Select IntelliJ as the Java / Kotlin provider in Settings → LSP before starting it.";
                return;
            }
            var options = new
            {
                useForJavaAndKotlin = selected,
                installationPath = Find<TextBox>("IntelliJInstallation").Text ?? "",
                projectJdk = Find<TextBox>("IntelliJProjectJdk").Text ?? "",
                buildTool = Find<TextBox>("IntelliJBuildTool").Text ?? "",
                autoStart = Find<CheckBox>("AutoStart").IsChecked == true,
                arguments = Find<CheckBox>("DefaultArguments").IsChecked == true ? null : Lines("Arguments"),
                initializationOptions = JsonSerializer.Deserialize<JsonElement>(Find<TextBox>("InitializationOptions").Text ?? "{}"),
                settings = JsonSerializer.Deserialize<JsonElement>(Find<TextBox>("ServerSettings").Text ?? "{}"),
                includePaths = server.Id == "clangd" ? Lines("IncludePaths") : null,
                compilerFlags = server.Id == "clangd" ? Lines("CompilerFlags") : null,
                compilationDatabasePath = Find<TextBox>("CompilationDatabase").Text ?? ""
            };
            var token = current.Token;
            var reviewBeforeRestart = restart && server.Id == "intellij" && !acceptanceReviewed;
            var request = new LanguageRequest(workspace, Operation: restart && !reviewBeforeRestart ? "restart" : "configure", Server: server.Id, Configuration: JsonSerializer.Serialize(options, Json));
            var result = await Task.Run(async () => await host.LanguageAsync(request, token), token);
            if (lifetime != current) return;
            if (result.State is "error" or "disabled" or "unavailable" or "requiresAcceptance" or "expired") { Find<TextBlock>("ConfigurationMessage").Text = result.Detail; return; }
            var saved = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "configuration", Server: server.Id), token), token);
            if (lifetime != current) return;
            server = server with { Configuration = saved.Configuration, ActiveConfiguration = saved.ActiveConfiguration };
            Load();
            if (restart && !reviewBeforeRestart) Find<TextBlock>("ConfigurationMessage").Text = result.State + ": " + result.Detail;
            if (reviewBeforeRestart && SettingsWindow is { } owner)
            {
                if (!options.useForJavaAndKotlin)
                {
                    Find<TextBlock>("ConfigurationMessage").Text = "Select IntelliJ for Java and Kotlin before starting it.";
                    return;
                }
                var acknowledgement = await host.IntelliJTermsAsync(new(workspace), token);
                if (lifetime != current || token.IsCancellationRequested) return;
                if (acknowledgement.Accepted && !forceReview)
                {
                    var started = await host.LanguageAsync(new(workspace, Operation: "restart", Server: server.Id), token);
                    if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = started.State + ": " + started.Detail;
                    return;
                }
                var terms = new IntelliJTermsWindow(host, workspace, startAfterAcceptance: true);
                terms.Closed += async (_, _) => { if (terms.StartRequested) await SaveAsync(true, acceptanceReviewed: true); };
                terms.Show(owner);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = error.Message; }
        finally
        {
            saving = false;
            Find<Button>("ReviewIntelliJTerms").IsEnabled = true;
            Find<Button>("SaveConfiguration").IsEnabled = true;
            Find<Button>("RestartConfiguration").IsEnabled = true;
        }
    }
}