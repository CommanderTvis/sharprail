using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.UI.Panels;

namespace SharpRail.UI;

// On a phone-sized screen the workbench is one page at a time, chosen from a bar at the bottom: Projects, the
// current terminal or editor, the other tools, and Settings. The side pages are the frame's left and right
// regions, so choosing something in one (a file, a workspace) or Back returns to the current page.
public sealed partial class WorkbenchWindow
{
    private string pageContext = "";
    private SettingsWindow? openSettings;

    /// <summary>The height the page bar takes at the bottom of the window; nothing where there is none.</summary>
    internal double PageBarHeight => this.FindControl<Border>("PageBar") is { IsVisible: true } bar ? bar.Bounds.Height : 0;

    // Settings is a page like the others in a compact workbench: its item is lit while it shows.
    private void SettingsShown(SettingsWindow? settings)
    {
        openSettings = settings;
        foreach (var update in pageBarUpdates) update();
    }
    private readonly List<Action> pageBarUpdates = [];

    private void WireDrawers()
    {
        surface.Drawers = true;
        CloseDrawers();
        this.FindControl<Button>("SettingsButton")!.IsVisible = false;
        var bar = this.FindControl<Border>("PageBar")!;
        var items = this.FindControl<Grid>("PageBarItems")!;
        bar.IsVisible = true;
        PageItem(items, 0, "PageProjects", () => "folderTab", () => "Projects", () => openSettings is null && Layout.State.LeftVisible, () => ShowPage("left"));
        PageItem(items, 1, "PageCurrent", () => CurrentIsTerminal() ? "terminal" : "fileText", () => CurrentIsTerminal() ? "Terminal" : "Editor",
            () => openSettings is null && !Layout.State.LeftVisible && !Layout.State.RightVisible, () => ShowPage(null));
        PageItem(items, 2, "PageTools", () => "stack", () => "Tools", () => openSettings is null && Layout.State.RightVisible, () => ShowPage("right"));
        PageItem(items, 3, "PageSettings", () => "settings", () => "Settings", () => openSettings is not null, () => { if (openSettings is null) ShowSettings(); });
        pageContext = PageContext();
        Layout.Changed += PageChanged;
        Layout.SelectionChanged += _ => PageChanged();
        AddHandler(BackRequestedEvent, (_, e) =>
        {
            if (!Layout.State.LeftVisible && !Layout.State.RightVisible) return;
            CloseDrawers();
            e.Handled = true;
        });
        // The keyboard takes the bar's place: typing needs the rows more than it needs navigation.
        Opened += (_, _) =>
        {
            if (InputPane is { } pane) pane.StateChanged += (_, args) => bar.IsVisible = args.NewState != InputPaneState.Open;
        };
    }

    private void PageItem(Grid items, int column, string name, Func<string> icon, Func<string> label, Func<bool> selected, Action open)
    {
        var button = new Button
        {
            Name = name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = default,
            CornerRadius = default,
            Padding = default
        };
        button.Click += (_, _) => open();
        void Paint()
        {
            var brush = selected() ? Ui.Accent : Ui.Muted;
            var text = Ui.Text(label(), brush, 11);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var glyph = Ui.Icon(icon(), brush, 20);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            button.Content = new StackPanel { Spacing = 2, Children = { glyph, text } };
            AutomationProperties.SetName(button, label());
        }
        pageBarUpdates.Add(Paint);
        Paint();
        Ui.Place(items, button, 0, column);
    }

    private void ShowPage(string? region)
    {
        openSettings?.Close();
        CloseDrawers();
        if (region is not null) Layout.Visible(region, true);
    }

    private void CloseDrawers()
    {
        if (Layout.State.LeftVisible) Layout.Visible("left", false);
        if (Layout.State.RightVisible) Layout.Visible("right", false);
    }

    private Docking.DockTab? CurrentTab() => Layout.State.Groups.Where(group => group.Region == "center")
        .Select(group => Layout.Selected(group.Id)).FirstOrDefault(tab => tab is not null);

    private bool CurrentIsTerminal() => CurrentTab() is null or { Kind: "terminal" };

    // What the current page shows: the workspace and each centre group's selected tab.
    private string PageContext() => Layout.State.ActiveWorkspace + "\n" +
        string.Join('\n', Layout.State.Groups.Where(group => group.Region == "center").Select(group => Layout.Selected(group.Id)?.Id));

    private void PageChanged()
    {
        var context = PageContext();
        if (context != pageContext)
        {
            pageContext = context;
            CloseDrawers();
        }
        foreach (var update in pageBarUpdates) update();
    }
}