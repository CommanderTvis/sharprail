using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit.Visualization;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

// The Visualize pane's diagrams are pictures; these checks hold that their text still selects and copies like a
// document's, that a tree view starts at the source's first node, and that comparison cards are selectable text.
internal static class VisualizeSelectionChecks
{
    private const string Tree = "treeView-beta\n    \"SharpRail tasks/\"\n        \"In progress/\"\n            \"Some item\"\n";

    internal static void Run()
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP visualize selection: Mermaid rendering requires macOS"); return; }
        TreeRoot();
        Selection();
        Flowchart();
        Comparison();
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

    private static void Selection()
    {
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        var view = new MermaidView(Tree, interactive: true);
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        Until(() => view.GetVisualDescendants().OfType<DiagramSelection>().Any());
        Settle();
        var pane = view.GetVisualDescendants().OfType<PanZoomView>().Single();
        var overlay = view.GetVisualDescendants().OfType<DiagramSelection>().Single();
        var text = pane.Text;
        Require(string.Join("|", text.Runs.Select(run => run.Text)) == "SharpRail tasks/|In progress/|Some item",
            "The diagram's drawn labels are read in reading order: " + string.Join("|", text.Runs.Select(run => run.Text)));

        // Drag across one label at a zoom other than 100%, so the hit test must follow the displayed scale.
        foreach (var _ in Enumerable.Range(0, 2)) Click(view, "MermaidZoomIn");
        Settle();
        var label = text.Runs.Single(run => run.Text == "In progress/");
        Point At(Point picture)
        {
            var bounds = overlay.Bounds;
            var cull = pane.Picture;
            var local = new Point((picture.X - cull.X) * bounds.Width / cull.Width, (picture.Y - cull.Y) * bounds.Height / cull.Height);
            return overlay.TranslatePoint(local, window)!.Value;
        }
        var start = At(new Point(label.Boxes[0].Left + 0.5, label.Bounds.Center.Y));
        var end = At(new Point(label.Boxes[^1].Right - 0.5, label.Bounds.Center.Y));
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Require(pane.SelectedText == "In progress/", $"Dragging over a label selects exactly it, got '{pane.SelectedText}'.");
        window.KeyPress(Key.C, command, PhysicalKey.C, null);
        Dispatcher.UIThread.RunJobs();
        Require(Clipboard(window) == "In progress/", $"Copy puts the selected label on the clipboard, got '{Clipboard(window)}'.");

        window.KeyPress(Key.A, command, PhysicalKey.A, null);
        window.KeyPress(Key.C, command, PhysicalKey.C, null);
        Dispatcher.UIThread.RunJobs();
        const string outline = "SharpRail tasks/\n    In progress/\n        Some item";
        Require(Clipboard(window) == outline, $"Select All and Copy take every label in order, as an indented outline, got '{Clipboard(window)}'.");

        // A press beside the labels pans and clears the selection rather than selecting.
        var blank = At(new Point(text.Runs[1].Bounds.Left / 2, text.Runs[1].Bounds.Center.Y));
        window.MouseDown(blank, MouseButton.Left);
        window.MouseUp(blank, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Require(pane.SelectedText.Length == 0, $"A click beside the labels clears the selection, got '{pane.SelectedText}' at {blank}.");

        // The context menu also copies the Mermaid source the diagram was drawn from.
        var viewer = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "MermaidPanZoom");
        viewer.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        var menu = viewer.ContextMenu!;
        var source = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy Mermaid Source"));
        source.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        menu.Close();
        Require(Clipboard(window) == Tree, $"Copy Mermaid Source copies the source, got '{Clipboard(window)}'.");

        window.Close();
        Console.WriteLine("PASS diagram text selects by drag, follows zoom, copies, selects all as an outline and copies its source");
    }

    private static void Comparison()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"type":"comparison","title":"Pick","options":[{"name":"OptA","description":"First path","pros":["Fast"],"cons":["Costly"]}]}""");
        var card = SharpRail.Plugins.UI.Kit.Visualization.VisualizationCard.Create(document.RootElement, interactive: true);
        var window = new Window { Width = 600, Height = 400, Content = card };
        window.Show();
        Settle();
        var selectable = card.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(block => block.Text).ToHashSet();
        foreach (var expected in new[] { "Pick", "OptA", "First path", "Fast", "Costly" })
            Require(selectable.Contains(expected), $"The comparison's \"{expected}\" is selectable text: {string.Join(" | ", selectable)}");
        window.Close();
        Console.WriteLine("PASS comparison cards are selectable text");
    }

    private static void Click(Control scope, string name)
    {
        var button = scope.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }


    private static string? Clipboard(Window window) => window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult();
}