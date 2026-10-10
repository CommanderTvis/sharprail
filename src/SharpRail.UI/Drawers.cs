using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        if (settings is not null) WireSettingsSwipe(settings);
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
        WirePageSwipes();
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

    // The pages lie side by side: Projects, the current tab, Tools. A finger travelling sideways drags the
    // neighbour in with it; let go far enough or fast enough and that page stays, otherwise the old one returns.
    // A touch that started out vertical belongs to what is under it.
    private void WirePageSwipes()
    {
        (IPointer Pointer, Point Start, long At)? touch = null;
        (int From, int Sign)? drag = null;
        var pastTools = false;
        static string? Region(int page) => page < 0 ? "left" : page > 0 ? "right" : null;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            // A drag whose touch the system took away never lifted: it ends where it was going.
            if (drag is not null) { drag = null; surface.ReleasePage(true, () => { }); }
            touch = e.Pointer.Type == PointerType.Touch && openSettings is null ? (e.Pointer, e.GetPosition(this), Environment.TickCount64) : null;
            pastTools = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (touch is not { } start || start.Pointer != e.Pointer) return;
            var moved = e.GetPosition(this) - start.Start;
            if (drag is null)
            {
                if (Math.Abs(moved.Y) > 24 && Math.Abs(moved.Y) > Math.Abs(moved.X)) { touch = null; return; }
                if (Math.Abs(moved.X) < 20 || Math.Abs(moved.X) < 2 * Math.Abs(moved.Y)) return;
                var page = Layout.State.LeftVisible ? -1 : Layout.State.RightVisible ? 1 : 0;
                var next = Math.Clamp(page - Math.Sign(moved.X), -1, 1);
                // Settings lies past Tools, but in a window of its own: nothing to drag in, so the lift opens it.
                if (next == page) { pastTools = page > 0; if (!pastTools) touch = null; return; }
                drag = (page, Math.Sign(moved.X));
                surface.HoldPage(() => ShowPage(Region(next)));
            }
            surface.DragPage(moved.X);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (touch is not { } start || start.Pointer != e.Pointer) return;
            touch = null;
            if (pastTools && drag is null)
            {
                var swept = e.GetPosition(this) - start.Start;
                if (swept.X < -64 && Math.Abs(swept.X) > 2 * Math.Abs(swept.Y)) ShowSettings();
                return;
            }
            if (drag is not { } dragged) return;
            drag = null;
            var travelled = (e.GetPosition(this).X - start.Start.X) * dragged.Sign;
            var stays = travelled > Bounds.Width / 3 || travelled > 48 && Environment.TickCount64 - start.At < 350;
            surface.ReleasePage(stays, () => ShowPage(Region(dragged.From)));
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Settings is the last page: a finger swept to the right over it goes back to the one before.
    private static void WireSettingsSwipe(SettingsWindow settings)
    {
        (IPointer Pointer, Point Start)? touch = null;
        settings.AddHandler(PointerPressedEvent, (_, e) =>
        {
            touch = e.Pointer.Type == PointerType.Touch ? (e.Pointer, e.GetPosition(settings)) : null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        settings.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (touch is not { } start || start.Pointer != e.Pointer) return;
            touch = null;
            var swept = e.GetPosition(settings) - start.Start;
            if (swept.X > 64 && Math.Abs(swept.X) > 2 * Math.Abs(swept.Y)) settings.Close();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Moves to the neighbouring page: a finger travelling right uncovers the page on the left.</summary>
    internal void SwipePage(bool towardsLeft)
    {
        if (openSettings is not null) return;
        var page = Layout.State.LeftVisible ? -1 : Layout.State.RightVisible ? 1 : 0;
        var next = Math.Clamp(page + (towardsLeft ? -1 : 1), -1, 1);
        if (next != page) ShowPage(next < 0 ? "left" : next > 0 ? "right" : null);
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