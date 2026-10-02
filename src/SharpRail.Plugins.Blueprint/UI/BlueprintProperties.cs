using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintProperties : UserControl
{
    private readonly Action<string> edit;
    private string content = "";
    private IReadOnlyList<FrontmatterProperty> properties = [];

    public BlueprintProperties(string content, Action<string> edit)
    {
        AvaloniaXamlLoader.Load(this);
        this.edit = edit;
        Name = "BlueprintProperties";
        Update(content);
    }

    public void Update(string next)
    {
        if (content == next) return;
        content = next;
        var rows = this.FindControl<StackPanel>("Rows")!;
        rows.Children.Clear();
        var block = Frontmatter.Parse(content);
        if (block is null) return;
        if (!block.Readable) { rows.Children.Add(MarkdownPreview.Code(block.Raw)); return; }
        properties = block.Properties;
        for (var index = 0; index < properties.Count; index++)
        {
            var at = index;
            var property = properties[at];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,160,*,Auto") };
            var shape = new ComboBox { Name = "BlueprintPropertyType", ItemsSource = new[] { "Text", "Sequence", "Mapping" }, SelectedIndex = property.Items is not null ? 1 : property.Entries is not null ? 2 : 0, MinWidth = 0, Width = 90 };
            shape.SelectionChanged += (_, _) => Convert(at, shape.SelectedIndex);
            Ui.Place(row, shape);
            Ui.Place(row, new BlueprintEditable(property.Key, false, "BlueprintPropertyKey", key =>
            {
                if (key.Length > 0 && !properties.Where((_, other) => other != at).Any(existing => existing.Key == key))
                    Commit(properties.Select((existing, other) => other == at ? existing with { Key = key } : existing));
            }), 0, 1);
            Control value = property.Items is { } items ? Sequence(at, property, items)
                : property.Entries is { } entries ? Mapping(at, property, entries)
                : Text(at, property);
            Ui.Place(row, value, 0, 2);
            var remove = Ui.Button("×", () => Commit(properties.Where((_, other) => other != at)));
            ToolTip.SetTip(remove, "Remove property " + property.Key);
            Ui.Place(row, remove, 0, 3);
            rows.Children.Add(row);
        }
        rows.Children.Add(Ui.Button("Add property", () =>
        {
            var key = "property";
            for (var suffix = 2; properties.Any(property => property.Key == key); suffix++) key = "property-" + suffix;
            Commit(properties.Append(new FrontmatterProperty(key, Text: "")));
        }, "add"));
    }

    private Control Text(int at, FrontmatterProperty property)
    {
        var editable = new BlueprintEditable(property.Text ?? "", false, "BlueprintPropertyValue", value => Set(at, property with { Text = value }));
        if (property.Key != "status") return editable;
        var suggestions = new ContextMenu
        {
            ItemsSource = new[] { "draft", "active", "stale", "done", "deprecated" }.Select(status =>
        {
            var item = new MenuItem { Header = status };
            item.Click += (_, _) => Set(at, property with { Text = status });
            return item;
        }).ToArray()
        };
        editable.ContextMenu = suggestions;
        return editable;
    }

    private Control Sequence(int at, FrontmatterProperty property, IReadOnlyList<string> items)
    {
        var values = new WrapPanel();
        for (var index = 0; index < items.Count; index++)
        {
            var item = index;
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Children.Add(new BlueprintEditable(items[item], false, "BlueprintPropertyItem", text => Set(at, property with { Items = items.Select((value, other) => other == item ? text : value).ToArray() })));
            chip.Children.Add(Ui.Button("×", () => Set(at, property with { Items = items.Where((_, other) => other != item).ToArray() })));
            values.Children.Add(chip);
        }
        var add = new TextBox { Name = "BlueprintPropertyItemAdd", PlaceholderText = "Add", Width = 80 };
        add.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || add.Text?.Trim() is not { Length: > 0 } text) return;
            Set(at, property with { Items = items.Append(text).ToArray() }); e.Handled = true;
        };
        values.Children.Add(add);
        return values;
    }

    private Control Mapping(int at, FrontmatterProperty property, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        var values = new StackPanel();
        for (var index = 0; index < entries.Count; index++)
        {
            var item = index;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,Auto") };
            Ui.Place(row, new BlueprintEditable(entries[item].Key, false, "BlueprintMapKey", key =>
            {
                if (key.Length > 0 && !entries.Where((_, other) => other != item).Any(pair => pair.Key == key))
                    Set(at, property with { Entries = entries.Select((pair, other) => other == item ? new(key, pair.Value) : pair).ToArray() });
            }));
            Ui.Place(row, new BlueprintEditable(entries[item].Value, false, "BlueprintMapValue", value =>
                Set(at, property with { Entries = entries.Select((pair, other) => other == item ? new(pair.Key, value) : pair).ToArray() })), 0, 1);
            Ui.Place(row, Ui.Button("×", () => Set(at, property with { Entries = entries.Where((_, other) => other != item).ToArray() })), 0, 2);
            values.Children.Add(row);
        }
        values.Children.Add(Ui.Button("Add entry", () =>
        {
            var key = "key";
            for (var suffix = 2; entries.Any(pair => pair.Key == key); suffix++) key = "key-" + suffix;
            Set(at, property with { Entries = entries.Append(new(key, "")).ToArray() });
        }));
        return values;
    }

    private void Set(int at, FrontmatterProperty property) => Commit(properties.Select((old, index) => index == at ? property : old));
    private static string Inline(FrontmatterProperty property) => property.Items is { } items ? "[" + string.Join(", ", items) + "]"
        : property.Entries is { } entries ? "{" + string.Join(", ", entries.Select(pair => pair.Key + ": " + pair.Value)) + "}" : property.Text ?? "";

    private void Convert(int at, int shape)
    {
        var property = properties[at];
        if (shape == (property.Items is not null ? 1 : property.Entries is not null ? 2 : 0)) return;
        if (shape == 0) { Set(at, new(property.Key, Text: Inline(property))); return; }
        var items = property.Items ?? (property.Entries is { } entries ? entries.Select(pair => pair.Key + ": " + pair.Value).ToArray()
            : string.IsNullOrWhiteSpace(property.Text) ? [] : new[] { property.Text! });
        if (shape == 1) { Set(at, new(property.Key, Items: items)); return; }
        var pairs = items.Select((item, index) => item.IndexOf(": ", StringComparison.Ordinal) is var split && split > 0
            ? new KeyValuePair<string, string>(item[..split], item[(split + 2)..]) : new((index + 1).ToString(), item)).ToArray();
        if (pairs.Select(pair => pair.Key).Distinct().Count() != pairs.Length)
            pairs = items.Select((item, index) => new KeyValuePair<string, string>((index + 1).ToString(), item)).ToArray();
        Set(at, new(property.Key, Entries: pairs));
    }

    private void Commit(IEnumerable<FrontmatterProperty> next)
    {
        var properties = next.ToArray();
        if (properties.Length == 0) { edit(""); return; }
        static string Scalar(string value) => value.Length == 0 ? "\"\"" :
            Regex.IsMatch(value, @"^[\s>|&*?#@`'""%{}\[\],-]|[:#]\s|\s$|^(true|false|null|~|yes|no)$", RegexOptions.IgnoreCase) || value.Contains('\n')
            ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
        var lines = properties.Select(property => property.Items is { } items ? property.Key + ":\n" + string.Join('\n', items.Select(item => "  - " + Scalar(item)))
            : property.Entries is { } entries ? property.Key + ":\n" + string.Join('\n', entries.Select(pair => "  " + pair.Key + ": " + Scalar(pair.Value)))
            : property.Key + ": " + Scalar(property.Text ?? ""));
        edit("---\n" + string.Join('\n', lines) + "\n---\n");
    }
}