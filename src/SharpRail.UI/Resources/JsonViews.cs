using System.Globalization;
using System.Text;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Resources;

internal enum JsonChange { Unchanged, Added, Removed, Changed }

/// <summary>One member or element of a structural diff. A changed container carries its children; a changed leaf both values.</summary>
internal sealed record JsonDiffNode(string Key, JsonChange Kind, JsonElement? Old, JsonElement? New, IReadOnlyList<JsonDiffNode> Children);

internal sealed record ParsedJson(JsonDocument? Document, bool Dialect, string? Error);

/// <summary>JSON as structure: strict parsing with a JSONC fallback, and a diff by key and by element identity.</summary>
internal static class JsonModel
{
    private const int MaxDepth = 256;

    internal static ParsedJson Parse(string text)
    {
        try { return new(JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth }), false, null); }
        catch (JsonException strict)
        {
            try { return new(JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }), true, null); }
            catch (JsonException) { return new(null, false, strict.Message); }
        }
    }

    /// <summary>A serialisation that ignores member order and formatting, so equal values compare equal.</summary>
    internal static string Stable(JsonElement value)
    {
        var text = new StringBuilder();
        Write(value);
        return text.ToString();

        void Write(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    text.Append('{');
                    foreach (var member in element.EnumerateObject().OrderBy(member => member.Name, StringComparer.Ordinal))
                    { text.Append(JsonSerializer.Serialize(member.Name)).Append(':'); Write(member.Value); text.Append(','); }
                    text.Append('}');
                    break;
                case JsonValueKind.Array:
                    text.Append('[');
                    foreach (var item in element.EnumerateArray()) { Write(item); text.Append(','); }
                    text.Append(']');
                    break;
                case JsonValueKind.String: text.Append(JsonSerializer.Serialize(element.GetString())); break;
                default: text.Append(element.GetRawText()); break;
            }
        }
    }

    // What makes two array elements the same element: a conventional identifying member, otherwise the whole value.
    private static string Identity(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "id", "key", "name" })
                if (item.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return key + ":" + value.GetRawText();
        return Stable(item);
    }

    internal static JsonDiffNode Diff(string key, JsonElement original, JsonElement modified, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (original.ValueKind == JsonValueKind.Object && modified.ValueKind == JsonValueKind.Object)
        {
            var before = Members(original); var after = Members(modified);
            var children = new List<JsonDiffNode>();
            foreach (var (name, value) in after)
                children.Add(before.TryGetValue(name, out var old) ? Diff(name, old, value, token) : new(name, JsonChange.Added, null, value, []));
            children.AddRange(before.Where(member => !after.ContainsKey(member.Key)).Select(member => new JsonDiffNode(member.Key, JsonChange.Removed, member.Value, null, [])));
            return Container(key, original, modified, children);
        }
        if (original.ValueKind == JsonValueKind.Array && modified.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] before = [.. original.EnumerateArray()], after = [.. modified.EnumerateArray()];
            var children = new List<JsonDiffNode>();
            List<JsonElement> removed = [], added = [];
            int left = 0, right = 0;

            void Flush()
            {
                // As many replacements as removals are the same positions with new values.
                if (removed.Count == added.Count)
                    for (var index = 0; index < removed.Count; index++) children.Add(Diff(Index(right - added.Count + index), removed[index], added[index], token));
                else
                {
                    children.AddRange(removed.Select((item, index) => new JsonDiffNode(Index(left - removed.Count + index), JsonChange.Removed, item, null, [])));
                    children.AddRange(added.Select((item, index) => new JsonDiffNode(Index(right - added.Count + index), JsonChange.Added, null, item, [])));
                }
                removed.Clear(); added.Clear();
            }

            foreach (var (side, _) in MarkdownDiff.Sequence([.. before.Select(Identity)], [.. after.Select(Identity)], token))
            {
                if (side < 0) removed.Add(before[left++]);
                else if (side > 0) added.Add(after[right++]);
                else { Flush(); children.Add(Diff(Index(right), before[left++], after[right++], token)); }
            }
            Flush();
            return Container(key, original, modified, children);
        }
        return new(key, Stable(original) == Stable(modified) ? JsonChange.Unchanged : JsonChange.Changed, original, modified, []);
    }

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    // The last of duplicate members wins, as it does for every JSON reader that keeps one.
    private static Dictionary<string, JsonElement> Members(JsonElement value)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject()) members[member.Name] = member.Value;
        return members;
    }

    private static JsonDiffNode Container(string key, JsonElement original, JsonElement modified, List<JsonDiffNode> children) =>
        new(key, children.Any(child => child.Kind != JsonChange.Unchanged) ? JsonChange.Changed : JsonChange.Unchanged, original, modified, children);

    internal static string Preview(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + value.EnumerateObject().Count().ToString(CultureInfo.InvariantCulture) + "}",
        JsonValueKind.Array => "[" + value.GetArrayLength().ToString(CultureInfo.InvariantCulture) + "]",
        _ => value.GetRawText() is { Length: > 200 } raw ? raw[..200] + "…" : value.GetRawText()
    };

    /// <summary>The changed paths as JSON pointers with their kind, for checks and summaries.</summary>
    internal static IEnumerable<(string Pointer, JsonChange Kind)> Changes(JsonDiffNode node, string pointer = "")
    {
        if (node.Kind == JsonChange.Unchanged) yield break;
        if (node.Children.Count == 0 || node.Kind != JsonChange.Changed) { yield return (pointer, node.Kind); yield break; }
        foreach (var child in node.Children)
            foreach (var change in Changes(child, pointer + "/" + child.Key.Replace("~", "~0").Replace("/", "~1")))
                yield return change;
    }
}

