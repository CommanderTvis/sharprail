using System.Text.RegularExpressions;

using SharpRail.Plugins.UI.Kit.Visualization;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

// The Visualize pane's diagrams: a tree view starts at the source's first node.
internal static class VisualizeSelectionChecks
{
    private const string Tree = "treeView-beta\n    \"SharpRail tasks/\"\n        \"In progress/\"\n            \"Some item\"\n";

    internal static void Run()
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP visualize selection: Mermaid rendering requires macOS"); return; }
        TreeRoot();
    }

    private static void Flowchart()
    {
        var rendered = MermaidRenderer.Render("flowchart LR\n    Start[Begin here] --> Finish[All done]\n", MermaidRenderer.Options());
        using var svg = Avalonia.Svg.Skia.SvgSource.LoadFromSvg(rendered.Svg!);
        var text = DiagramText.From(svg.Svg?.Model, outline: false);
        var all = text.Text(0, text.Length);
        Require(all == "Begin here All done", $"A flowchart's node labels read left to right on one line, got '{all}'.");
        Console.WriteLine("PASS flowchart labels are read as selectable text");
    }

    private static void TreeRoot()
    {
        var rendered = MermaidRenderer.Render(Tree, MermaidRenderer.Options());
        Require(rendered.Svg is not null, "The tree view renders: " + rendered.Error);
        var labels = Regex.Matches(rendered.Svg!, "<text[^>]*>([^<]*)</text>").Select(match => match.Groups[1].Value).ToArray();
        Require(labels.SequenceEqual(["SharpRail tasks/", "In progress/", "Some item"]),
            "The tree view's first row is the source's first node, with no implicit \"/\" root: " + string.Join(" | ", labels));
        var annotated = MermaidRenderer.Render("treeView-beta\n    \"a\" :::highlight\n", MermaidRenderer.Options());
        Require(annotated.Error is { Length: > 0 }, "Merman 0.7 has no tree view annotations; the parser error reaches the caller instead of a wrong drawing.");
        Console.WriteLine("PASS tree view starts at the source's first node");
    }
}