using System.Text.Json;

using Avalonia;
using Avalonia.Controls;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// The layered keys as the kit's scoped setting rows. A value of an editable type opens the shared value dialog (in
/// choice mode for an enum key) and saves directly; "Add a setting" offers only the reference's documented keys.
/// </summary>
public static class CodexSettingsSurface
{
    public delegate void Save(CodexWritableScope scope, IReadOnlyList<string> keyPath, JsonElement? value);

    private static readonly JsonSerializerOptions Display = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Show(JsonElement value) => JsonSerializer.Serialize(value, Display);

    private static ValueShape? Shape(string key) => CodexConfigDocs.ValueShape(key) switch
    {
        CodexValueShape.Text => ValueShape.Text,
        CodexValueShape.Number => ValueShape.Number,
        CodexValueShape.Switch => ValueShape.Switch,
        CodexValueShape.List => ValueShape.List,
        _ => null
    };

    public static Control Create(bool projectTrusted, IReadOnlyList<CodexSetting> settings, Save onSave, Action<string> onOpen)
    {
        var frame = new CodexSettingsList();
        frame.FindControl<TextBlock>("TrustText")!.Text = projectTrusted
            ? "Codex trusts this project, so its .codex/config.toml applies."
            : "Codex does not trust this project yet — its .codex/config.toml is ignored until you trust it when Codex asks.";
        var rows = frame.FindControl<StackPanel>("Rows")!;
        var none = frame.FindControl<TextBlock>("NoKeys")!;
        var query = "";
        CodexWritableScope ScopeOf(string key) => settings.FirstOrDefault(entry => entry.Key == key)?.Scope == CodexScope.Project ? CodexWritableScope.Project : CodexWritableScope.User;

        async Task Compose(Control anchor, string key, IReadOnlyList<string> keyPath, JsonElement? value)
        {
            if (TopLevel.GetTopLevel(anchor) is not Window owner) return;
            var result = await SettingValueDialog.ShowAsync(owner, new("Codex", key, value, CodexConfigDocs.AddableKeys, "Save")
            {
                Choices = CodexConfigDocs.EnumValues,
                ShapeFor = Shape,
                KnownKeysOnly = true,
                KeyPlaceholder = "model_reasoning_effort"
            });
            if (result is null) return;
            onSave(ScopeOf(result.Value.Key), key.Length > 0 ? keyPath : result.Value.Key.Split('.'), result.Value.Value);
        }

        Control Value(CodexSetting setting)
        {
            var cell = new CodexSettingValue();
            var change = cell.FindControl<Button>("CodexSettingChange")!;
            var text = cell.FindControl<TextBlock>("StaticValue")!;
            if (setting.Scope != CodexScope.System && SettingValueDialog.ShapeOf(setting.Value) is not null)
            {
                cell.Children.Remove(text);
                cell.FindControl<TextBlock>("ChangeText")!.Text = Show(setting.Value);
                Avalonia.Automation.AutomationProperties.SetName(change, $"Edit {setting.Key}");
                change.Click += (_, _) => _ = Compose(change, setting.Key, setting.KeyPath, setting.Value);
            }
            else
            {
                cell.Children.Remove(change);
                text.Text = Show(setting.Value);
                ToolTip.SetTip(text, Show(setting.Value));
            }
            return cell;
        }

        Control? Actions(CodexSetting setting)
        {
            if (setting.Scope == CodexScope.System) return null;
            var actions = new CodexSettingActions();
            if (SettingValueDialog.ShapeOf(setting.Value) is not null) actions.Children.Remove(actions.FindControl<TextBlock>("EditAsFile")!);
            var scope = setting.Scope == CodexScope.Project ? CodexWritableScope.Project : CodexWritableScope.User;
            actions.FindControl<ContentControl>("Remove")!.Content = ScopedSetting.RowAction("CodexSettingRemove", "Remove", () => onSave(scope, setting.KeyPath, null), danger: true);
            return actions;
        }

        void Render()
        {
            rows.Children.Clear();
            var shown = settings.Where(entry => entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var setting in shown)
                rows.Children.Add(ScopedSetting.SettingRow("Codex", setting.Key, Value(setting),
                    new(setting.Scope.ToString().ToLowerInvariant(), setting.Path),
                    [.. setting.Shadowed.Select(shadow => new ScopedSettingShadow(shadow.Scope.ToString().ToLowerInvariant(), shadow.Path, Show(shadow.Value)))],
                    onOpen, Actions(setting) is { } actions ? [actions] : null, CodexConfigDocs.DocsUrl(setting.Key),
                    "What this key does, in Codex's configuration reference"));
            none.IsVisible = shown.Length == 0;
        }

        var toolbar = frame.FindControl<ContentControl>("Toolbar")!;
        toolbar.Content = ScopedSetting.SettingsToolbar("Codex", query, next => { query = next; Render(); },
            () => _ = Compose(toolbar, "", [], null));
        Render();
        return frame;
    }
}