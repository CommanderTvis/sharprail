using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;

namespace SharpRail.Checks;

internal static class DrawnRenderingChecks
{
    internal static void Run()
    {
        Markdown();
        MarkdownSelectionChecks.Run();
        Trees();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
    }

    private static void Markdown()
    {
        var source = string.Join("\n\n", Enumerable.Range(0, 600).Select(index => $"## Section {index}\n\nParagraph {index} with a [link](target.md) and **bold words**."));
        var context = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 78, true);
        using var preview = new MarkdownPreview(source, "README.md", context);
        var find = new FindBar(() => preview);
        var grid = new Grid(); grid.Children.Add(preview); grid.Children.Add(find);
        var window = new Window { Width = 800, Height = 600, Content = grid, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window);
            Require(preview.GetVisualDescendants().OfType<SelectableTextBlock>().Count() is > 0 and < 50,
                "A large Markdown preview realizes only paragraphs near the viewport.");
            var first = preview.GetVisualDescendants().OfType<SelectableTextBlock>().First();
            first.Focus();
            window.KeyPress(Key.A, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.A, null);
            Require(preview.SelectedText.Contains("Paragraph 0 with a link", StringComparison.Ordinal) &&
                preview.SelectedText.Contains("Paragraph 599 with a link", StringComparison.Ordinal) &&
                !preview.SelectedText.Contains('￼') && preview.SelectedLines is { Start: 1, End: > 1000 },
                "Select All includes offscreen text and link labels, with the complete source-line span.");
            preview.ScrollToAnchor("section-599"); Frame(window);
            Require(preview.Offset.Y > 0 && preview.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Inlines?.Text?.Contains("Section 599", StringComparison.Ordinal) == true),
                $"An offscreen heading anchor is realized: offset={preview.Offset}, visible={string.Join('|', preview.GetVisualDescendants().OfType<SelectableTextBlock>().Select(text => text.Inlines?.Text))}.");
            Require(preview.SelectedText.Contains("Paragraph 0 with a link", StringComparison.Ordinal), "Scrolling keeps document-wide selection.");
            find.Open();
            var input = find.GetLogicalDescendants().OfType<TextBox>().Single();
            input.Text = "Paragraph 0"; Frame(window);
            Require(!find.Missed && preview.Offset.Y < 200 && preview.TextBlocks().Any(text => text.SelectedText == "Paragraph 0"),
                "Find searches the whole document and brings an offscreen first match back into view.");
            input.Text = "Paragraph 599"; Frame(window);
            Require(!find.Missed && preview.Offset.Y > 1000 && preview.TextBlocks().Any(text => text.SelectedText == "Paragraph 599"),
                "Find also realizes a match at the end of a virtual document.");
            Require(preview.GetVisualDescendants().OfType<SelectableTextBlock>().Count() < 50, "Anchor and find navigation do not accumulate mounted Markdown blocks.");
        }
        finally { window.Close(); }
        var before = string.Join("\n\n", Enumerable.Range(0, 160).Select(index => $"Paragraph {index} old content."));
        var merged = MarkdownDiff.Merge(before, before.Replace("old", "new", StringComparison.Ordinal));
        var document = MarkdownPreview.Parse(merged);
        using var diff = new MarkdownPreview(document, "README.md", context, renderDiagrams: false,
            focus: DiffFocus.Create(before, merged, document, new HashSet<string>()));
        window = new Window { Width = 800, Height = 600, Content = diff, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window);
            Require(diff.GetVisualDescendants().OfType<SelectableTextBlock>().Count() is > 0 and < 50,
                "A large rendered Markdown diff uses the same viewport layout.");
            var marks = diff.GetLogicalDescendants().OfType<SelectableTextBlock>().SelectMany(text => text.Inlines?.OfType<Avalonia.Controls.Documents.Run>() ?? []).ToArray();
            Require(marks.Any(run => run.Classes.Contains("ins")) && marks.Any(run => run.Classes.Contains("del")), "Virtualized diffs retain both change marks.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS virtual Markdown preview/diff: bounded mounted blocks, offscreen selection, source spans, links, find and anchors");
    }

    internal static void Trees()
    {
        var activated = "";
        var menuTarget = "";
        var iconsCreated = 0;
        var tree = new DrawnTree();
        var children = Enumerable.Range(0, 2000).Select(index =>
        {
            var id = "entry-" + index;
            return new DrawnTreeEntry(id, "Row " + index)
            {
                IconKey = _ => "shared-file-icon",
                Icon = _ => { iconsCreated++; return Ui.Icon("file", null, 14); },
                Activate = _ => activated = id,
                Menu = () => { menuTarget = id; return new ContextMenu { Items = { new MenuItem { Header = id } } }; }
            };
        }).ToArray();
        var root = new DrawnTreeEntry("root", "Root") { CanExpand = true, Expanded = true, Children = children };
        tree.SetRoots([root]);
        var window = new Window { Width = 360, Height = 500, Content = tree, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window);
            Require(tree.Rows.Count == 2001 && tree.GetVisualDescendants().OfType<DrawnTreeLabel>().Count() < 60, "Expanded trees keep their children as data and realize only viewport rows.");
            Require(ControlAutomationPeer.CreatePeerForElement(tree)!.GetAutomationControlType() == AutomationControlType.Tree, "The virtual list retains tree accessibility semantics.");
            var scroll = tree.GetVisualDescendants().OfType<ScrollViewer>().Single();
            for (var step = 1; step <= 30; step++)
            {
                scroll.Offset = new Vector(0, step * 96);
                Frame(window);
            }
            Require(iconsCreated < 60, $"Scrolling same-type files reuses viewport icons instead of rebuilding them for every filename: {iconsCreated} icons.");
            var reused = tree.GetVisualDescendants().OfType<DrawnTreeLabel>().First(label => label.Entry != root);
            var oldIcon = reused.Child;
            reused.Entry.IconKey = _ => "changed-file-icon";
            tree.RefreshRows();
            Require(!ReferenceEquals(reused.Child, oldIcon), "A changed provider answer replaces a recycled row's icon.");
            oldIcon = reused.Child;
            tree.RefreshRows(forceIcons: true);
            Require(!ReferenceEquals(reused.Child, oldIcon), "Plugin invalidation refreshes icons even when their keys match.");
            tree.Reveal(root); Frame(window);
            var rootContainer = tree.ContainerFromItem(root)!;
            var peer = ControlAutomationPeer.CreatePeerForElement(rootContainer)!;
            Require(peer.GetAutomationControlType() == AutomationControlType.TreeItem && peer.GetName() == "Root", "Drawn rows retain their accessible name and tree-item identity.");
            var expand = peer.GetProvider<IExpandCollapseProvider>()!;
            expand.Collapse(); Frame(window);
            Require(tree.Rows.Count == 1 && expand.ExpandCollapseState == ExpandCollapseState.Collapsed, "Accessibility can collapse a branch without drawing its descendants.");
            expand.Expand(); Frame(window);
            tree.Reveal(children[^1]); Frame(window);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Require(activated == children[^1].Id, $"Keyboard activation uses the row reached after scrolling and recycling: activated={activated}, selected={(tree.SelectedItem as DrawnTreeEntry)?.Id}, focused={window.FocusManager?.GetFocusedElement()?.GetType().Name}, container={tree.ContainerFromItem(children[^1])?.GetType().Name}.");
            var row = E2eWorkspace.TreeRow(tree, children[^1].Id);
            var point = row.TranslatePoint(new Point(70, 12), window)!.Value;
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Frame(window);
            Require(menuTarget == children[^1].Id && row.ContextMenu?.IsOpen == true, "A recycled row opens the current entry's menu.");
            row.ContextMenu!.Close();
            tree.Reveal(children[^1]);
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null); Frame(window);
            Require(ReferenceEquals(tree.SelectedItem, root), "Left navigates from a deeply scrolled child to its parent.");
            Require(tree.GetVisualDescendants().OfType<DrawnTreeLabel>().Count() < 60, "Tree navigation does not grow the control window.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS drawn trees: bounded rows, recycling, keyboard navigation, current-row menus and accessible expansion");
    }
}