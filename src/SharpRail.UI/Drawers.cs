using Avalonia.Controls;

namespace SharpRail.UI;

// On a phone-sized screen the side panels are drawers: both start closed, the header opens one at a time, and
// choosing something in a drawer (a file, a workspace) or Back closes it.
public sealed partial class WorkbenchWindow
{
    private string drawerContext = "";

    private void WireDrawers()
    {
        surface.Drawers = true;
        CloseDrawers();
        this.FindControl<ContentControl>("BrandIcon")!.IsVisible = false;
        foreach (var (name, region, icon) in new[] { ("LeftDrawerButton", "left", "layoutLeft"), ("RightDrawerButton", "right", "layoutRight") })
        {
            var button = this.FindControl<Button>(name)!;
            button.Content = Ui.Icon(icon);
            button.IsVisible = true;
            button.Click += (_, _) =>
            {
                var open = region == "left" ? Layout.State.LeftVisible : Layout.State.RightVisible;
                CloseDrawers();
                if (!open) Layout.Visible(region, true);
            };
        }
        drawerContext = DrawerContext();
        Layout.Changed += CloseDrawersOnNavigation;
        Layout.SelectionChanged += _ => CloseDrawersOnNavigation();
        AddHandler(BackRequestedEvent, (_, e) =>
        {
            if (!Layout.State.LeftVisible && !Layout.State.RightVisible) return;
            CloseDrawers();
            e.Handled = true;
        });
    }

    private void CloseDrawers()
    {
        if (Layout.State.LeftVisible) Layout.Visible("left", false);
        if (Layout.State.RightVisible) Layout.Visible("right", false);
    }

    // What the centre shows: the workspace and each centre group's selected tab.
    private string DrawerContext() => Layout.State.ActiveWorkspace + "\n" +
        string.Join('\n', Layout.State.Groups.Where(group => group.Region == "center").Select(group => Layout.Selected(group.Id)?.Id));

    private void CloseDrawersOnNavigation()
    {
        var context = DrawerContext();
        if (context == drawerContext) return;
        drawerContext = context;
        CloseDrawers();
    }
}