using SharpRail.UI.Docking;

namespace SharpRail.Checks;

internal static class LayoutChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        CheckAuxiliaryRegions();
        CheckMixedResources();
        var session = new LayoutSession();
        session.SwitchWorkspace("one");
        var primary = session.State.Center.Leaves().Single();
        session.Open(new("a", "A", "markdown", "a.md"), false);
        session.Open(new("b", "B", "markdown", "b.md"), false);
        Require(session.Tabs(primary).Count == 1 && session.Tabs(primary)[0].Id == "b", "Preview must replace its slot.");
        session.Keep(primary, "b");
        session.Open(new("a", "A", "markdown", "a.md"), false);
        session.Open(new("a", "A refreshed", "markdown", "a.md"), true);
        Require(session.Tabs(primary).Count == 2 && session.Tabs(primary).All(tab => !tab.Preview), "Keep must promote without duplication.");
        Require(session.Selected(primary)?.Title == "A refreshed", "Reopening did not refresh metadata.");
        session.Close(primary, "b");
        Require(session.Selected(primary)?.Id == "a", "Closing an inactive tab changed selection.");
        session.Open(new("b", "B", "markdown", "b.md"), true);
        session.Open(new("d", "D", "file", "d.txt"), true);
        session.Select(primary, "b"); session.Close(primary, "b");
        Require(session.Selected(primary)?.Id == "d", "Closing selected tab did not select its right neighbor.");
        Require(session.Move("a", primary, primary, 0, "right"), "Center split failed.");
        var secondary = session.State.Center.Leaves().Last();
        Require(session.Selected(secondary)?.Id == "a", "Split destination must select moved resource.");
        session.SwitchWorkspace("two");
        Require(session.Tabs(primary).Count == 0, "Workspace documents leaked.");
        session.Open(new("c", "C", "markdown", "c.md"), true);
        session.SwitchWorkspace("one");
        Require(session.Selected(secondary)?.Id == "a", "Workspace selection was lost.");
        Require(session.RemoveGroup(primary), "Center merge failed.");
        session.SwitchWorkspace("two");
        Require(session.Tabs(secondary).Any(tab => tab.Id == "c"), "Removed frame group did not rehome other workspace.");
        session.Close(secondary, "c");
        Require(session.State.Center.Leaves().Single() == secondary, "Closing final tab must retain topology.");
        Require(!session.RemoveGroup(secondary), "Final center leaf must survive.");

        var files = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files"));
        var projects = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "projects"));
        var before = session.Epoch;
        Require(!session.Move("files", files.Id, secondary, 0), "Tools must not enter center.");
        Require(session.Epoch == before, "Illegal drop committed state.");
        Require(session.Move("files", files.Id, projects.Id, 1), "Tool move failed.");
        Require(session.State.Groups.SelectMany(group => group.Tools).Count(tab => tab.Id == "files") == 1, "Tool duplicated.");
        session.Close(projects.Id, "files");
        Require(session.State.ToolRestore.GetValueOrDefault("files") == projects.Id, "Hidden tool restore target lost.");
        session.RestoreTool("files");
        Require(session.Group(projects.Id).Tools.Any(tab => tab.Id == "files"), "Tool restored to wrong group.");
        session.Geometry(state => state.SideLimit = 1);
        Require(!session.NewGroup(projects.Id, "after"), "Configured group limit ignored.");
        session.Geometry(state => state.SideLimit = 6);
        session.Visible("bottom", false);
        Require(session.MoveToRegion("files", projects.Id, "bottom") && session.State.BottomVisible, "Hidden bottom drop failed.");
        Require(LayoutSession.IsValid(session.State), "Model invariants violated.");
        var malformed = session.State.Copy(); malformed.Center.Axis = "invalid";
        if (!malformed.Center.IsLeaf) Require(!LayoutSession.IsValid(malformed), "Invalid split orientation accepted.");
        malformed = session.State.Copy(); malformed.Groups[0].Weight = double.NaN;
        Require(!LayoutSession.IsValid(malformed), "Invalid weights accepted.");
        malformed = session.State.Copy(); malformed.Groups[0].Region = "unknown";
        Require(!LayoutSession.IsValid(malformed), "Invalid region accepted.");

        session.SwitchWorkspace("one");
        session.ApplyPreset(DockState.Preset("review"));
        Require(session.State.Center.Leaves().Count() == 2 &&
            session.State.Workspaces["one"].Documents.Values.SelectMany(tabs => tabs).Any(tab => tab.Id == "a"),
            "Preset lost retained documents.");
        var random = new Random(431);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var centers = session.State.Center.Leaves().ToArray();
            var group = centers[random.Next(centers.Length)];
            switch (random.Next(4))
            {
                case 0: session.NewGroup(group, random.Next(2) == 0 ? "right" : "bottom"); break;
                case 1: session.RemoveGroup(group); break;
                case 2: session.Open(new("f" + random.Next(8), "File", "file"), random.Next(2) == 0); break;
                case 3:
                    var tabs = session.Tabs(group);
                    if (tabs.Count > 0) session.Close(group, tabs[0].Id);
                    break;
            }
            Require(LayoutSession.IsValid(session.State), "Generated transition broke invariants.");
        }
        Console.WriteLine("PASS docking transitions, workspace isolation, preview, restore, limits and invariant sequence");
    }

    private static void CheckMixedResources()
    {
        var session = new LayoutSession();
        session.SwitchWorkspace("one");
        var bottom = session.State.Groups.Single(group => group.Region == "bottom").Id;
        var side = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "specs")).Id;
        var terminal = session.Tabs(bottom).Single();
        Require(terminal is { Kind: "terminal", Title: "Terminal 1" } && session.Selected(bottom)?.Id == terminal.Id,
            "A new workspace must open one selected terminal in its bottom group.");
        Require(session.Move(terminal.Id, bottom, side, 0) && session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, "specs", "files"]),
            "Moving a terminal before a tool must preserve the mixed tab order.");
        Require(session.Move(terminal.Id, side, side, 3) && session.Tabs(side).Select(tab => tab.Id).SequenceEqual(["specs", "files", terminal.Id]),
            "Moving an anchored terminal to the end must remove its old tool anchor.");
        Require(session.Move(terminal.Id, side, side, 0), "Moving a terminal back before the first tool failed.");
        Require(session.Move("specs", side, side, 3) && session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, "files", "specs"]),
            "Moving a tool must preserve the terminal's displayed position and retarget its anchor.");
        Require(session.Move("specs", side, side, 1), "Restoring tool order around a terminal failed.");
        session.Close(side, "specs");
        Require(session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, "files"]), "Hiding a tool must preserve the terminal's displayed position.");
        session.RestoreTool("specs");
        Require(session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, "specs", "files"]), "Revealing a tool must restore its mixed-strip position.");
        session.SwitchWorkspace("two");
        Require(session.Tabs(side).Select(tab => tab.Id).SequenceEqual(["specs", "files"]), "A terminal must not leak into another workspace's tool strip.");
        session.SwitchWorkspace("one");
        var restored = new LayoutSession(System.Text.Json.JsonSerializer.Deserialize<DockState>(System.Text.Json.JsonSerializer.Serialize(session.State)));
        Require(restored.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, "specs", "files"]), "Persisted mixed tab order was lost.");
        restored.Select(side, terminal.Id); restored.Close(side, terminal.Id);
        Require(restored.Selected(side)?.Id == "specs", "Closing a terminal must select its neighbor in the displayed order.");
        restored.NewTerminal(side); restored.ApplyPreset(DockState.Preset("review"));
        Require(restored.State.Groups.Where(group => group.Region == "bottom").SelectMany(group => restored.Tabs(group.Id)).Any(tab => tab.Kind == "terminal") &&
            !restored.State.Center.Leaves().SelectMany(restored.Tabs).Any(tab => tab.Kind == "terminal"),
            "Applying a preset must preserve terminal resources in the bottom region.");
    }

    private static void CheckAuxiliaryRegions()
    {
        var session = new LayoutSession(); session.SwitchWorkspace("auxiliary");
        var right = session.State.Groups.Where(group => group.Region == "right").ToArray();
        Require(session.NewGroup(right[0].Id, "after"), "Auxiliary creation failed.");
        var groups = session.State.Groups.Where(group => group.Region == "right").ToArray();
        Require(Math.Abs(groups.Sum(group => group.Weight) - 1) < 1e-9 &&
            Math.Abs(groups[1].Weight - 1d / 3) < 1e-9 &&
            Math.Abs(groups[0].Weight / groups[2].Weight - 1.25) < 1e-9,
            "Auxiliary creation did not preserve retained proportions.");
        var removedWeight = groups[1].Weight; var targetWeight = groups[0].Weight;
        Require(session.RemoveGroup(groups[1].Id) && Math.Abs(session.Group(groups[0].Id).Weight - targetWeight - removedWeight) < 1e-9,
            "Auxiliary merge did not transfer size to its preceding neighbor.");
        var left = session.State.Groups.Single(group => group.Region == "left");
        Require(!session.RemoveGroup(left.Id), "Removing a populated final auxiliary group must be refused.");
        Require(session.NewGroup(left.Id, "after"), "Second left group creation failed.");
        var retainedLeft = session.State.Groups.Where(group => group.Region == "left").Select(group => group.Id).ToArray();
        session.Focus(left.Id);
        session.Visible("left", false);
        var source = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files"));
        Require(session.MoveToRegion("files", source.Id, "left") &&
            session.State.Groups.Where(group => group.Region == "left").Take(2).Select(group => group.Id).SequenceEqual(retainedLeft) &&
            session.State.Groups.Last(group => group.Region == "left").Tools.Any(tab => tab.Id == "files"),
            "Hidden side drop must append a new group after all retained groups, regardless of prior focus.");
        var bottom = session.State.Groups.Single(group => group.Region == "bottom");
        Require(session.NewGroup(bottom.Id, "after"), "Second bottom group creation failed.");
        session.Focus(bottom.Id); session.Focus(session.State.Center.Leaves().First());
        source = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files"));
        session.Visible("bottom", false);
        Require(session.MoveToRegion("files", source.Id, "bottom") && session.Group(bottom.Id).Tools.Any(tab => tab.Id == "files"),
            "Hidden bottom drop must reuse its last focused group.");
        Require(session.View.Copy().FocusedAuxiliary.GetValueOrDefault("bottom") == bottom.Id, "Copied workspace lost auxiliary attention.");
    }
}
