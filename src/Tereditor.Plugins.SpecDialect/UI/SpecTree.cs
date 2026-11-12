using System.Text.RegularExpressions;

namespace SharpRail.Plugins.SpecDialect;

public sealed record SpecTreeNode(SpecGraphNode Node, IReadOnlyList<SpecTreeNode> Children);

/// <summary>The Specs panel's presentation rules: the parent tree, role labels and tags, and display titles.</summary>
public static partial class SpecTree
{
    // The Markdown preview recognises a spec by its frontmatter alone and keeps its own copy of these roles in core.
    private static readonly Dictionary<string, (string Label, string Tag)> Roles = new(StringComparer.Ordinal)
    {
        ["goal-and-requirements"] = ("Goal", "GOAL"),
        ["architecture-design"] = ("Architecture", "ARCH"),
        ["module-design"] = ("Module", "MODULE"),
        ["submodule-design"] = ("Submodule", "SUBMODULE"),
        ["task-spec"] = ("Task", "TASK"),
    };

    public static string RoleLabel(string type)
    {
        if (Roles.TryGetValue(type, out var role)) return role.Label;
        var words = Separators().Split(type).Where(word => word.Length > 0).ToArray();
        return words.Length == 0 ? "Spec" : string.Join(' ', words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    public static string RoleTag(string type) => Roles.TryGetValue(type, out var role) ? role.Tag : RoleLabel(type).ToUpperInvariant();

    /// <summary>Collapses a spaced em or en dash separator to a middle dot.</summary>
    public static string DisplayTitle(string title) => Dash().Replace(title, " · ");

    /// <summary>
    /// Roots are nodes with no, a dangling or a self parent; roots and siblings sort by title. The visited guard
    /// prevents repeated descendants. Nodes in a parent cycle have no root and are reported by spec_validate.
    /// </summary>
    public static IReadOnlyList<SpecTreeNode> Build(IReadOnlyList<SpecGraphNode> nodes)
    {
        var ids = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        bool Nested(SpecGraphNode node) => node.Parent is { } parent && parent != node.Id && ids.Contains(parent);
        var children = nodes.Where(Nested).GroupBy(node => node.Parent!).ToDictionary(group => group.Key,
            group => group.OrderBy(node => node.Title, StringComparer.CurrentCulture).ToArray());
        var visited = new HashSet<string>(StringComparer.Ordinal);
        SpecTreeNode Materialize(SpecGraphNode node)
        {
            visited.Add(node.Id);
            var below = children.TryGetValue(node.Id, out var direct)
                ? direct.Where(child => !visited.Contains(child.Id)).Select(Materialize).ToArray()
                : [];
            return new(node, below);
        }
        return nodes.Where(node => !Nested(node)).OrderBy(node => node.Title, StringComparer.CurrentCulture).Select(Materialize).ToArray();
    }

    [GeneratedRegex(@"[-_\s]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+[—–]\s+")]
    private static partial Regex Dash();
}