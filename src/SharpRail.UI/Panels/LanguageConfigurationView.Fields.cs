using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Interactivity;

using SharpRail.Host.Abstractions;

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
            foreach (var (control, key) in new[] { ("ProviderWebsite", "website"), ("ProviderRepository", "repository") })
            {
                var link = Find<Button>(control);
                link.Tag = Uri.TryCreate(schema[key]?.ToString(), UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null;
                link.IsVisible = link.Tag is Uri;
                ToolTip.SetTip(link, link.Tag?.ToString());
            }
            if (Find<Button>("ProviderWebsite").Tag is Uri website &&
                Find<Button>("ProviderRepository").Tag is Uri repository &&
                website.AbsoluteUri.TrimEnd('/').Equals(repository.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
                Find<Button>("ProviderWebsite").IsVisible = false;
            Find<Button>("ProviderRepository").Content = (schema["repositoryLabel"]?.ToString() ?? "Source repository") + " ↗";
            Find<WrapPanel>("ProviderLinks").IsVisible = Find<Button>("ProviderWebsite").IsVisible || Find<Button>("ProviderRepository").IsVisible;
            var version = schema["version"]?.ToString() ?? server.Version;
            Find<TextBlock>("ProviderVersion").Text = version == "toolchain" ? "Version: supplied by the installed SDK" : "Managed version: " + version + " (pinned)";
            Find<TextBlock>("ProviderSource").Text = (version == "toolchain" ? "Binary source: " : "Managed source: ") + (schema["source"]?.ToString() ?? "Not reported by this host");
            Find<TextBlock>("ProviderSettingsHelp").Text = result.Detail;
            optionFields = (schema["fields"]?.AsArray() ?? []).Select(field => new LanguageOptionField(field!, FieldChanged)).ToArray();
            Find<ItemsControl>("ProviderFields").ItemsSource = optionFields;
            RefreshFields();
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) Find<TextBlock>("ProviderSettingsHelp").Text = "Named settings unavailable: " + error.Message; }
    }

    private async void ProviderLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri uri } && TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(uri);
    }

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
                field.Value = json is JsonArray array ? string.Join('\n', array.Select(item => item?.ToString() ?? "")) : json?.ToString() ?? (field.IsChoice ? LanguageOptionField.Default : "");
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
            if (string.IsNullOrWhiteSpace(field.Value) || field.Value == LanguageOptionField.Default) container.Remove(keys[^1]);
            else container[keys[^1]] = field.Type switch
            {
                "boolean" => JsonValue.Create(bool.Parse(field.Value)),
                "integer" => JsonValue.Create(int.Parse(field.Value, CultureInfo.InvariantCulture)),
                "lines" => new JsonArray(field.Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
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
}

internal sealed class LanguageOptionField : INotifyPropertyChanged
{
    internal const string Default = "Server default";
    private string value = "";
    private readonly Action<LanguageOptionField> changed;
    internal LanguageOptionField(JsonNode field, Action<LanguageOptionField> changed)
    {
        this.changed = changed;
        Label = field["label"]!.ToString(); Section = field["section"]!.ToString(); Path = field["path"]!.ToString(); Type = field["type"]!.ToString();
        Help = field["help"]?.ToString() ?? "";
        Choices = Type == "boolean" ? [Default, "true", "false"] : field["choices"] is JsonArray choices ? [Default, .. choices.Select(item => item!.ToString())] : [];
    }
    public string Label { get; }
    public string Help { get; }
    public string Section { get; }
    public string Path { get; }
    public string Type { get; }
    public string[] Choices { get; }
    public bool IsChoice => Choices.Length > 0;
    public bool IsText => !IsChoice;
    public bool Multiline => Type == "lines";
    public string Value
    {
        get => value;
        set { if (this.value == value) return; this.value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); changed(this); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}