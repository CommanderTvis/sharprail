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
    /// <summary>Counts saves, so a configuration read that a save overtook cannot load its older settings over the newer ones.</summary>
    private int saves;
    internal int Saves => saves;
    /// <summary>What the server's plugin declares (H21): terms to accept, servers it replaces, and compiler-flag options.</summary>
    private readonly bool hasTerms, replaces, compilerFlags;
    private readonly Dictionary<string, string?> loadedText = [];
    private bool? loadedAutoStart;
    private string? loadedBuildTool;
    private bool? loadedDefaultArguments;

    internal LanguageConfigurationView(IProjectServices host, string workspace, LanguageServerState server)
    {
        this.host = host; this.workspace = workspace; this.server = server;
        AvaloniaXamlLoader.Load(this);
        Classes.Set("host-path-entry-only", !CanBrowseHostPaths(host));
        Find<TextBlock>("ConfigurationScope").Text = "This workspace only";
        ToolTip.SetTip(Find<TextBlock>("ConfigurationScope"), workspace);
        var presentation = server.Presentation ?? LanguageServerNameConverter.Presentation(server.Id);
        var groups = presentation?.OptionGroups ?? [];
        hasTerms = presentation?.HasTerms == true;
        replaces = presentation?.Replaces is { Count: > 0 };
        Find<StackPanel>("ProviderOptions").IsVisible = hasTerms || groups.Contains("installation") || groups.Contains("jvmProject");
        Find<TextBlock>("ProviderHeading").Text = LanguageServerNameConverter.Display(server.Id);
        Find<TextBlock>("ProviderNote").Text = presentation?.TermsNote ?? "";
        Find<TextBlock>("ProviderNote").IsVisible = presentation?.TermsNote is { Length: > 0 };
        Find<Button>("ReviewTerms").IsVisible = Find<TextBlock>("TermsStatus").IsVisible = hasTerms;
        Find<Button>("ReviewTerms").Content = "Review agreement and start " + LanguageServerNameConverter.Display(server.Id) + "…";
        Find<StackPanel>("InstallationOptions").IsVisible = groups.Contains("installation");
        Find<StackPanel>("JvmProjectOptions").IsVisible = groups.Contains("jvmProject");
        compilerFlags = groups.Contains("compilerFlags");
        Find<Button>("ReviewTerms").Click += async (_, _) =>
        {
            await SaveAsync(true, forceReview: true);
        };
        Find<CheckBox>("AutoStart").IsEnabled = true;
        Find<CheckBox>("ProjectAutoStart").Click += async (_, _) => await SaveProjectAutostartAsync();
        Find<Button>("RestartConfiguration").IsEnabled = true;
        foreach (var name in new[] { "InstallationPath", "ProjectJdk" })
            Find<TextBox>(name).TextChanged += (_, _) => { if (!loading) dirty = true; };
        Find<ComboBox>("BuildTool").ItemsSource = new[] { "Automatic", "maven", "gradle", "bazel", "jps" };
        Find<ComboBox>("BuildTool").SelectionChanged += (_, _) => { if (!loading) dirty = true; };
        Find<StackPanel>("CompilerOptions").IsVisible = compilerFlags;
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
        AttachedToVisualTree += async (_, _) =>
        {
            await LoadFieldsAsync();
            await RefreshProjectAutostartAsync();
            await RefreshProjectFileAsync();
        };
        foreach (var name in new[] { "InitializationOptions", "ServerSettings" })
            Find<TextBox>(name).TextChanged += (_, _) => { if (!loading) RefreshFields(); };
    }

    private async Task RefreshProjectAutostartAsync()
    {
        if (lifetime is not { } current) return;
        try
        {
            var result = await host.LanguageAsync(new(workspace, Operation: "projectAutostart"), current.Token);
            if (lifetime == current && result.State == "ready")
                Find<CheckBox>("ProjectAutoStart").IsChecked = JsonSerializer.Deserialize<string[]>(result.Configuration)?.Contains(server.Id) == true;
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = error.Message; }
    }

    /// <summary>Shows where the values come from: the project file, a local override, or neither.</summary>
    internal async Task RefreshProjectFileAsync()
    {
        if (lifetime is not { } current) return;
        try
        {
            var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "projectLanguageFile", Server: server.Id), current.Token), current.Token);
            if (lifetime != current || result.State != "ready") return;
            var status = JsonSerializer.Deserialize<JsonElement>(result.Configuration);
            var origin = status.GetProperty("origin").GetString();
            var diagnostics = status.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetString()).ToArray();
            var share = Find<CheckBox>("ShareWithProject");
            if (!dirty) share.IsChecked = origin == "file";
            Find<TextBlock>("ProjectFileNote").Text = (origin switch
            {
                "file" => "Values come from " + status.GetProperty("path").GetString() + ".",
                "local" => "This workspace overrides the project file with local values.",
                _ => "No project file entry for this server."
            }) + (diagnostics.Length > 0 ? " " + string.Join(" ", diagnostics) : "");
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception) { /* The note is informational. */ }
    }

    private async Task SaveProjectAutostartAsync()
    {
        if (lifetime is not { } current) return;
        var checkbox = Find<CheckBox>("ProjectAutoStart");
        checkbox.IsEnabled = false;
        try
        {
            await host.LanguageAsync(new(workspace, Operation: "setProjectAutostart", Server: server.Id,
                Configuration: (checkbox.IsChecked == true).ToString()), current.Token);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = error.Message;
        }
        finally
        {
            if (lifetime == current) { await RefreshProjectAutostartAsync(); checkbox.IsEnabled = true; }
        }
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
        var generation = saves;
        try
        {
            var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "configuration", Server: server.Id), current.Token), current.Token);
            if (lifetime != current) return;
            if (result.State != "ready") { Find<TextBlock>("ConfigurationMessage").Text = result.Detail; return; }
            var next = server with { Configuration = result.Configuration, ActiveConfiguration = result.ActiveConfiguration };
            if (discardDraft && generation == saves && !saving) { server = next; Load(); }
            else Update(next, generation);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = error.Message; }
    }

    internal async Task ReloadAsync() { await RefreshSavedAsync(false); await RefreshProjectFileAsync(); }

    internal void UpdateSavedConfiguration(string configuration) => Update(server with { Configuration = configuration });

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)!;
    /// <param name="savesAtRead">The save count when <paramref name="next"/> was requested; a save since then keeps the newer saved configuration.</param>
    internal void Update(LanguageServerState next, int? savesAtRead = null)
    {
        if (savesAtRead is { } read && (read != saves || saving)) next = next with { Configuration = server.Configuration, ActiveConfiguration = server.ActiveConfiguration };
        if (next == server) { if (!dirty && !saving) ShowState(); return; }
        dirty = invalidFields.Count > 0 || loadedText.Any(item => Find<TextBox>(item.Key).Text != item.Value) ||
            Find<ComboBox>("BuildTool").SelectedItem?.ToString() != loadedBuildTool ||
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
            Find<TextBlock>("TermsStatus").Text = "Setup checks the selected installation’s agreement. Existing acceptance is reused; changed terms require acknowledgement.";
            Find<TextBox>("InstallationPath").Text = options["installationPath"]?.GetValue<string>() ?? "";
            Find<TextBox>("ProjectJdk").Text = options["projectJdk"]?.GetValue<string>() ?? "";
            Find<ComboBox>("BuildTool").SelectedItem = options["buildTool"]?.GetValue<string>() is { Length: > 0 } importer ? importer : "Automatic";
            Find<CheckBox>("AutoStart").IsChecked = options["autoStart"]?.GetValue<bool>() ?? false;
            Find<CheckBox>("DefaultArguments").IsChecked = options["arguments"] is null;
            Find<TextBox>("Arguments").Text = ReadLines("arguments");
            Find<TextBox>("IncludePaths").Text = ReadLines("includePaths");
            Find<TextBox>("CompilerFlags").Text = ReadLines("compilerFlags");
            Find<TextBox>("CompilationDatabase").Text = options["compilationDatabasePath"]?.GetValue<string>() ?? "";
            Find<TextBox>("InitializationOptions").Text = Pretty(options["initializationOptions"]?.ToJsonString() ?? "{}");
            Find<TextBox>("ServerSettings").Text = Pretty(options["settings"]?.ToJsonString() ?? "{}");
            foreach (var name in new[] { "Arguments", "IncludePaths", "CompilerFlags", "CompilationDatabase", "InitializationOptions", "ServerSettings", "InstallationPath", "ProjectJdk" })
                loadedText[name] = Find<TextBox>(name).Text;
            loadedBuildTool = Find<ComboBox>("BuildTool").SelectedItem?.ToString();
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
        Find<TextBlock>("ConfigurationMessage").Text = server.State is "error" or "expired" or "disabled" ? server.Detail : pending ? "Saved settings differ from the running server. Restart to apply them." : replaces ? $"Start {LanguageServerNameConverter.Display(server.Id)} after choosing it in Settings → Code Intelligence." : "Applies on next start.";
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
        saves++;
        Find<TextBlock>("ConfigurationMessage").Text = restart ? "Starting language server…" : "Saving settings…";
        Find<Button>("ReviewTerms").IsEnabled = false;
        Find<Button>("SaveConfiguration").IsEnabled = Find<Button>("RestartConfiguration").IsEnabled = false;
        try
        {
            var selected = false;
            if (replaces)
            {
                var currentOptions = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "javaKotlinProvider"), current.Token), current.Token);
                if (lifetime != current) return;
                selected = currentOptions.Configuration == server.Id;
            }
            if (restart && replaces && !selected)
            {
                Find<TextBlock>("ConfigurationMessage").Text = $"Select {LanguageServerNameConverter.Display(server.Id)} as the provider in Settings → Code Intelligence before starting it.";
                return;
            }
            var options = new
            {
                installationPath = Find<TextBox>("InstallationPath").Text ?? "",
                projectJdk = Find<TextBox>("ProjectJdk").Text ?? "",
                buildTool = Find<ComboBox>("BuildTool").SelectedItem?.ToString() is { } selectedImporter && selectedImporter != "Automatic" ? selectedImporter : "",
                autoStart = Find<CheckBox>("AutoStart").IsChecked == true,
                arguments = Find<CheckBox>("DefaultArguments").IsChecked == true ? null : Lines("Arguments"),
                initializationOptions = JsonSerializer.Deserialize<JsonElement>(Find<TextBox>("InitializationOptions").Text ?? "{}"),
                settings = JsonSerializer.Deserialize<JsonElement>(Find<TextBox>("ServerSettings").Text ?? "{}"),
                includePaths = compilerFlags ? Lines("IncludePaths") : null,
                compilerFlags = compilerFlags ? Lines("CompilerFlags") : null,
                compilationDatabasePath = Find<TextBox>("CompilationDatabase").Text ?? ""
            };
            var token = current.Token;
            var reviewBeforeRestart = restart && hasTerms && !acceptanceReviewed;
            var share = Find<CheckBox>("ShareWithProject").IsChecked == true;
            var serialized = JsonSerializer.Serialize(options, Json);
            if (share)
            {
                var shared = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "saveProjectLanguageFile", Server: server.Id, Configuration: serialized), token), token);
                if (lifetime != current) return;
                if (shared.State != "ready") { Find<TextBlock>("ConfigurationMessage").Text = shared.Detail; return; }
            }
            var request = new LanguageRequest(workspace, Operation: restart && !reviewBeforeRestart ? "restart" : "configure", Server: server.Id,
                Configuration: share ? null : serialized);
            var result = await Task.Run(async () => share && request.Operation == "configure" ? new LanguageResult("ready") : await host.LanguageAsync(request, token), token);
            if (lifetime != current) return;
            if (result.State is "error" or "disabled" or "unavailable" or "requiresAcceptance" or "expired") { Find<TextBlock>("ConfigurationMessage").Text = result.Detail; return; }
            var saved = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "configuration", Server: server.Id), token), token);
            if (lifetime != current) return;
            server = server with { Configuration = saved.Configuration, ActiveConfiguration = saved.ActiveConfiguration };
            Load();
            await RefreshProjectFileAsync();
            if (restart && !reviewBeforeRestart) Find<TextBlock>("ConfigurationMessage").Text = result.State + ": " + result.Detail;
            if (reviewBeforeRestart && SettingsWindow is { } owner)
            {
                if (replaces && !selected)
                {
                    Find<TextBlock>("ConfigurationMessage").Text = $"Select {LanguageServerNameConverter.Display(server.Id)} as the provider before starting it.";
                    return;
                }
                var acknowledgement = await host.LanguageTermsAsync(new(workspace, server.Id), token);
                if (lifetime != current || token.IsCancellationRequested) return;
                if (acknowledgement.Accepted && !forceReview)
                {
                    var started = await host.LanguageAsync(new(workspace, Operation: "restart", Server: server.Id), token);
                    if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = started.State + ": " + started.Detail;
                    return;
                }
                var terms = new LanguageTermsWindow(host, workspace, server.Id, startAfterAcceptance: true);
                terms.Closed += async (_, _) => { if (terms.StartRequested) await SaveAsync(true, acceptanceReviewed: true); };
                terms.Show(owner);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) Find<TextBlock>("ConfigurationMessage").Text = error.Message; }
        finally
        {
            saving = false;
            Find<Button>("ReviewTerms").IsEnabled = true;
            Find<Button>("SaveConfiguration").IsEnabled = true;
            Find<Button>("RestartConfiguration").IsEnabled = true;
        }
    }
}