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
        CheckPanes();
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

    // Fork model.test.ts "tab panes", translated.
    private static void CheckPanes()
    {
        (LayoutSession Session, string Group) Fresh(params string[] ids)
        {
            var created = new LayoutSession();
            created.SwitchWorkspace("panes");
            foreach (var id in ids) created.Open(new(id, id.ToUpperInvariant(), "file", id + ".txt"), true);
            return (created, created.State.Center.Leaves().Single());
        }
        string[] Strip(LayoutSession s, string g) => [.. s.Tabs(g).Select(tab => tab.Id)];

        var (session, group) = Fresh("a", "b", "c");
        Require(session.GroupTabs(group, "a", "b", "horizontal") && session.PaneFor(group, "a") is { } created &&
            created.TabIds.SequenceEqual(["b", "a"]) && created.Weights.SequenceEqual([0.5, 0.5]) && session.PaneFor(group, "c") is null,
            "Grouping a tab with a lone tab creates a pane holding both.");

        (session, group) = Fresh("a", "b", "c");
        Require(session.GroupTabs(group, "c", "a", "horizontal") && Strip(session, group).SequenceEqual(["a", "c", "b"]) &&
            session.PaneFor(group, "c")!.TabIds.SequenceEqual(["a", "c"]), "Grouping pulls the members together into one run.");
        Require(!session.Move("b", group, group, 1), "A drop between a pane's members is refused rather than splitting the block.");
        var copy = new LayoutSession(session.State.Copy());
        Require(copy.Move("b", group, group, 0) && Strip(copy, group).SequenceEqual(["b", "a", "c"]), "A drop beside the block still moves the tab.");
        copy = new LayoutSession(session.State.Copy());
        Require(copy.Move("c", group, group, 0) && copy.PaneFor(group, "c")!.TabIds.SequenceEqual(["c", "a"]) && Strip(copy, group).SequenceEqual(["c", "a", "b"]),
            "A member dropped inside its own run reorders the pane.");
        copy = new LayoutSession(session.State.Copy());
        Require(copy.Move("c", group, group, 3) && copy.PaneFor(group, "a") is null && Strip(copy, group).SequenceEqual(["a", "b", "c"]),
            "A member dropped past its run leaves, and the pane dissolves below two members.");
        Require(session.SetPaneWeights(group, session.PaneFor(group, "a")!.Id, [0.7, 0.3]) && session.ReorderPaneMember(group, "c", -1) &&
            Strip(session, group).SequenceEqual(["c", "a", "b"]) && session.PaneFor(group, "c")!.Weights.SequenceEqual([0.3, 0.7]),
            "A member reordered from its menu keeps its weight.");

        (session, group) = Fresh("a", "b", "c");
        session.GroupTabs(group, "a", "b", "horizontal");
        session.GroupTabs(group, "c", "a", "horizontal");
        Require(session.View.Panes[group].Count == 1 && session.PaneFor(group, "c")!.TabIds.SequenceEqual(["b", "a", "c"]), "Grouping onto a member joins its pane.");

        (session, group) = Fresh("a", "b", "c");
        session.GroupTabs(group, "a", "b", "horizontal");
        session.SetPaneWeights(group, session.PaneFor(group, "a")!.Id, [0.7, 0.3]);
        session.GroupTabs(group, "c", "a", "vertical");
        var joined = session.PaneFor(group, "c")!;
        Require(joined.Direction == "horizontal" && joined.TabIds.SequenceEqual(["b", "a", "c"]) &&
            joined.Weights.Select(weight => Math.Round(weight, 4)).SequenceEqual([0.4667, 0.2, 0.3333]), "A newcomer joins the arrangement rather than redrawing it.");

        (session, group) = Fresh("a");
        session.Open(new("d", "D", "file", "d.txt"), false);
        session.GroupTabs(group, "d", "a", "horizontal");
        session.Open(new("e", "E", "file", "e.txt"), false);
        Require(Strip(session, group).SequenceEqual(["a", "e"]) && session.PaneFor(group, "e") is { Direction: "horizontal" } preview &&
            preview.TabIds.SequenceEqual(["a", "e"]) && preview.Weights.SequenceEqual([0.5, 0.5]), "A preview over a pane member takes its place.");

        (session, group) = Fresh("a", "b", "c", "d");
        session.GroupTabs(group, "a", "b", "horizontal");
        session.GroupTabs(group, "c", "d", "horizontal");
        session.GroupTabs(group, "a", "c", "horizontal");
        Require(session.View.Panes[group].Count == 1 && session.PaneFor(group, "a")!.TabIds.SequenceEqual(["d", "c", "a"]) && session.PaneFor(group, "b") is null,
            "Membership is exclusive: joining a second pane leaves the first.");
        Require(!session.GroupTabs(group, "a", "a", "horizontal") && !session.GroupTabs(group, "a", "missing", "horizontal"), "Refuses itself and a missing tab.");

        (session, group) = Fresh("a", "b", "c");
        session.GroupTabs(group, "a", "b", "horizontal");
        Require(session.Ungroup(group, "a") && !session.View.Panes.ContainsKey(group), "Ungrouping dissolves a pane below two members.");
        session.GroupTabs(group, "a", "b", "horizontal");
        session.GroupTabs(group, "c", "a", "horizontal");
        session.Close(group, "c");
        Require(session.PaneFor(group, "c") is null && session.PaneFor(group, "a")!.TabIds.SequenceEqual(["b", "a"]), "Closing a tab takes its membership with it.");
        var restored = new LayoutSession(System.Text.Json.JsonSerializer.Deserialize<DockState>(System.Text.Json.JsonSerializer.Serialize(session.State)));
        Require(restored.PaneFor(group, "a")!.TabIds.SequenceEqual(["b", "a"]), "Panes persist with the workspace view.");

        var tools = new LayoutSession();
        tools.SwitchWorkspace("tools");
        Require(tools.IsToolShowing("projects") && tools.IsToolShowing("changes") && !tools.IsToolShowing("review"), "A tool shows only as its group's shown tab.");
        tools.Visible("left", false);
        Require(!tools.IsToolShowing("projects"), "A hidden side shows no tool.");
        Console.WriteLine("PASS fork model.test.ts tab panes: grouping, contiguity, member drops, joining, preview, exclusivity, closing, persistence; isToolShowing");
    }

    private static void CheckMixedResources()
    {
        var session = new LayoutSession();
        session.SwitchWorkspace("one");
        var bottom = session.State.Groups.Single(group => group.Region == "bottom").Id;
        var side = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == DockState.SpecsTool)).Id;
        var terminal = session.Tabs(bottom).Single();
        Require(terminal is { Kind: "terminal", Title: "Terminal 1" } && session.Selected(bottom)?.Id == terminal.Id,
            "A new workspace must open one selected terminal in its bottom group.");
        Require(session.Move(terminal.Id, bottom, side, 0) && session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, DockState.SpecsTool, "files"]),
            "Moving a terminal before a tool must preserve the mixed tab order.");
        Require(session.Move(terminal.Id, side, side, 3) && session.Tabs(side).Select(tab => tab.Id).SequenceEqual([DockState.SpecsTool, "files", terminal.Id]),
            "Moving an anchored terminal to the end must remove its old tool anchor.");
        Require(session.Move(terminal.Id, side, side, 0), "Moving a terminal back before the first tool failed.");
        Require(session.Move(DockState.SpecsTool, side, side, 3) && session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, "files", DockState.SpecsTool]),
            "Moving a tool must preserve the terminal's displayed position and retarget its anchor.");
        Require(session.Move(DockState.SpecsTool, side, side, 1), "Restoring tool order around a terminal failed.");
        session.Close(side, DockState.SpecsTool);
        Require(session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, "files"]), "Hiding a tool must preserve the terminal's displayed position.");
        session.RestoreTool(DockState.SpecsTool);
        Require(session.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, DockState.SpecsTool, "files"]), "Revealing a tool must restore its mixed-strip position.");
        session.SwitchWorkspace("two");
        Require(session.Tabs(side).Select(tab => tab.Id).SequenceEqual([DockState.SpecsTool, "files"]), "A terminal must not leak into another workspace's tool strip.");
        session.SwitchWorkspace("one");
        var restored = new LayoutSession(System.Text.Json.JsonSerializer.Deserialize<DockState>(System.Text.Json.JsonSerializer.Serialize(session.State)));
        Require(restored.Tabs(side).Select(tab => tab.Id).SequenceEqual([terminal.Id, DockState.SpecsTool, "files"]), "Persisted mixed tab order was lost.");
        restored.Select(side, terminal.Id); restored.Close(side, terminal.Id);
        Require(restored.Selected(side)?.Id == DockState.SpecsTool, "Closing a terminal must select its neighbor in the displayed order.");
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