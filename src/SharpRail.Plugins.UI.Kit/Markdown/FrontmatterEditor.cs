using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace SharpRail.Plugins.UI.Kit.Markdown;

internal sealed class FrontmatterEditor : UserControl
{
    private readonly List<FrontmatterProperty> properties;
    private readonly StackPanel rows;
    private readonly Action<IReadOnlyList<FrontmatterProperty>> commit;

    internal FrontmatterEditor(FrontmatterBlock block, Action<IReadOnlyList<FrontmatterProperty>> commit)
    {
        AvaloniaXamlLoader.Load(this);
        properties = block.Properties.ToList();
        this.commit = commit;
        rows = this.FindControl<StackPanel>("Rows")!;
        this.FindControl<Button>("FrontmatterAdd")!.Click += (_, _) =>
        {
            properties.Add(new(Unique(properties.Select(property => property.Key), "property"), Text: ""));
            Save();
        };
        Fill();
    }

    private static string Unique(IEnumerable<string> keys, string prefix)
    {
        var used = keys.ToHashSet();
        var key = prefix;
        for (var suffix = 2; used.Contains(key); suffix++) key = prefix + "-" + suffix;
        return key;
    }

    internal IReadOnlyList<FrontmatterProperty> Properties => properties;

    internal void SuppressBlur(bool suppress)
    {
        foreach (var field in this.GetLogicalDescendants().OfType<FrontmatterField>()) field.SuppressBlur = suppress;
    }

    private void Save(bool rebuild = true) { commit(properties.ToArray()); if (rebuild) Fill(); }

    private void Set(int index, FrontmatterProperty value, bool rebuild = true) { properties[index] = value; Save(rebuild); }

    private void Fill()
    {
        var addFields = rows.GetLogicalDescendants().OfType<FrontmatterField>().Where(field => field.Name == "FrontmatterListAdd").ToArray();
        var focusedAdd = Array.FindIndex(addFields, field => field.IsKeyboardFocusWithin);
        rows.Children.Clear();
        for (var index = 0; index < properties.Count; index++)
        {
            var at = index;
            var property = properties[at];
            var key = new FrontmatterField(property.Key, "FrontmatterKey", next =>
            {
                next = next.Trim();
                if (!Frontmatter.ValidKey(next) || properties.Where((_, i) => i != at).Any(item => item.Key == next)) { Fill(); return; }
                Set(at, properties[at] with { Key = next });
            });
            Control value;
            if (property.Items is { } items)
            {
                var list = new WrapPanel { Name = "FrontmatterValue" };
                for (var i = 0; i < items.Count; i++)
                {
                    var itemIndex = i;
                    list.Children.Add(new FrontmatterChip(items[i],
                        () => Set(at, properties[at] with { Items = items.Where((_, n) => n != itemIndex).ToArray() })));
                }
                FrontmatterField? add = null;
                add = new("", "FrontmatterListAdd", next =>
                {
                    if (next.Trim().Length > 0) Set(at, properties[at] with { Items = [.. items, next.Trim()] });
                    add!.Clear();
                }, placeholder: "Add…");
                list.Children.Add(add);
                value = list;
            }
            else if (property.Entries is { } entries)
            {
                var map = new FrontmatterMapping(() => Set(at, properties[at] with
                {
                    Entries = [.. properties[at].Entries!, new(Unique(properties[at].Entries!.Select(entry => entry.Key), "key"), "")]
                }));
                for (var i = 0; i < entries.Count; i++)
                {
                    var entryIndex = i;
                    var entry = entries[i];
                    var entryKey = new FrontmatterField(entry.Key, "FrontmatterMapKey", next =>
                    {
                        next = next.Trim();
                        if (!Frontmatter.ValidKey(next) || entries.Where((_, n) => n != entryIndex).Any(item => item.Key == next)) { Fill(); return; }
                        Set(at, properties[at] with { Entries = entries.Select((item, n) => n == entryIndex ? new(next, item.Value) : item).ToArray() });
                    });
                    var entryValue = new FrontmatterField(entry.Value, "FrontmatterMapValue", next =>
                        Set(at, properties[at] with { Entries = properties[at].Entries!.Select((item, n) => n == entryIndex ? new(item.Key, next) : item).ToArray() }, rebuild: false));
                    map.Entries.Children.Add(new FrontmatterRow(entryKey, entryValue,
                        () => Set(at, properties[at] with { Entries = properties[at].Entries!.Where((_, n) => n != entryIndex).ToArray() }), "Remove entry " + entry.Key));
                }
                value = map;
            }
            else value = new FrontmatterField(property.Text ?? "", "FrontmatterValue", next => Set(at, properties[at] with { Text = next }, rebuild: false),
                property.Key == "status" ? ["draft", "active", "stale", "done", "deprecated"] : property.Key == "type" ? Frontmatter.SpecTypes : null);
            rows.Children.Add(new FrontmatterRow(key, value, () => { properties.RemoveAt(at); Save(); }, "Remove property " + property.Key,
                shape => { if (Frontmatter.Shape(properties[at]) != shape) Set(at, Frontmatter.Convert(properties[at], shape)); }, Frontmatter.Shape(property))
            { Name = "FrontmatterProperty" });
        }
        if (focusedAdd >= 0)
        {
            var field = rows.GetLogicalDescendants().OfType<FrontmatterField>().Where(field => field.Name == "FrontmatterListAdd").ElementAtOrDefault(focusedAdd);
            Dispatcher.UIThread.Post(() => { if (field?.IsEffectivelyVisible == true) field.FocusInput(); }, DispatcherPriority.Loaded);
        }
    }
}