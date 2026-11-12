using Avalonia;
using Avalonia.Controls;

namespace SharpRail.UI.Docking;

// A tab group on a phone shows one member at a time under a strip of chips. Which member is the phone's own choice,
// kept here and never sent to the host: the shared layout keeps every member, its direction and weights, and the
// group's selection. The phone follows the selection only when it changes (a tab opened from Projects, another
// client's choice), and otherwise stays on the member it last showed.
public sealed partial class DockSurface
{
    // Per pane: the group selection last seen, and the member shown since.
    private readonly Dictionary<string, (string Seen, string Shown)> phoneMembers = [];
    // Members with something new since they were last shown, by workspace and tab.
    private readonly HashSet<(string Workspace, string Tab)> memberActivity = [];
    private readonly Dictionary<string, PhonePaneView> phonePanes = [];

    /// <summary>The phone's tab groups on screen, by centre group.</summary>
    internal IReadOnlyDictionary<string, PhonePaneView> PhonePanes => phonePanes;

    /// <summary>
    /// Notes new activity (output, an agent's attention, a new drawing) in a tab of the active workspace. A phone
    /// marks the chip of a member it is not showing until it shows it; elsewhere this does nothing.
    /// </summary>
    public void NoteActivity(string tabId)
    {
        if (!Drawers) return;
        foreach (var view in phonePanes.Values)
            if (view.Hides(tabId) && memberActivity.Add((Session.State.ActiveWorkspace, tabId))) view.Paint();
    }

    private Control PhonePane(DockGroup group, DockPane pane, DockTab[] members, DockTab selected)
    {
        var view = new PhonePaneView(group.Id, pane.Id, tab => renderContent(tab), shown => MemberShown(pane.Id, shown),
            tab => memberActivity.Contains((Session.State.ActiveWorkspace, tab)), tab => TabOverlay?.Invoke(tab));
        phonePanes[group.Id] = view;
        view.Show(members, PhoneMember(pane, selected, members));
        MemberShown(pane.Id, view.Shown);
        return view;
    }

    private bool TryRefreshPhonePane(Border body, DockGroup group, DockPane pane, DockTab[] members, DockTab selected)
    {
        if (body.Child is not PhonePaneView view || view.PaneId != pane.Id) return false;
        phonePanes[group.Id] = view;
        view.Show(members, PhoneMember(pane, selected, members));
        MemberShown(pane.Id, view.Shown);
        return true;
    }

    private string PhoneMember(DockPane pane, DockTab selected, DockTab[] members)
    {
        if (!phoneMembers.TryGetValue(pane.Id, out var known) || known.Seen != selected.Id || members.All(member => member.Id != known.Shown))
            phoneMembers[pane.Id] = known = (selected.Id, selected.Id);
        return known.Shown;
    }

    private void MemberShown(string paneId, string tabId)
    {
        phoneMembers[paneId] = (phoneMembers.TryGetValue(paneId, out var known) ? known.Seen : tabId, tabId);
        if (memberActivity.Remove((Session.State.ActiveWorkspace, tabId)))
            foreach (var view in phonePanes.Values.Where(view => view.PaneId == paneId)) view.Paint();
    }

    /// <summary>The phone tab group a sideways drag starting on <paramref name="source"/> moves within, if it has a member that way.</summary>
    internal PhonePaneView? MemberSwipe(Visual? source, int direction) =>
        Drawers ? phonePanes.Values.FirstOrDefault(view => view.Holds(source) && view.CanSwipe(direction)) : null;
}