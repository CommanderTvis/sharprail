using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguageConfigurationView
{
    private LanguageOptionField[] optionFields = [];
    private bool refreshingFields;
    private readonly HashSet<LanguageOptionField> invalidFields = [];

    private async Task LoadFieldsAsync()
    {
        if (lifetime is not { } current) return;
        try
        {
            var result = await host.LanguageAsync(new(workspace, Operation: "configurationSchema", Server: server.Id), current.Token);
            if (lifetime != current) return;
            if (result.State != "ready" || result.Configuration.Length == 0) return;
            var schema = JsonNode.Parse(result.Configuration)!;
            var provider = (schema["provider"]?.ToString() ?? server.Id).Split(" · ", 2, StringSplitOptions.None);
            Find<TextBlock>("ProviderName").Text = provider[0];
            Find<TextBlock>("ProviderPublisher").Text = provider.Length > 1 ? provider[1].Replace("; ", "\n", StringComparison.Ordinal) : "";
            Find<TextBlock>("ProviderPublisher").IsVisible = provider.Length > 1;
            LanguageProviderLinks.Apply(schema, Find<Button>("ProviderWebsite"), Find<Button>("ProviderRepository"), Find<WrapPanel>("ProviderLinks"));
            var version = schema["version"]?.ToString() ?? server.Version;
            Find<TextBlock>("ProviderVersion").Text = version == "toolchain" ? "Version: supplied by the installed SDK" : "Managed version: " + version + " (pinned)";
            Find<TextBlock>("ProviderSource").Text = (version == "toolchain" ? "Binary source: " : "Managed source: ") + (schema["source"]?.ToString() ?? "Not reported by this host");
            Find<TextBlock>("ProviderSettingsHelp").Text = result.Detail;
            optionFields = (schema["fields"]?.AsArray() ?? []).Select(field => new LanguageOptionField(field!, FieldChanged, CanBrowseHostPaths(host))).ToArray();
            Find<ItemsControl>("ProviderFields").ItemsSource = optionFields;
            RefreshFields();
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) Find<TextBlock>("ProviderSettingsHelp").Text = "Named settings unavailable: " + error.Message; }
    }

    private void ProviderLinkClicked(object? sender, RoutedEventArgs e) => LanguageProviderLinks.Open(sender, e);

    private void RefreshFields()
    {
        if (refreshingFields) return;
        refreshingFields = true;
        try
        {
            foreach (var field in optionFields)
            {
                var json = JsonNode.Parse(Find<TextBox>(field.Section == "settings" ? "ServerSettings" : "InitializationOptions").Text ?? "{}");
                foreach (var part in field.Path.Split('.')) json = json is JsonObject obj ? obj[part] : null;
                invalidFields.Remove(field);
                field.Load(json);
            }
        }
        catch (System.Text.Json.JsonException) { }
        finally { refreshingFields = false; }
    }

    private void FieldChanged(LanguageOptionField field)
    {
        if (refreshingFields) return;
        try
        {
            var editor = Find<TextBox>(field.Section == "settings" ? "ServerSettings" : "InitializationOptions");
            var document = JsonNode.Parse(editor.Text ?? "{}")!.AsObject();
            var keys = field.Path.Split('.');
            var container = document;
            foreach (var key in keys[..^1])
            {
                if (container[key] is not JsonObject nested) container[key] = nested = new JsonObject();
                container = nested;
            }
            if (!field.HasOverride) container.Remove(keys[^1]);
            else container[keys[^1]] = field.Type switch
            {
                "boolean" => JsonValue.Create(bool.Parse(field.Value)),
                "integer" => JsonValue.Create(field.ParseInteger()),
                "lines" or "features" when !field.AllFeatures => new JsonArray(field.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => (JsonNode?)JsonValue.Create(value.TrimEnd('\r'))).ToArray()),
                "features" => JsonValue.Create("all"),
                _ => JsonValue.Create(field.Value)
            };
            refreshingFields = true;
            editor.Text = document.ToJsonString(Json);
            invalidFields.Remove(field);
            dirty = true;
            Find<TextBlock>("ConfigurationMessage").Text = "Unsaved workspace settings. Save and restart to apply.";
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or OverflowException or InvalidOperationException)
        { invalidFields.Add(field); dirty = true; Find<TextBlock>("ConfigurationMessage").Text = "Cannot apply " + field.Label + ": " + error.Message; }
        finally { refreshingFields = false; }
    }
    private void ResetFieldClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LanguageOptionField field }) field.Value = LanguageOptionField.Default;
    }

    private async void BrowseFieldClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var field = button.DataContext as LanguageOptionField;
        var target = button.Tag is string name ? Find<TextBox>(name) : null;
        var directory = field?.PathKind == "folder" || target?.Name is "IncludePaths" or "CompilationDatabase" or "IntelliJInstallation" or "IntelliJProjectJdk";
        if (button.Tag is "folder") directory = true;
        if (host is not LocalProjectAdapter || OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var allowMultiple = field?.Multiline == true || target?.Name == "IncludePaths";
            var paths = directory
                ? (await top.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose " + (field?.Label ?? "folder"), AllowMultiple = allowMultiple })).Select(item => item.TryGetLocalPath()).OfType<string>().ToArray()
                : (await top.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose " + (field?.Label ?? "file"), AllowMultiple = allowMultiple })).Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths.Length == 0 || lifetime is null) return;
            if (field is not null)
                field.Value = field.Multiline ? AppendPaths(field.HasOverride && !field.CustomJson ? field.Value : "", paths) : paths[0];
            else if (target is not null)
                target.Text = allowMultiple ? AppendPaths(target.Text ?? "", paths) : paths[0];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { Find<TextBlock>("ConfigurationMessage").Text = "Cannot choose host path: " + error.Message; }
    }

    internal static bool CanBrowseHostPaths(IProjectServices host) => host is LocalProjectAdapter && !OperatingSystem.IsAndroid() && !OperatingSystem.IsBrowser();
    private static string AppendPaths(string current, string[] paths) => string.Join('\n', new[] { current }.Where(value => value.Length > 0).Concat(paths));

}

internal sealed class LanguageOptionField : INotifyPropertyChanged
{
    internal const string Default = "Server default";
    private string value = "";
    private bool allFeatures;
    private readonly bool canBrowse;
    private readonly Action<LanguageOptionField> changed;
    internal LanguageOptionField(JsonNode field, Action<LanguageOptionField> changed, bool canBrowse = false)
    {
        this.changed = changed; this.canBrowse = canBrowse;
        Label = field["label"]!.ToString(); Section = field["section"]!.ToString(); Path = field["path"]!.ToString(); Type = field["type"]!.ToString();
        Help = field["help"]?.ToString() ?? "";
        PathKind = field["pathKind"]?.ToString() ?? "";
        AllowCustomChoices = field["allowCustomChoices"]?.GetValue<bool>() == true;
        Minimum = field["minimum"]?.GetValue<decimal>() ?? int.MinValue;
        Choices = Type == "boolean" ? [Default, "true", "false"] : field["choices"] is JsonArray choices ? [Default, .. choices.Select(item => item!.ToString())] : [];
    }
    public string Label { get; }
    public string Help { get; }
    public string Section { get; }
    public string Path { get; }
    public string Type { get; }
    public string PathKind { get; }
    public string[] Choices { get; private set; }
    public bool AllowCustomChoices { get; }
    public decimal Minimum { get; }
    public bool HasOverride { get; private set; }
    public bool CustomJson { get; private set; }
    public bool IsChoice => Choices.Length > 0 && !CustomJson;
    public bool IsText => (!IsChoice || AllowCustomChoices) && !IsNumber && !CustomJson;
    public bool IsNumber => Type == "integer" && !CustomJson;
    public bool Multiline => Type is "lines" or "features";
    public bool HasPath => PathKind.Length > 0;
    public bool FilePath => canBrowse && PathKind is "file" or "fileOrFolder";
    public bool FolderPath => canBrowse && PathKind is "folder" or "fileOrFolder";
    public bool IsFeatures => Type == "features" && !CustomJson;
    public bool AllFeatures
    {
        get => allFeatures;
        set
        {
            if (value == allFeatures) return;
            allFeatures = value; this.value = value ? "all" : ""; HasOverride = true; CustomJson = false;
            Notify(); changed(this);
        }
    }
    public decimal? Number
    {
        get => HasOverride && decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        set => Value = value?.ToString(CultureInfo.InvariantCulture) ?? Default;
    }
    public string Value
    {
        get => value;
        set
        {
            var hasOverride = value != Default;
            if (this.value == value && HasOverride == hasOverride) return;
            this.value = value; HasOverride = hasOverride; CustomJson = false; allFeatures = false;
            Notify(); changed(this);
        }
    }
    internal int ParseInteger()
    {
        var number = int.Parse(value, CultureInfo.InvariantCulture);
        if (number < Minimum) throw new FormatException("Must be at least " + Minimum.ToString(CultureInfo.InvariantCulture) + ".");
        return number;
    }
    internal void Load(JsonNode? node)
    {
        HasOverride = node is not null;
        allFeatures = Type == "features" && node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text == "all";
        CustomJson = node is not null && !Compatible(node);
        value = node is JsonArray array && !CustomJson ? string.Join('\n', array.Select(item => item!.GetValue<string>())) : node?.ToString() ?? Default;
        if (IsChoice && HasOverride && !Choices.Contains(value)) Choices = [.. Choices, value];
        Notify();
    }
    private bool Compatible(JsonNode node) => Type switch
    {
        "lines" => node is JsonArray array && array.All(item => item is JsonValue value && value.TryGetValue<string>(out _)),
        "features" => node is JsonValue value && value.TryGetValue<string>(out var text) && text == "all" || node is JsonArray array && array.All(item => item is JsonValue itemValue && itemValue.TryGetValue<string>(out _)),
        "integer" => node is JsonValue value && value.TryGetValue<int>(out _),
        "boolean" => node is JsonValue value && value.TryGetValue<bool>(out _),
        _ => node is JsonValue value && value.TryGetValue<string>(out _)
    };
    private void Notify() => PropertyChanged?.Invoke(this, new(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}