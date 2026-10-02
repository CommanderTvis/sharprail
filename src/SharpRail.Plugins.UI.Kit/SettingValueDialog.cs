using System.Globalization;
using System.Text.Json;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>The value shapes a setting can be composed as.</summary>
public enum ValueShape { Text, Number, Switch, List }

/// <summary>What the value composer edits and offers.</summary>
/// <param name="NamePrefix">Prefixes every control name, so two panes' dialogs stay distinguishable.</param>
/// <param name="SettingKey">The key being changed, or empty to add one.</param>
/// <param name="CurrentValue">The value it has now, or null when adding.</param>
/// <param name="KnownKeys">Keys offered while typing a new one.</param>
/// <param name="SubmitLabel">The confirming button's label.</param>
public sealed record SettingValueDialogOptions(string NamePrefix, string SettingKey, JsonElement? CurrentValue, IReadOnlyList<string> KnownKeys, string SubmitLabel)
{
    /// <summary>A placeholder for the key field.</summary>
    public string? KeyPlaceholder { get; init; }

    /// <summary>The closed set of values a key accepts, offered as segments instead of a free field.</summary>
    public Func<string, IReadOnlyList<string>?>? Choices { get; init; }

    /// <summary>The shape a key is declared with, which hides the shape switch.</summary>
    public Func<string, ValueShape?>? ShapeFor { get; init; }

    /// <summary>Restricts a new key to <see cref="KnownKeys"/>, picked from a filtered list.</summary>
    public bool KnownKeysOnly { get; init; }
}

/// <summary>
/// Composes one setting's value as text, a number, on/off or a list of text, and returns the key and the value as
/// JSON. Validation runs as it is typed and the confirming button stays disabled while it fails.
/// </summary>
public static class SettingValueDialog
{
    private static readonly (ValueShape Shape, string Label)[] Shapes =
        [(ValueShape.Text, "Text"), (ValueShape.Number, "Number"), (ValueShape.Switch, "On / off"), (ValueShape.List, "List")];

