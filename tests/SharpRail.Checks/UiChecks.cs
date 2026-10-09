using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Markdig.Extensions.Tables;
using Markdig.Syntax;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class UiChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> complete, string message)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (!complete() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
        Require(complete(), message);
    }

    private static void Await(Task task)
    {
        Pump(() => task.IsCompleted, "UI operation timed out.");
        task.GetAwaiter().GetResult();
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetLogicalDescendants().OfType<T>().Distinct().Single(item => item.Name == name);

    private static Point Center(Window window, Control control)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        if (!control.GetVisualAncestors().Contains(window) && control.Name is not null)
            control = Find<Control>(window, control.Name);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Require(control.Bounds.Width > 0 && control.Bounds.Height > 0, "Click target has not been arranged.");
        // Fixtures created under the project root earlier can push a row below the visible tree.
        control.BringIntoView();
        window.UpdateLayout();
        var point = Center(window, control);
        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    internal static void RunWorkspaceSuites(string root)
    {
        E2E.ProjectsE2E.Run(root);
        E2E.ProjectPickerE2E.Run(root);
        E2E.NewWorkspaceShortcutE2E.Run(root);
        E2E.WorkspaceActionsE2E.Run(root);
        E2E.WelcomeE2E.Run(root);
        E2E.DefaultWorkspaceE2E.Run(root);
        E2E.WorkspaceLifecycleE2E.Run(root);
        E2E.ExternalWorkspaceE2E.Run(root);
        E2E.NewWorkspaceE2E.Run(root);
        E2E.ReloadNavigationE2E.Run(root);
        E2E.FilesE2E.Run(root);
        E2E.ProjectContextE2E.Run(root);
        E2E.MultiClientE2E.Run(root);
        E2E.ReconnectE2E.Run(root);
    }

    public static void Run(string root)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
        EditorChecks.Run();
        EditorTextChecks.Run();
        EditorWorkbenchChecks.Run(root);
        foreach (var weight in new[] { Ui.InterfaceWeight, FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold })
            Require(FontManager.Current.TryGetGlyphTypeface(new Typeface(Ui.InterfaceFont, weight: weight), out var face) && face.Weight == weight,
                $"Bundled Geist face {weight} fell back to a different weight.");
        var store = new ProfileStore(Path.Combine(root, ".profile"));
        var host = new LocalProjectAdapter(new ProjectServices(root));
        SelectionChecks.Run(host);
        E2E.TerminalChromeChecks.Run(Path.Combine(root, "upstream-e2e"));
        E2E.LayoutSettingsE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.ThemeE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.TopbarChromeE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.ChangesE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.ChangesScopeE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.ChangesDiffE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.RenderedDiffE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.LiveRefreshE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.WorkspaceTabsE2E.Run(Path.Combine(root, "upstream-e2e"));
        RunWorkspaceSuites(Path.Combine(root, "upstream-e2e"));
        E2E.SettingsGitHubE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.LineWidthE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.TerminalRemountE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.TerminalsE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.BottomPanelE2E.Run(Path.Combine(root, "upstream-e2e"));
        GhosttySkiaChecks.Run(root);
        E2E.PreviewTabsE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.MarkdownLinksE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.MarkdownAlertsE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.MarkdownMermaidE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.EditorE2E.Run(Path.Combine(root, "upstream-e2e"));
        E2E.LayoutE2E.Run(Path.Combine(root, "upstream-e2e"));
        using (var frontmatterPreview = new MarkdownPreview("---\nid: private-metadata\ntitle: Internal title\n---\n\n# Visible heading\n\nVisible paragraph.",
            "metadata.md", host, store.Data.Preferences, (_, _) => { }))
        {
            var visibleText = frontmatterPreview.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(text => text.Text ??
                string.Concat(text.Inlines?.OfType<Run>().Select(run => run.Text) ?? []));
            var renderedText = string.Join("\n", visibleText);
            Require(renderedText.Contains("Visible heading", StringComparison.Ordinal) && renderedText.Contains("Visible paragraph.", StringComparison.Ordinal) &&
                ((StackPanel)frontmatterPreview.Content!).Children.Count == 3 &&
                ((StackPanel)frontmatterPreview.Content!).Children[0] is Border { Name: "MarkdownFrontmatter" } metadata &&
                string.Concat(metadata.GetLogicalDescendants().OfType<SelectableTextBlock>().Single().Inlines!.OfType<Run>().Select(run => run.Text)) ==
                    "id: private-metadata\ntitle: Internal title",
                "Markdown frontmatter must render first as a code block, not as headings or stray paragraphs.");
        }
        foreach (var size in new[] { 14d, 24d })
        {
            using var linkPreview = new MarkdownPreview("Before [linked text](#anchor) after.", "link.md", host,
                new Preferences { FontSize = size }, (_, _) => { });
            var linkWindow = new Window { Width = 800, Height = 200, FontFamily = Ui.InterfaceFont, Content = linkPreview };
            linkWindow.Show(); Dispatcher.UIThread.RunJobs(); linkWindow.UpdateLayout();
            var paragraph = linkPreview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
            var link = (Button)paragraph.Inlines!.OfType<InlineUIContainer>().Single().Child;
            var label = (TextBlock)link.Content!;
            var baseline = label.TranslatePoint(default, paragraph)!.Value.Y + label.TextLayout.TextLines[0].Baseline;
            Require(Math.Abs(baseline - paragraph.TextLayout.TextLines[0].Baseline) < 1,
                $"Markdown link baseline differs from surrounding text at font size {size}: {baseline}/{paragraph.TextLayout.TextLines[0].Baseline}.");
            linkWindow.Close();
        }
        foreach (var (size, prefix) in new[] { (14d, "- "), (24d, "- "), (14d, "1. "), (24d, "1. ") })
        {
            using var listPreview = new MarkdownPreview(prefix + string.Join(' ', Enumerable.Repeat("wrapped list content", 12)),
                "list.md", host, new Preferences { FontSize = size }, (_, _) => { });
            var listWindow = new Window { Width = 360, Height = 250, Content = listPreview };
            listWindow.Show(); Dispatcher.UIThread.RunJobs(); listWindow.UpdateLayout();
            var item = (Grid)((StackPanel)((StackPanel)listPreview.Content!).Children.Single()).Children.Single();
            var marker = (TextBlock)item.Children[0];
            var itemText = ((StackPanel)item.Children[1]).Children.OfType<SelectableTextBlock>().Single();
            Require(itemText.Bounds.Height > itemText.LineHeight * 2 &&
                Math.Abs(marker.TranslatePoint(default, item)!.Value.Y - itemText.TranslatePoint(default, item)!.Value.Y) < 1 &&
                marker.FontSize == itemText.FontSize,
                "Wrapped Markdown list marker does not align with the first text line.");
            listWindow.Close();
        }
        using (var scrollingPreview = new MarkdownPreview(string.Join("\n\n", Enumerable.Range(0, 40).Select(index => $"Paragraph {index}: scrollable Markdown content.")),
            "README.md", host, store.Data.Preferences, (_, _) => { }))
        {
            var scrollingWindow = new Window { Width = 500, Height = 250, Content = scrollingPreview };
            scrollingWindow.Show(); Dispatcher.UIThread.RunJobs(); scrollingWindow.UpdateLayout();
            scrollingWindow.MouseWheel(Center(scrollingWindow, scrollingPreview), new Vector(0, -3));
            Dispatcher.UIThread.RunJobs(); scrollingWindow.UpdateLayout();
            Require(scrollingPreview.Extent.Height > scrollingPreview.Viewport.Height && scrollingPreview.Offset.Y > 0,
                "Mouse-wheel input did not scroll the Markdown preview.");
            scrollingWindow.Close();
        }
        var window = new WorkbenchWindow(host, root, store, E2E.E2eTerminals.Plain);
        window.Width = 1352; window.Height = 848;
        window.Show();
        Pump(() => window.WorkspaceMounted, "Workspace did not mount.");
        var specs = Find<TreeView>(window, "SpecsTree");
        Pump(() => specs.Items.Count > 0 && specs.Items[0] is TreeViewItem { Items.Count: > 0 }, "Spec hierarchy did not render.");
        {
            var panel = (Control)specs.GetVisualParent()!;
            var rootItem = (TreeViewItem)specs.Items[0]!;
            var child = (TreeViewItem)rootItem.Items[0]!;
            double X(Visual visual) => visual.TranslatePoint(default, panel)!.Value.X;
            var chevron = rootItem.GetVisualDescendants().OfType<ToggleButton>().First();
            // The reference row: 4px inset, a 20px chevron slot, then the icon; each level indents 12px.
            Require(Math.Abs(X(chevron) - 9) <= 1 && Math.Abs(X((Visual)rootItem.Header!) - 25) <= 1 && X((Visual)child.Header!) - X((Visual)rootItem.Header!) == 12,
                $"Spec tree indentation differs from the reference: chevron={X(chevron)} icon={X((Visual)rootItem.Header!)} child={X((Visual)child.Header!)}.");
        }
        var controls = window.GetLogicalDescendants().OfType<Control>().Distinct().ToArray();
        Require(controls.Length > 100, "UI was flattened.");
        Require(Find<Control>(window, "ProjectsPanel").Bounds.Width > 200, "Projects geometry differs.");
        Require(Find<Control>(window, "ChangesPanel").Bounds.Width > 300, "Changes geometry differs.");
        var projectTab = Find<Button>(window, "Tab_projects");
        var projectLabel = projectTab.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "Projects");
        Require(projectLabel.TranslatePoint(default, projectTab)!.Value.X == 26 && projectLabel.FontWeight == Ui.InterfaceWeight,
            "Tab icon spacing or text weight differs from the reference.");
        {
            var pressPoint = projectTab.TranslatePoint(new Point(projectTab.Bounds.Width / 2, projectTab.Bounds.Height / 2), window)!.Value;
            window.MouseDown(pressPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            var presenter = projectTab.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");
            var pressed = projectTab.Classes.Contains(":pressed");
            var tinted = presenter.Background is ISolidColorBrush { Color.A: > 0 };
            var scaled = projectTab.RenderTransform is { Value.IsIdentity: false };
            window.MouseUp(pressPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Require(pressed && !tinted && !scaled, $"Holding a tab must neither tint nor shrink it: pressed={pressed} background={presenter.Background} transform={projectTab.RenderTransform}.");
        }
        Require(Find<Grid>(window, "ProjectRow").Bounds.Height == 28,
            "Project row does not match the reference height.");
        Click(window, Find<Button>(window, "ProjectExpand"));
        Require(!window.GetLogicalDescendants().OfType<Button>().Any(button => button.ContextMenu is not null && Equals(ToolTip.GetTip(button), root)),
            "Collapsing a project did not hide its workspaces.");
        Require(Find<Button>(window, "ProjectExpand").IsFocused, "Project collapse lost keyboard focus.");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Dispatcher.UIThread.RunJobs();
        Require(window.GetLogicalDescendants().OfType<Button>().Any(button => button.ContextMenu is not null && Equals(ToolTip.GetTip(button), root)),
            "Keyboard project expansion did not restore its workspaces.");
        Capture(window, ".bench/prototype-empty-headless.png");
        var specNode = (TreeViewItem)specs.Items[0]!;
        var specHeader = (Grid)specNode.Header!;
        var childHeader = (Grid)((TreeViewItem)specNode.Items[0]!).Header!;
        Require(childHeader.Children.OfType<TextBlock>().Single(text => text.Name != "SpecRole").Text == "Architecture · components",
            "Spec titles must compact spaced em dashes to middle dots.");
        var specRole = specHeader.Children.OfType<TextBlock>().Single(text => text.Name == "SpecRole");
        Require(!specRole.IsVisible && specRole.Text == "PRODUCT GOAL", "Spec type must initially be hidden.");
        Click(window, Find<Button>(window, "Tab_specs")); window.UpdateLayout();
        window.MouseMove(Center(window, specHeader)); Dispatcher.UIThread.RunJobs();
        Require(specRole.IsVisible, "Spec type must be shown while hovering the row.");
        Click(window, specHeader.Children.OfType<TextBlock>().Single(text => text.Name != "SpecRole"));
        Pump(() => window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs)
            .Any(tab => tab.Path == "SPEC.md" && tab.Preview), "Single-clicking a spec did not preview its Markdown.");

        var filesPeer = ControlAutomationPeer.CreatePeerForElement(Find<Button>(window, "Tab_files"))!;
        Require(filesPeer.GetAutomationControlType() == AutomationControlType.TabItem && filesPeer.GetName() == "Files",
            "Tool tab lacks accessible identity.");
        var filesSelection = (ISelectionItemProvider)filesPeer;
        Require(!filesSelection.IsSelected && !filesSelection.SelectionContainer!.CanSelectMultiple &&
            filesSelection.SelectionContainer.IsSelectionRequired, "Tab selection contract differs from the pane.");
        filesSelection.Select();
        Dispatcher.UIThread.RunJobs();
        filesPeer = ControlAutomationPeer.CreatePeerForElement(Find<Button>(window, "Tab_files"))!;
        filesSelection = (ISelectionItemProvider)filesPeer;
        Require(filesSelection.IsSelected && filesSelection.SelectionContainer!.GetSelection().Single() == filesPeer,
            "Accessible selection did not update canonical tab state.");
        Click(window, Find<Button>(window, "Tab_files"));
        Require(window.Layout.State.Groups.Any(group => window.Layout.Selected(group.Id)?.Id == "files"), "Tab click failed.");
        Require(Find<TreeView>(window, "FilesTree").Items.Count >= 3, "File tree is empty.");
        var readmeNode = Find<TreeView>(window, "FilesTree").Items.OfType<TreeViewItem>()
            .Single(item => item.Tag is SharpRail.Host.Abstractions.ProjectFile { Path: "README.md" });
        Click(window, ((Grid)readmeNode.Header!).Children.OfType<TextBlock>().Single());
        var selectedFileRow = readmeNode.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot");
        Require(selectedFileRow.Bounds.Height == 24 && selectedFileRow.Background is SolidColorBrush selectedFill && selectedFill.Color == Ui.Hover.Color,
            $"Selected file row differs from the reference's 24px height and muted fill ({selectedFileRow.Bounds.Height}px, {(selectedFileRow.Background as SolidColorBrush)?.Color}).");
        Pump(() => window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs)
            .Any(tab => tab.Path == "README.md" && tab.Preview), "File-tree click did not open its preview.");
        var primary = window.Layout.State.Center.Leaves().Single();
        Require(window.Layout.Selected(primary)?.Preview == true, "File preview slot failed.");
        var previewTab = Find<Button>(window, "Tab_markdown_README.md");
        var previewTitle = previewTab.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "README.md");
        Require(previewTitle.FontSize == 14 && previewTitle.FontStyle == FontStyle.Italic && previewTitle.Padding.Right >= 3 &&
            previewTitle.Bounds.Height < previewTab.Bounds.Height,
            "Italic preview label does not fit within its fixed-height tab.");
        var preview = Find<MarkdownPreview>(window, "MarkdownPreview");
        Require(preview.Document.Any(block => block is HeadingBlock) && preview.Document.Any(block => block is Table), "Markdown blocks were not parsed.");
        Require(preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(), "Markdown text is not selectable.");
        var markdownBody = (StackPanel)preview.Content!;
        var headingFrame = (Border)markdownBody.Children[0];
        var headingText = (SelectableTextBlock)headingFrame.Child!;
        var paragraphText = (SelectableTextBlock)markdownBody.Children[1];
        Require(headingText.FontSize == 24 && headingText.LineHeight == 30 &&
            headingFrame.Margin.Bottom == 12 && paragraphText.Margin.Top == 0 &&
            Math.Abs(paragraphText.LineHeight - store.Data.Preferences.FontSize * 1.6) < .001,
            "Markdown heading typography or adjacent block spacing differs from the reference.");
        var tabPoint = Center(window, Find<Button>(window, "Tab_markdown_README.md"));
        for (var click = 0; click < 2; click++)
        {
            window.MouseDown(tabPoint, MouseButton.Right); window.MouseUp(tabPoint, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Find<Button>(window, "Tab_markdown_README.md").ContextMenu!.Close();
        }
        Require(window.Layout.Selected(primary)?.Preview == true,
            "Double right-clicking a preview must open its menu without keeping it.");
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(Application.Current!.PlatformSettings!.GetDoubleTapTime(PointerType.Mouse) + TimeSpan.FromMilliseconds(10));
        window.MouseDown(tabPoint, MouseButton.Left); window.MouseUp(tabPoint, MouseButton.Left);
        window.MouseDown(tabPoint, MouseButton.Left); window.MouseUp(tabPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Require(window.Layout.Selected(primary)?.Preview == false &&
            Find<Button>(window, "Tab_markdown_README.md").GetLogicalDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "README.md").FontStyle == FontStyle.Normal,
            "Double-clicking a preview tab did not keep it and remove italic styling.");
        window.Layout.Close(primary, window.Layout.Selected(primary)!.Id);
        Await(window.OpenDocumentAsync("README.md"));
        Await(window.OpenDocumentAsync("hello.txt", true));
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Thread.Sleep(Application.Current!.PlatformSettings!.GetDoubleTapTime(PointerType.Mouse) + TimeSpan.FromMilliseconds(10));
        var inactivePreviewTab = Find<Button>(window, "Tab_markdown_README.md");
        tabPoint = Center(window, inactivePreviewTab);
        window.MouseDown(tabPoint, MouseButton.Left); window.MouseUp(tabPoint, MouseButton.Left);
        Require(ReferenceEquals(inactivePreviewTab, Find<Button>(window, "Tab_markdown_README.md")),
            "Selecting a tab replaced its input target before the second click.");
        window.MouseDown(tabPoint, MouseButton.Left); window.MouseUp(tabPoint, MouseButton.Left);
        Require(window.Layout.Selected(primary) is { Path: "README.md", Preview: false },
            "Double-clicking an inactive preview did not select and keep it.");
        window.Layout.Close(primary, window.Layout.Tabs(primary).Single(tab => tab.Path == "README.md").Id);
        window.Layout.Close(primary, window.Layout.Tabs(primary).Single(tab => tab.Path == "hello.txt").Id);
        Await(window.OpenDocumentAsync("README.md"));
        Await(window.OpenDocumentAsync("hello.txt"));
        Require(window.Layout.Tabs(primary).Count == 1, "Preview did not replace.");
        Await(window.OpenDocumentAsync("README.md", true));
        Require(window.Layout.Tabs(primary).Count == 2, "Kept tab missing.");
        Capture(window, ".bench/prototype-markdown.png");

        var filesGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files")).Id;
        Click(window, Find<Button>(window, "FoldRestore_" + filesGroup));
        var restore = Find<Button>(window, "FoldRestore_" + filesGroup);
        var foldedRegion = window.Layout.Group(filesGroup).Region;
        var foldedStack = restore.GetLogicalAncestors().OfType<Grid>().First(grid => grid.Name == "AuxiliaryStack_" + foldedRegion);
        var separators = window.Layout.State.Groups.Count(group => group.Region == foldedRegion) - 1;
        var foldedHeight = 27 * (foldedStack.Bounds.Height - separators) / foldedStack.Bounds.Height;
        Require(window.Layout.Group(filesGroup).Folded && restore.IsFocused &&
            Math.Abs(restore.GetLogicalAncestors().OfType<Grid>().First(grid => grid.Name == "DockGroup").Bounds.Height - foldedHeight) < .01,
            "Side folding did not retain compact geometry and keyboard focus.");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Require(!window.Layout.Group(filesGroup).Folded && Find<Button>(window, "Tab_files").IsFocused,
            "Keyboard expansion did not restore selected-tab focus.");
        var originalFocus = window.Layout.View.FocusedGroup;
        window.KeyPress(Key.F6, RawInputModifiers.Control, PhysicalKey.F6, null); Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.F6, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F6, null); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.View.FocusedGroup == originalFocus, "Reverse group traversal did not return to its starting pane.");

        // Cancelled pointer gestures must preserve the original placement and epoch.
        var surface = Find<DockSurface>(window, "WorkspaceWorkbench");
        var source = Find<Button>(window, "Tab_files");
        var point = Center(window, source);
        var epoch = window.Layout.Epoch;
        var originalGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files")).Id;
        window.MouseDown(point, MouseButton.Left);
        window.MouseMove(point + new Vector(30, 40));
        Dispatcher.UIThread.RunJobs();
        Require(surface.IsDragging, "Pointer drag did not start.");
        Await(window.RefreshAsync());
        Require(surface.IsDragging && window.Layout.Epoch == epoch, "Host refresh cancelled a drag draft.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(point + new Vector(30, 40), MouseButton.Left);
        Require(!surface.IsDragging && window.Layout.Epoch == epoch &&
            window.Layout.Group(originalGroup).Tools.Any(tab => tab.Id == "files"), "Escape changed the source.");

        // Move a tool to the Projects tab strip, then verify a single canonical placement.
        source = Find<Button>(window, "Tab_files");
        var projects = Find<Button>(window, "Tab_projects");
        var from = Center(window, source);
        var to = projects.TranslatePoint(new Point(projects.Bounds.Width - 2, 16), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(10, 10));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var projectsGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "projects"));
        Require(projectsGroup.Tools.Any(tab => tab.Id == "files"), "Pointer drop did not join the pane.");
        Require(window.Layout.State.Groups.SelectMany(group => group.Tools).Count(tab => tab.Id == "files") == 1, "Pointer drop copied a tool.");

        var bottomGroup = window.Layout.State.Groups.Single(group => group.Region == "bottom").Id;
        foreach (var terminal in window.Layout.Tabs(bottomGroup).ToArray()) window.Layout.Close(bottomGroup, terminal.Id);
        Pump(() => window.Layout.Tabs(bottomGroup).Count == 0, "Closing the idle initial terminal did not empty the bottom group.");
        Require(window.Layout.RemoveGroup(bottomGroup), "Empty bottom group removal failed.");
        window.Layout.Visible("bottom", true);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        source = Find<Button>(window, "Tab_files"); from = Center(window, source);
        to = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, surface.Bounds.Height - 8), window)!.Value;
        window.MouseDown(from, MouseButton.Left); window.MouseMove(from + new Vector(10, 10));
        window.MouseMove(to); window.MouseUp(to, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.State.BottomVisible && window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files")).Region == "bottom",
            "Hidden bottom drop did not reveal and reuse a group.");

        window.UpdateLayout();
        source = Find<Button>(window, "Tab_markdown_README.md");
        var centerPanel = Find<Control>(window, "CenterPanel");
        from = Center(window, source);
        to = centerPanel.TranslatePoint(new Point(centerPanel.Bounds.Width - 6, centerPanel.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left); window.MouseMove(from + new Vector(10, 10));
        window.MouseMove(to); window.MouseUp(to, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.State.Center.Leaves().Count() == 2, "Pointer center split failed.");
        Require(window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs).Count(tab => tab.Path == "README.md") == 1,
            "Center split duplicated a document.");

        window.UpdateLayout();
        var separator = window.GetLogicalDescendants().OfType<ResizeHandle>().First(handle => handle.Bounds.Height > 400);
        var originalWidth = window.Layout.State.LeftWidth;
        from = Center(window, separator); epoch = window.Layout.Epoch;
        window.MouseDown(from, MouseButton.Left); window.MouseMove(from + new Vector(30, 0));
        Require(separator.IsActive, "Resize draft did not start.");
        Await(window.RefreshAsync());
        Require(separator.IsActive && window.Layout.Epoch == epoch, "Host refresh cancelled a resize draft.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(from + new Vector(30, 0), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.State.LeftWidth == originalWidth && window.Layout.Epoch == epoch, "Escape committed resize geometry.");
        var leftSeparator = Find<ResizeHandle>(window, "leftSeparator");
        var originalRightWidth = window.Layout.State.RightWidth;
        leftSeparator.Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Dispatcher.UIThread.RunJobs();
        Require(window.Layout.State.LeftWidth > originalWidth && window.Layout.State.RightWidth == originalRightWidth,
            "Keyboard side resize failed after initial arrange.");

        var settings = new SettingsWindow(window, () => { });
        _ = settings.ShowDialog(window);
        settings.ShowSection("Appearance");
        Capture(settings, ".bench/prototype-settings.png");
        var light = Find<Button>(settings, "Theme_light");
        Require(light.Bounds.Width > 500 && Find<Button>(settings, "Settings_Appearance").Bounds.Width == 167,
            "Settings rows did not fill their reference columns.");
        Click(settings, light);
        Pump(() => store.Data.Preferences.Theme == "light", "Settings theme did not save.");
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Require(!settings.IsVisible, "Escape did not close settings.");
        var reloaded = new ProfileStore(Path.Combine(root, ".profile"));
        Require(reloaded.OpenState().Current.Settings.Theme == "light" && LayoutSession.IsValid(reloaded.Data.Windows[0].Layout), "Profile restore failed.");
        var other = root + "-other"; Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "note.md"), "# Other project");
        File.WriteAllText(Path.Combine(other, ".git"), "gitdir: " + Path.Combine(other, "nonexistent-git-directory"));
        var frameIds = window.Layout.State.Groups.Select(group => group.Id).ToArray();
        Await(window.OpenProjectAsync(other));
        Require(window.WorkspaceRoot == other && window.Layout.State.Groups.Select(group => group.Id).SequenceEqual(frameIds), "Project switch rewrote the frame.");
        Await(window.OpenProjectAsync(root));
        Require(window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs).Any(tab => tab.Path == "README.md"), "Project switch lost tabs.");
        window.Close();
        var restored = new WorkbenchWindow(new LocalProjectAdapter(new ProjectServices(root)), root,
            new ProfileStore(Path.Combine(root, ".profile")), E2E.E2eTerminals.Plain);
        restored.Show();
        Pump(() => restored.WorkspaceMounted && restored.GetLogicalDescendants().OfType<MarkdownPreview>().Any(),
            "Fresh process profile restoration left the document loading.");
        restored.ShowSettings();
        var liveSettings = restored.OwnedWindows.OfType<SettingsWindow>().Single();
        var fontSize = liveSettings.GetLogicalDescendants().OfType<NumericUpDown>().Single();
        fontSize.Value = 24;
        Dispatcher.UIThread.RunJobs(); restored.UpdateLayout();
        Require(restored.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.Classes.Contains("dock-tab-title"))
            .All(text => text.FontSize == 14 && text.Bounds.Height < 32),
            "Changing content font size enlarged labels beyond the fixed tab header.");
        fontSize.Value = 14;
        liveSettings.ShowSection("Line width");
        var previewWidth = ((StackPanel)Find<MarkdownPreview>(restored, "MarkdownPreview").Content!).MaxWidth;
        Find<TextBox>(liveSettings, "MarkdownLineWidthInput").Text = "60";
        Find<Button>(liveSettings, "MarkdownLineWidthSave").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        double PreviewWidth() { restored.UpdateLayout(); return ((StackPanel)Find<MarkdownPreview>(restored, "MarkdownPreview").Content!).MaxWidth; }
        Pump(() => Math.Abs(PreviewWidth() - previewWidth * 60 / 78) < 0.5,
            "Line-width settings did not update the mounted Markdown preview.");
        var bounded = Find<CheckBox>(liveSettings, "MarkdownLineWidthBounded");
        bounded.IsChecked = false;
        Pump(() => double.IsPositiveInfinity(PreviewWidth()), "Disabling bounded line width did not update the preview.");
        var savedProfile = new ProfileStore(Path.Combine(root, ".profile"));
        var savedAppearance = savedProfile.Data.Preferences;
        var savedShared = savedProfile.OpenState().Current.Settings;
        Require(savedAppearance.FontSize == 14 && savedShared.MarkdownLineWidth == 60 && !savedShared.MarkdownLineWidthBounded,
            "Live settings changes were not persisted.");
        liveSettings.Close();
        restored.Close();
        var blockedProfile = Path.Combine(root, "blocked-profile");
        var invalidProfile = Path.Combine(root, "invalid-profile");
        Directory.CreateDirectory(invalidProfile);
        File.WriteAllText(Path.Combine(invalidProfile, "profile.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Projects = new string?[] { null, "relative", "\0", root },
                LastProject = "\0",
                GitSelections = new Dictionary<string, object?>
                {
                    ["relative"] = null,
                    [root] = new { Target = "\0", Scope = "Commit", Commit = new { Sha = "invalid", ShortSha = "invalid", Subject = "", Author = "", CommittedAt = "" } }
                }
            }));
        var normalized = new ProfileStore(invalidProfile);
        Require(normalized.OpenState().Current.Projects.SequenceEqual([root]) && normalized.Data.Windows[0].LastProject.Length == 0,
            "Invalid recent-project paths survived profile restoration.");
        Require(normalized.Data.GitSelections.Count == 1 && normalized.Data.GitSelections[root] is { Target: "", Scope: "All changes", Commit: null },
            "Malformed Git query state survived profile restoration.");
        File.WriteAllText(blockedProfile, "A file cannot be used as a profile directory.");
        var unsaved = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(blockedProfile), E2E.E2eTerminals.Plain);
        unsaved.Show(); Pump(() => unsaved.WorkspaceMounted, "Read-only profile prevented project opening.");
        var saveError = Find<TextBlock>(unsaved, "WorkspaceError");
        Require(saveError.IsVisible && saveError.Text?.Contains("could not be saved", StringComparison.Ordinal) == true,
            "Profile save failure was silently hidden.");
        unsaved.Close();
        NavigationChecks.Run(root);
        StartupChecks.Run(root);
        DockInputChecks.Run(root);
        AppCommandChecks.Run(root);
        AuxiliaryInputChecks.Run(root);
        GitUiChecks.Run(root + "-git");
        Console.WriteLine("PASS tabs, Markdown, pointer drag/cancel/drop, settings persistence, project and frame restoration");
    }

    private static void Capture(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame();
        Require(frame is not null, "No rendered screenshot.");
        frame!.Save(Path.Combine(Directory.GetCurrentDirectory(), path), PngBitmapEncoderOptions.Default);
    }
}