/// <summary>A JSON file as a collapsible tree, and two of them as the tree of what changed.</summary>
internal static class JsonViews
{
    private const int MaxChildren = 2000;

    internal static Control View(ResourceView view)
    {
        var parsed = JsonModel.Parse((view.Content as ResourceContent.Text)?.Value ?? "");
        if (parsed.Document is not { } document) return Notice("JsonInvalid", "Invalid JSON — " + parsed.Error, Ui.Danger);
        var tree = new TreeView { Name = "JsonTree", Margin = new Thickness(12, 8), Background = Brushes.Transparent };
        var root = Item(null, document.RootElement);
        root.IsExpanded = true;
        tree.Items.Add(root);
        return parsed.Dialect ? WithNotice("JsonDialect", "Parsed as JSONC: comments and trailing commas are ignored.", tree) : tree;
    }

    internal static Task<Control?> DiffAsync(ResourceDiff diff, CancellationToken token)
    {
        if (diff.Original is ResourceContent.Bytes || diff.Modified is ResourceContent.Bytes) return Task.FromResult<Control?>(null);
        string original = (diff.Original as ResourceContent.Text)?.Value ?? "", modified = (diff.Modified as ResourceContent.Text)?.Value ?? "";
        return Task.Run<Control?>(async () =>
        {
            var before = diff.Original is ResourceContent.Absent ? null : JsonModel.Parse(original);
            var after = diff.Modified is ResourceContent.Absent ? null : JsonModel.Parse(modified);
            JsonDiffNode? root = null;
            string? invalid = before is { Document: null } ? "The original side is not valid JSON — " + before.Error
                : after is { Document: null } ? "The modified side is not valid JSON — " + after.Error : null;
            if (invalid is null)
                root = before is null ? new("", JsonChange.Added, null, after!.Document!.RootElement, [])
                    : after is null ? new("", JsonChange.Removed, before.Document!.RootElement, null, [])
                    : JsonModel.Diff("", before.Document!.RootElement, after.Document!.RootElement, token);
            return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (invalid is not null) return Notice("JsonInvalid", invalid + ". Use the Source view.", Ui.Danger);
                Control body;
                if (root!.Kind == JsonChange.Unchanged) body = Notice("JsonDiffEmpty", "No structural changes: the two sides differ only in formatting or member order.", Ui.Muted);
                else
                {
                    var tree = new TreeView { Name = "JsonDiff", Margin = new Thickness(12, 8), Background = Brushes.Transparent };
                    tree.Items.Add(DiffItem(root, isRoot: true));
                    body = tree;
                }
                return before?.Dialect == true || after?.Dialect == true ? WithNotice("JsonDialect", "Parsed as JSONC: comments and trailing commas are ignored.", body) : body;
            });
        }, token);
    }

    private static Control Notice(string name, string message, IBrush color)
    {
        var text = Ui.Text(message, color, 12);
        text.Name = name; text.Margin = new Thickness(24); text.TextWrapping = TextWrapping.Wrap;
        text.HorizontalAlignment = HorizontalAlignment.Left; text.VerticalAlignment = VerticalAlignment.Top;
        return text;
    }

    private static Control WithNotice(string name, string message, Control body)
    {
        var panel = new DockPanel();
        var notice = new Border { Background = Ui.InfoWash, Padding = new Thickness(12, 4), Child = Ui.Text(message, Ui.Info, 12) };
        notice.Child.Name = name;
        DockPanel.SetDock(notice, Dock.Top);
        panel.Children.Add(notice); panel.Children.Add(body);
        return panel;
    }

    private static TextBlock Label(string? key, string value, IBrush valueColor, string? prefix = null, IBrush? prefixColor = null)
    {
        var block = new TextBlock { FontFamily = Ui.CodeFont, FontSize = 12, Inlines = [] };
        if (prefix is not null) block.Inlines.Add(new Run(prefix + " ") { Foreground = prefixColor });
        if (key is not null) block.Inlines.Add(new Run(key + ": ") { Foreground = Ui.TextBrush });
        block.Inlines.Add(new Run(value) { Foreground = valueColor });
        return block;
    }

    // Children are built when a node first opens, so a large document costs only what is looked at.
    private static TreeViewItem Item(string? key, JsonElement value)
    {
        var container = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        var item = new TreeViewItem { Header = Label(key, JsonModel.Preview(value), container ? Ui.Muted : Ui.Accent) };
        if (!container || JsonModel.Preview(value) is "{0}" or "[0]") return item;
        item.Items.Add(new TreeViewItem());
        var built = false;
        item.PropertyChanged += (_, change) =>
        {
            if (change.Property != TreeViewItem.IsExpandedProperty || !item.IsExpanded || built) return;
            built = true;
            item.Items.Clear();
            var children = value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().Select(member => (Key: member.Name, member.Value))
                : value.EnumerateArray().Select((element, index) => (Key: index.ToString(CultureInfo.InvariantCulture), Value: element));
            var count = 0;
            foreach (var (name, child) in children)
            {
                if (count++ == MaxChildren) { item.Items.Add(new TreeViewItem { Header = Ui.Text("… more members are shown in Source", Ui.Hint, 12) }); break; }
                item.Items.Add(Item(name, child));
            }
        };
        return item;
    }

    private static TreeViewItem DiffItem(JsonDiffNode node, bool isRoot = false)
    {
        var key = isRoot ? null : node.Key;
        var item = node.Kind switch
        {
            JsonChange.Added => new TreeViewItem { Header = Label(key, JsonModel.Preview(node.New!.Value), Ui.Success, "+", Ui.Success) },
            JsonChange.Removed => new TreeViewItem { Header = Label(key, JsonModel.Preview(node.Old!.Value), Ui.Danger, "−", Ui.Danger) },
            _ when node.Children.Count == 0 => new TreeViewItem { Header = Leaf(key, node) },
            _ => new TreeViewItem { Header = Label(key, JsonModel.Preview(node.New!.Value), Ui.Muted, "~", Ui.Warning), IsExpanded = true }
        };
        item.Classes.Add("json-" + node.Kind.ToString().ToLowerInvariant());
        if (node.Kind is JsonChange.Added or JsonChange.Removed)
        {
            // The whole added or removed value can still be browsed.
            var value = (node.New ?? node.Old)!.Value;
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                foreach (var child in Children(Item(null, value))) item.Items.Add(child);
            return item;
        }
        var unchanged = 0;
        foreach (var child in node.Children)
            if (child.Kind == JsonChange.Unchanged) unchanged++;
            else item.Items.Add(DiffItem(child));
        if (unchanged > 0 && node.Children.Count > 0)
            item.Items.Add(new TreeViewItem { Header = Ui.Text($"… {unchanged} unchanged", Ui.Hint, 12) });
        return item;
    }

    // Opening a node builds its children, which then move under the diff's own node.
    private static List<TreeViewItem> Children(TreeViewItem item)
    {
        item.IsExpanded = true;
        var children = item.Items.OfType<TreeViewItem>().ToList();
        item.Items.Clear();
        return children;
    }

    private static TextBlock Leaf(string? key, JsonDiffNode node)
    {
        var block = Label(key, JsonModel.Preview(node.Old!.Value), Ui.Danger, "~", Ui.Warning);
        ((Run)block.Inlines![^1]).TextDecorations = TextDecorations.Strikethrough;
        block.Inlines.Add(new Run(" → ") { Foreground = Ui.Muted });
        block.Inlines.Add(new Run(JsonModel.Preview(node.New!.Value)) { Foreground = Ui.Success });
        return block;
    }
}