    /// <summary>The shape a value can be edited as, or null for one only its file can change (an object, a mixed list).</summary>
    public static ValueShape? ShapeOf(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => ValueShape.Switch,
        JsonValueKind.Number => ValueShape.Number,
        JsonValueKind.String => ValueShape.Text,
        JsonValueKind.Array when value.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) => ValueShape.List,
        _ => null
    };

    /// <summary>Shows the composer owned by <paramref name="owner"/>; null when cancelled.</summary>
    public static async Task<(string Key, JsonElement Value)?> ShowAsync(Window owner, SettingValueDialogOptions options)
    {
        var prefix = options.NamePrefix;
        var adding = options.SettingKey.Length == 0;
        var detected = ShapeOf(options.CurrentValue) ?? options.ShapeFor?.Invoke(options.SettingKey);
        var window = DialogWindow.Create(adding ? "Add a setting" : $"Change {options.SettingKey}", 512);
        window.Tag = prefix + "ValueDialog";
        var fields = window.FindControl<StackPanel>("DialogFields")!;
        var actions = window.FindControl<StackPanel>("DialogActions")!;

        var key = options.SettingKey;
        var shape = detected ?? ValueShape.Text;
        var current = options.CurrentValue;
        var text = current is { ValueKind: JsonValueKind.String or JsonValueKind.Number } scalar
            ? scalar.ValueKind == JsonValueKind.String ? scalar.GetString()! : scalar.GetRawText() : "";
        var on = current is { ValueKind: JsonValueKind.True };
        var items = current is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(item => item.ToString()).ToList() : [""];
        var choice = current is { ValueKind: JsonValueKind.String } chosen ? chosen.GetString()! : "";

        var problemText = Ui.Text("", Ui.Warning, 12);
        problemText.Name = prefix + "ValueProblem";
        problemText.TextWrapping = TextWrapping.Wrap;
        var submit = Ui.Button(options.SubmitLabel, () => { });
        submit.Name = prefix + "ValueContinue";
        submit.Classes.Add("primary");
        submit.IsDefault = true;
        var valueArea = new StackPanel { Spacing = 8 };
        Control? focusFirst = null;

        IReadOnlyList<string>? KeyChoices() => options.Choices?.Invoke(key.Trim()) is { Count: > 0 } offered ? offered : null;

        string? Problem()
        {
            if (KeyChoices() is not null) return choice.Length == 0 ? "Pick a value." : null;
            if (key.Trim().Length == 0) return "A key is needed.";
            if (shape == ValueShape.Number && !double.IsFinite(Parse(text))) return "That is not a number.";
            if (shape == ValueShape.Text && text.Length == 0) return "An empty string is probably not what you mean.";
            return null;
        }

        void Validate()
        {
            var problem = Problem();
            problemText.Text = problem ?? "";
            problemText.IsVisible = problem is not null;
            submit.IsEnabled = problem is null;
        }

        void PickKey(string next)
        {
            key = next;
            if (options.ShapeFor?.Invoke(next) is { } declared) shape = declared;
            RenderValue();
        }

        if (adding)
        {
            fields.Children.Add(Ui.Text("Key", Ui.Muted, 12));
            if (options.KnownKeysOnly)
            {
                var query = new TextBox { Name = prefix + "ValueKey", PlaceholderText = options.KeyPlaceholder };
                AutomationProperties.SetName(query, "Setting key");
                var list = new ListBox { Name = prefix + "ValueKeyPicker", MaxHeight = 192 };
                var picked = Ui.Text("", Ui.TextBrush, 12);
                picked.Name = prefix + "ValueKeyPicked";
                picked.FontFamily = Ui.CodeFont;
                void Filter() => list.ItemsSource = options.KnownKeys.Where(known => known.Contains(query.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
                query.TextChanged += (_, _) => Filter();
                list.SelectionChanged += (_, _) =>
                {
                    if (list.SelectedItem is not string selected) return;
                    picked.Text = selected;
                    PickKey(selected);
                };
                Filter();
                fields.Children.Add(query);
                fields.Children.Add(list);
                fields.Children.Add(picked);
                focusFirst = query;
            }
            else
            {
                var input = new AutoCompleteBox
                {
                    Name = prefix + "ValueKey",
                    ItemsSource = options.KnownKeys,
                    FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
                    PlaceholderText = options.KeyPlaceholder,
                    FontFamily = Ui.CodeFont
                };
                AutomationProperties.SetName(input, "Setting key");
                input.TextChanged += (_, _) => { key = input.Text ?? ""; if (options.ShapeFor?.Invoke(key.Trim()) is { } declared && declared != shape) { shape = declared; RenderValue(); } else Validate(); };
                fields.Children.Add(input);
                focusFirst = input;
            }
        }

        fields.Children.Add(Ui.Text("Value", Ui.Muted, 12));
        fields.Children.Add(valueArea);
        fields.Children.Add(problemText);

        void RenderValue()
        {
            valueArea.Children.Clear();
            if (KeyChoices() is { } offered)
            {
                var segments = new WrapPanel { Name = prefix + "ValueChoices" };
                foreach (var option in offered)
                {
                    var segment = Ui.Segment(prefix + "ValueChoice_" + option, option);
                    segment.IsChecked = choice == option;
                    segment.Margin = new Thickness(0, 0, 8, 0);
                    segment.Click += (_, _) => { choice = option; RenderValue(); };
                    segments.Children.Add(segment);
                }
                valueArea.Children.Add(segments);
                Validate();
                return;
            }
            if (adding && options.ShapeFor?.Invoke(key.Trim()) is null || detected is null)
            {
                var switcher = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var (option, label) in Shapes)
                {
                    var segment = Ui.Segment(prefix + "ValueShape_" + option.ToString().ToLowerInvariant(), label);
                    segment.IsChecked = shape == option;
                    segment.Click += (_, _) => { shape = option; RenderValue(); };
                    switcher.Children.Add(segment);
                }
                valueArea.Children.Add(switcher);
            }
            switch (shape)
            {
                case ValueShape.Switch:
                    var toggle = Ui.Button(on ? "true" : "false", () => { });
                    toggle.Name = prefix + "ValueSwitch";
                    toggle.Tag = on;
                    toggle.HorizontalAlignment = HorizontalAlignment.Left;
                    AutomationProperties.SetName(toggle, "Value");
                    if (on) toggle.Background = Ui.PrimarySubtle;
                    toggle.Click += (_, _) => { on = !on; RenderValue(); };
                    valueArea.Children.Add(toggle);
                    break;
                case ValueShape.List:
                    valueArea.Children.Add(ListEntries());
                    break;
                default:
                    var field = new TextBox { Name = prefix + "ValueText", Text = text, FontFamily = Ui.CodeFont };
                    AutomationProperties.SetName(field, "Value");
                    field.TextChanged += (_, _) => { text = field.Text ?? ""; Validate(); };
                    valueArea.Children.Add(field);
                    break;
            }
            Validate();
        }

        Control ListEntries()
        {
            var list = new StackPanel { Name = prefix + "ValueList", Spacing = 4 };
            for (var index = 0; index < items.Count; index++)
            {
                var at = index;
                var row = new DockPanel();
                var remove = Ui.IconButton("close", $"Remove entry {at + 1}", () => { items.RemoveAt(at); RenderValue(); });
                remove.Name = prefix + "ValueListRemove";
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var entry = new TextBox { Name = prefix + "ValueListEntry", Text = items[at], AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.CodeFont };
                AutomationProperties.SetName(entry, $"Entry {at + 1}");
                entry.TextChanged += (_, _) => { items[at] = entry.Text ?? ""; Validate(); };
                row.Children.Add(entry);
                list.Children.Add(row);
            }
            var add = Ui.Button("Add entry", () => { items.Add(""); RenderValue(); }, "add");
            add.Name = prefix + "ValueListAdd";
            add.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(add);
            return new ScrollViewer { MaxHeight = 360, Content = list };
        }

        RenderValue();
        (string, JsonElement)? result = null;
        actions.Children.Add(Ui.Button("Cancel", () => window.Close()));
        submit.Click += (_, _) =>
        {
            Validate();
            if (!submit.IsEnabled) return;
            result = (key.Trim(), KeyChoices() is not null ? JsonSerializer.SerializeToElement(choice) : Compose(shape, text, on, items));
            window.Close();
        };
        actions.Children.Add(submit);
        window.Opened += (_, _) => (focusFirst ?? valueArea.Children.OfType<TextBox>().FirstOrDefault())?.Focus();
        await window.ShowDialog(owner);
        return result;
    }

    // As JavaScript's Number(): blank is zero, anything else must parse whole.
    private static double Parse(string text) =>
        text.Trim().Length == 0 ? 0 : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;

    private static JsonElement Compose(ValueShape shape, string text, bool on, IReadOnlyList<string> items) => shape switch
    {
        ValueShape.Switch => JsonSerializer.SerializeToElement(on),
        ValueShape.Number => JsonSerializer.SerializeToElement(Parse(text)),
        ValueShape.List => JsonSerializer.SerializeToElement(items.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray()),
        _ => JsonSerializer.SerializeToElement(text)
    };
}