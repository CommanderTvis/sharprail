using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;

namespace SharpRail.Checks;

internal static class LayoutChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void CheckTerminalTitles()
    {
        var session = new LayoutSession();
        session.SwitchWorkspace("one");
        var titles = new Dictionary<string, string> { ["terminal:initial"] = "Fix login" };
        SharpRail.Host.Abstractions.TerminalTab[] catalog = [new("terminal:initial", "Terminal 1")];
        session.ReconcileTerminals("one", catalog, new HashSet<string>(), titles: titles);
        session.SwitchWorkspace("two");
        session.ReconcileTerminals("two", catalog, new HashSet<string>());
        catalog = [.. catalog, new("peer", "Terminal 72")];
        session.ReconcileTerminals("one", catalog, new HashSet<string>(), titles: titles);
        var tabs = session.State.Workspaces["one"].Documents.Values.SelectMany(tabs => tabs).ToArray();
        Require(tabs.Single(tab => tab.Id == "terminal:initial").Title == "Fix login",
            "Catalog updates must retain the host's agent title in an inactive workspace.");
        Require(tabs.Single(tab => tab.Id == "peer").Title == "Terminal 72" && session.State.Workspaces["one"].NextTerminalNumber == 73,
            "Untitled terminals must retain catalog titles and numbering.");
        titles["peer"] = "Review changes";
        session.ReconcileTerminals("one", catalog, new HashSet<string>(), titles: titles);
        session.SwitchWorkspace("one");
        Require(session.State.Workspaces["one"].Documents.Values.SelectMany(tabs => tabs).Single(tab => tab.Id == "peer").Title == "Review changes",
            "A terminal placed by the catalog must also receive later agent titles.");
        Require(session.State.Workspaces["two"].Documents.Values.SelectMany(tabs => tabs).Single(tab => tab.Id == "terminal:initial").Title == "Terminal 1",
            "Agent titles must be scoped to their workspace.");
    }

    public static void Run()
    {
        var empty = new LayoutSession();
        empty.SwitchWorkspace("empty");
        Require(empty.State.Groups.All(group => group.Region != "bottom") && empty.View.Documents.Count == 0,
            "Opening a workspace must not create a bottom zone or an automatic terminal.");
        var legacy = empty.State.Copy();
        var legacyCenter = legacy.Center.Leaves().Single();
        legacy.Workspaces["empty"].Documents[legacyCenter] = [new("kept", "Kept", "file", "kept.txt")];
        var bottom = new DockGroup { Region = "bottom" };
        legacy.Groups.Add(bottom);
        legacy.Workspaces["empty"].Documents[bottom.Id] = [new("terminal:initial", "Terminal 1", "terminal")];
        var restoredLegacy = new LayoutSession(legacy);
        Require(restoredLegacy.State.Groups.All(group => group.Region != "bottom") && restoredLegacy.Tabs(legacyCenter).Single().Id == "kept",
            "An older profile must discard the bottom zone and its automatic terminal while retaining document tabs.");
        var center = empty.State.Center.Leaves().Single();
        empty.Open(new("file", "File", "file", "file.txt"), true);
        empty.ReconcileTerminals("empty", [new("peer", "Peer terminal")], new HashSet<string>());
        Require(empty.Tabs(center).Any(tab => tab.Id == "peer") && empty.Selected(center)?.Id == "file",
            "Shared terminals arrive in the center without replacing the selected document.");
        var toolSide = empty.State.Groups.First(group => group.Region == "right").Id;
        Require(empty.Move("peer", center, toolSide, 0, "after"), "A shared terminal can create its own side pane.");
        var hiddenSide = empty.View.Documents.Single(pair => pair.Value.Any(tab => tab.Id == "peer")).Key;
        empty.Visible("right", false);
        empty.ReconcileTerminals("empty", [new("peer", "Peer terminal")], new HashSet<string>());
        Require(empty.Tabs(center).Any(tab => tab.Id == "peer") && empty.Tabs(hiddenSide).All(tab => tab.Id != "peer"),
            "Projects must retain access to catalog terminals whose side region is hidden.");
        ProjectPaneChecks.Model();
        CheckTerminalTitles();
        CheckAuxiliaryRegions();
        CheckSeparatePanes();
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
        var epoch = session.Epoch;
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
            Require(!session.CanMove("a", primary, primary, 0, edge) && !session.Move("a", primary, primary, 0, edge) && !session.NewGroup(primary, edge),
                "The main column must refuse custom splits.");
        Require(session.Epoch == epoch && session.State.Center.Leaves().Single() == primary, "A refused split must change nothing.");
        Require(session.GroupTabs(primary, "a", "d", "horizontal") && session.PaneFor(primary, "a") is not null,
            "Tab groups must remain the main column's arrangement.");
        session.SwitchWorkspace("two");
        Require(session.Tabs(primary).Count == 0, "Workspace documents leaked.");
        session.Open(new("c", "C", "markdown", "c.md"), true);
        session.SwitchWorkspace("one");
        Require(session.Tabs(primary).Select(tab => tab.Id).Order().SequenceEqual(new[] { "a", "d" }), "Workspace tabs were lost.");
        Require(!session.RemoveGroup(primary), "Final center leaf must survive.");
        CheckLegacyCenterSplit();

        var files = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files"));
        var projects = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "projects"));
        var before = session.Epoch;
        Require(!session.Move("files", files.Id, primary, 0), "Tools must not enter center.");
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
        Require(!session.MoveToRegion("files", projects.Id, "bottom"), "The retired bottom zone must reject drops.");
        Require(LayoutSession.IsValid(session.State), "Model invariants violated.");
        var malformed = session.State.Copy(); malformed.Center.Axis = "invalid";
        if (!malformed.Center.IsLeaf) Require(!LayoutSession.IsValid(malformed), "Invalid split orientation accepted.");
        malformed = session.State.Copy(); malformed.Groups[0].Weight = double.NaN;
        Require(!LayoutSession.IsValid(malformed), "Invalid weights accepted.");
        malformed = session.State.Copy(); malformed.Groups[0].Region = "unknown";
        Require(!LayoutSession.IsValid(malformed), "Invalid region accepted.");

        session.SwitchWorkspace("one");
        session.ApplyPreset(DockState.Preset("review"));
        Require(session.State.Center.IsLeaf &&
            session.State.Workspaces["one"].Documents.Values.SelectMany(tabs => tabs).Any(tab => tab.Id == "a"),
            "Preset lost retained documents.");
        var random = new Random(431);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var centers = session.State.Center.Leaves().ToArray();
            var group = centers[random.Next(centers.Length)];
            switch (random.Next(4))
            {
                case 0: Require(!session.NewGroup(group, random.Next(2) == 0 ? "right" : "bottom"), "The main column must refuse splits."); break;
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
    /// <summary>A layout saved while the main column could split: three centre groups, a tab group, a duplicate and two previews.</summary>
    internal static DockState LegacyCenterSplit()
    {
        var legacy = DockState.Preset("balanced");
        var first = legacy.Center.GroupId;
        DockGroup right = new(), below = new();
        legacy.Groups.Add(right); legacy.Groups.Add(below);
        legacy.Center = new()
        {
            Axis = "horizontal",
            First = new() { GroupId = first },
            Second = new() { Axis = "vertical", First = new() { GroupId = right.Id }, Second = new() { GroupId = below.Id } }
        };
        legacy.ActiveWorkspace = "one";
        legacy.Workspaces["one"] = new()
        {
            Documents =
            {
                [first] = [new("a", "A", "file", "a.txt"), new("p", "P", "file", "p.txt", Preview: true)],
                [right.Id] = [new("b", "B", "file", "b.txt"), new("c", "C", "file", "c.txt"), new("q", "Q", "file", "q.txt", Preview: true)],
                [below.Id] = [new("t", "Terminal", "terminal"), new("a", "A", "file", "a.txt")]
            },
            Panes = { [right.Id] = [new() { Id = "pair", TabIds = ["b", "c"], Direction = "vertical", Weights = [.3, .7] }] },
            Selected = { [first] = "a", [right.Id] = "c", [below.Id] = "t" },
            FocusedCenter = right.Id,
            FocusedGroup = right.Id
        };
        legacy.Workspaces["two"] = new() { Documents = { [below.Id] = [new("z", "Z", "file", "z.txt")] }, Selected = { [below.Id] = "z" }, FocusedCenter = below.Id, FocusedGroup = below.Id };
        return legacy;
    }

    /// <summary>A persisted layout whose main column was split restores as one group holding every tab and its tab groups.</summary>
    private static void CheckLegacyCenterSplit()
    {
        var legacy = LegacyCenterSplit();
        var first = legacy.Center.Leaves().First();
        var restored = System.Text.Json.JsonSerializer.Deserialize<DockState>(System.Text.Json.JsonSerializer.Serialize(legacy))!;
        var session = new LayoutSession(restored);
        Require(session.State.Center.IsLeaf && session.State.Center.GroupId == first &&
            session.State.Groups.Count(group => group.Region == "center") == 1 && LayoutSession.IsValid(session.State),
            "A split main column must restore as one group.");
        var one = session.State.Workspaces["one"];
        Require(one.Documents[first].Select(tab => tab.Id).SequenceEqual(new[] { "a", "p", "b", "c", "q", "t" }) &&
            one.Documents[first].Count(tab => tab.Preview) == 1,
            "Every tab of a split main column must survive once, in order, with one preview.");
        Require(one.Panes[first].Single() is { Direction: "vertical" } pane && pane.TabIds.SequenceEqual(new[] { "b", "c" }) && pane.Weights.SequenceEqual(new[] { .3, .7 }),
            "A tab group from a removed split must survive the merge.");
        Require(one.Selected[first] == "c" && one.FocusedCenter == first && one.FocusedGroup == first,
            "The merged group must keep the focused split's selection.");
        Require(session.State.Workspaces["two"].Documents[first].Single().Id == "z", "Other workspaces must keep their tabs.");
        var preset = legacy.Copy();
        var custom = new LayoutSession();
        custom.SwitchWorkspace("one");
        custom.Open(new("x", "X", "file", "x.txt"), true);
        custom.ApplyPreset(preset);
        Require(custom.State.Center.IsLeaf && custom.Tabs(custom.State.Center.GroupId).Single().Id == "x",
            "A custom preset with a split main column must apply as one group.");
        Console.WriteLine("PASS a split main column restores as one group with every tab and tab group");
    }

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
        Require(tools.IsToolShowing("projects") && tools.IsToolShowing(DockState.SpecsTool) && !tools.IsToolShowing("files"), "A tool shows only as its group's shown tab.");
        tools.Visible("left", false);
        Require(!tools.IsToolShowing("projects"), "A hidden side shows no tool.");
        Console.WriteLine("PASS fork model.test.ts tab panes: grouping, contiguity, member drops, joining, preview, exclusivity, closing, persistence; isToolShowing");
    }

    // Upstream layout SPEC "Tools and terminals never share a pane", rail activation and boundary drops.
    private static void CheckSeparatePanes()
    {
        var session = new LayoutSession();
        session.SwitchWorkspace("one");
        var bottom = session.State.Center.Leaves().Single();
        session.NewTerminal(bottom);
        var side = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == DockState.SpecsTool)).Id;
        var lower = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "review")).Id;
        // The rail-entry checks below need a second tool in the lower pane.
        session.RestoreTool("lsp", lower); session.Select(lower, "review");
        var terminal = session.Tabs(bottom).Single();
        Require(terminal is { Kind: "terminal", Title: "Terminal 1" } && session.Selected(bottom)?.Id == terminal.Id,
            "An explicit terminal must open selected in its center group.");
        var epoch = session.Epoch;
        Require(!session.Move(terminal.Id, bottom, side, 0) && !session.CanMove(terminal.Id, bottom, side, 0) && session.Epoch == epoch,
            "A terminal must not join a tool pane.");
        Require(!session.Move(DockState.SpecsTool, side, bottom, 0) && session.Epoch == epoch, "A tool must not join a pane that shows terminals.");
        Require(session.Move(terminal.Id, bottom, side, 0, "after") && session.State.Groups.Where(group => group.Region == "right").Count() == 3,
            "A terminal may always create its own pane beside a tool pane.");
        var created = session.State.Workspaces["one"].Documents.Single(pair => pair.Value.Any(tab => tab.Id == terminal.Id)).Key;
        Require(created != side && session.Group(created).Tools.Count == 0, "The new pane must be tool-free.");
        Require(!session.CanRemoveGroup(created), "A terminal pane between tool panes has no sibling that can take it.");

        // A persisted mixed pane migrates: its terminal moves to the nearest tool-free sibling and old anchors go.
        var legacy = session.State.Copy();
        legacy.Workspaces["one"].Documents[created].Clear();
        legacy.Workspaces["one"].Documents[lower] = [terminal];
        legacy.Workspaces["one"].BeforeToolByTabId[terminal.Id] = "review";
        Require(!LayoutSession.IsValid(legacy), "A mixed tool and terminal pane must be invalid.");
        var migrated = new LayoutSession(System.Text.Json.JsonSerializer.Deserialize<DockState>(System.Text.Json.JsonSerializer.Serialize(legacy)));
        Require(migrated.State.Workspaces["one"].Documents[created].Single().Id == terminal.Id &&
            migrated.State.Workspaces["one"].BeforeToolByTabId.Count == 0 && migrated.State.Groups.Count == legacy.Groups.Count,
            "Migration must move a mixed pane's terminal to its nearest tool-free sibling and drop mixed-order anchors.");

        // New terminal from a tool pane creates a compatible pane directly below it.
        session.NewTerminal(lower);
        var right = session.State.Groups.Where(group => group.Region == "right").ToList();
        var below = right[right.FindIndex(group => group.Id == lower) + 1];
        Require(below.Tools.Count == 0 && session.Tabs(below.Id).Single().Kind == "terminal" && session.View.FocusedGroup == below.Id &&
            session.Tabs(lower).All(tab => tab.IsTool), "New terminal from a tool pane must create a tool-free pane directly below.");

        // A tool joining a pane that shows terminals in another workspace displaces them there deterministically.
        session.Close(created, terminal.Id);
        session.SwitchWorkspace("two");
        session.NewTerminal(created);
        var twoTerminal = session.Tabs(created).Single(tab => tab.Kind == "terminal");
        var twoTarget = session.State.TerminalTarget(session.View, session.Group(created));
        session.SwitchWorkspace("one");
        Require(session.Tabs(created).Count == 0 && session.Move("files", side, created, 0), "A tool must join a pane that shows no terminals here.");
        var two = session.State.Workspaces["two"];
        Require(!two.Documents.ContainsKey(created) && two.Documents[twoTarget].Any(tab => tab.Id == twoTerminal.Id),
            "A terminal whose pane became a tool pane must move to a surviving tool-free pane.");
        Require(session.Group(created).Folded == false && LayoutSession.IsValid(session.State), "Displacement must keep the layout valid.");

        // Passive placement uses the same rule and never unfolds.
        session.HidePane(below.Id);
        session.Focus(created);
        session.ReconcileTerminals("one", [new("terminal:initial", "Terminal 1"), new("peer", "Peer")], new HashSet<string>());
        var peer = session.State.Workspaces["one"].Documents.Single(pair => pair.Value.Any(tab => tab.Id == "peer")).Key;
        Require(session.Group(peer).Tools.Count == 0 && session.Group(below.Id).Folded, "Catalog terminals must land in a tool-free pane without unfolding.");

        // Rail entries: an unpressed entry reveals, unfolds and selects at once; the pressed one folds its pane.
        session.Visible("right", false);
        epoch = session.Epoch;
        Require(!session.ActivateEntry(lower, "review") && session.Epoch == epoch + 1 && session.State.RightVisible &&
            !session.Group(lower).Folded && session.Selected(lower)?.Id == "review", "An unpressed rail entry must reveal and select in one transition.");
        Require(session.ActivateEntry(lower, "review") && session.Group(lower).Folded, "The pressed rail entry must fold its pane.");
        Require(!session.ActivateEntry(lower, "lsp") && !session.Group(lower).Folded && session.Selected(lower)?.Id == "lsp",
            "Another entry of a folded pane must unfold it and select itself.");
        Require(!session.ActivateEntry(below.Id, null) && !session.Group(below.Id).Folded, "A terminal pane's entry must unfold it.");

        // Cluster boundaries create a pane at that index; a pane's only tab beside itself is a no-op.
        var first = session.State.Groups.First(group => group.Region == "right");
        Require(!session.CanMoveToNewGroupAt("review", lower, "right", -1), "A boundary outside the rail must be refused.");
        session.HidePane(first.Id);
        Require(session.MoveToNewGroupAt("review", lower, "right", 0) &&
            session.State.Groups.First(group => group.Region == "right").Tools.Single().Id == "review", "A boundary drop must create a pane at its index.");
        var alone = session.State.Groups.First(group => group.Region == "right");
        Require(!session.CanMoveToNewGroupAt("review", alone.Id, "right", 0) && !session.CanMoveToNewGroupAt("review", alone.Id, "right", 1),
            "Moving a pane's only tab to its own boundaries must be refused.");
        Require(session.Move("review", alone.Id, first.Id, 0) && !session.Group(first.Id).Folded,
            "An explicit drop must unfold its destination in the same transition.");

        // Project Home reserves no terminal and keeps its own Projects pane state.
        session.SwitchWorkspace("home:/project");
        Require(session.View.Documents.Values.All(tabs => tabs.Count == 0), "Project Home must not reserve a terminal.");
        session.HomeProjects(false);
        var copy = session.State.Copy();
        Require(!copy.HomeProjectsVisible && copy.LeftVisible && LayoutSession.IsValid(copy), "Project Home's Projects pane must be independent of the left side.");
        Require(LayoutSession.IsValid(session.State), "Model invariants violated.");
        session.ApplyPreset(DockState.Preset("review"));
        Require(session.State.Workspaces["one"].Documents.Where(pair => session.Group(pair.Key).Tools.Count > 0).All(pair => pair.Value.Count == 0),
            "A preset must map terminals into tool-free panes.");
        Console.WriteLine("PASS separate tool and terminal panes, rail activation, boundary drops and Project Home pane");
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
        Require(!session.MoveToNewRegionGroup("files", session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files")).Id, "bottom", true),
            "The retired bottom zone must reject new groups.");
    }
}