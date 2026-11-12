using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintProperties : UserControl
{
    private readonly Action<string> edit;
    private string content = "";

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
        var rows = this.FindControl<ContentControl>("Rows")!;
        var block = Frontmatter.Parse(content);
        if (block is null) { rows.Content = null; return; }
        if (!block.Readable) { rows.Content = MarkdownPreview.Code(block.Raw); return; }
        // Our own edit echoes back as new content; keep the mounted editor (and its focus) when it already shows it.
        if (rows.Content is FrontmatterEditor editor && editor.Properties.SequenceEqual(block.Properties, PropertyComparer.Instance)) return;
        rows.Content = new FrontmatterEditor(block, Commit);
    }

    private sealed class PropertyComparer : IEqualityComparer<FrontmatterProperty>
    {
        public static readonly PropertyComparer Instance = new();
        public bool Equals(FrontmatterProperty? left, FrontmatterProperty? right) => left is not null && right is not null && left.Key == right.Key
            && left.Text == right.Text && (left.Items ?? []).SequenceEqual(right.Items ?? []) && (left.Entries ?? []).SequenceEqual(right.Entries ?? [])
            && (left.Items is null) == (right.Items is null) && (left.Entries is null) == (right.Entries is null);
        public int GetHashCode(FrontmatterProperty property) => property.Key.GetHashCode(StringComparison.Ordinal);
    }

    private void Commit(IReadOnlyList<FrontmatterProperty> next)
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