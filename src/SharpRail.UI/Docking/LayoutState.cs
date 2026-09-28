namespace SharpRail.UI.Docking;

public sealed record DockTab(string Id, string Title, string Kind, string Path = "", bool Preview = false, string Scope = "", string Comparison = "")
{
    public bool IsTool => Kind == "tool";
}

public sealed class CenterNode
{
    public string GroupId { get; set; } = "";
    public string Axis { get; set; } = "horizontal";
    public double Ratio { get; set; } = .5;
    public CenterNode? First { get; set; }
    public CenterNode? Second { get; set; }
    public bool IsLeaf => First is null;
    public CenterNode Copy() => new() { GroupId = GroupId, Axis = Axis, Ratio = Ratio, First = First?.Copy(), Second = Second?.Copy() };
    public IEnumerable<string> Leaves() => IsLeaf ? [GroupId] : First!.Leaves().Concat(Second!.Leaves());
}

public sealed class DockGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Region { get; set; } = "center";
    public List<DockTab> Tools { get; set; } = [];
    public bool Folded { get; set; }
    public double Weight { get; set; } = 1;
    public DockGroup Copy() => new() { Id = Id, Region = Region, Tools = [.. Tools], Folded = Folded, Weight = Weight };
}

public sealed class WorkspaceView
{
    public Dictionary<string, List<DockTab>> Documents { get; set; } = [];
    public Dictionary<string, string> Selected { get; set; } = [];
    public string FocusedCenter { get; set; } = "";
    public string FocusedGroup { get; set; } = "";
    public Dictionary<string, string> FocusedAuxiliary { get; set; } = [];
    public WorkspaceView Copy() => new()
    {
        Documents = Documents.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
        Selected = new(Selected),
        FocusedCenter = FocusedCenter,
        FocusedGroup = FocusedGroup,
        FocusedAuxiliary = new(FocusedAuxiliary)
    };
}

public sealed class DockState
{
    public int Schema { get; set; } = 1;
    public CenterNode Center { get; set; } = new();
    public List<DockGroup> Groups { get; set; } = [];
    public Dictionary<string, WorkspaceView> Workspaces { get; set; } = [];
    public Dictionary<string, string> ToolRestore { get; set; } = [];
    public string ActiveWorkspace { get; set; } = "";
    public bool LeftVisible { get; set; } = true;
    public bool RightVisible { get; set; } = true;
    public bool BottomVisible { get; set; } = true;
    public double LeftWidth { get; set; } = .18;
    public double RightWidth { get; set; } = .28;
    public double BottomHeight { get; set; } = .30;
    public string BottomAlignment { get; set; } = "center";
    public int SideLimit { get; set; } = 6;
    public int BottomLimit { get; set; } = 3;
    public DockState Copy() => new()
    {
        Center = Center.Copy(),
        Groups = Groups.Select(group => group.Copy()).ToList(),
        Workspaces = Workspaces.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()),
        ToolRestore = new(ToolRestore),
        ActiveWorkspace = ActiveWorkspace,
        LeftVisible = LeftVisible,
        RightVisible = RightVisible,
        BottomVisible = BottomVisible,
        LeftWidth = LeftWidth,
        RightWidth = RightWidth,
        BottomHeight = BottomHeight,
        BottomAlignment = BottomAlignment,
        SideLimit = SideLimit,
        BottomLimit = BottomLimit
    };

    public static readonly string[] ToolNames = ["projects", "specs", "files", "changes", "review"];
    public static DockTab Tool(string id) => new(id, char.ToUpperInvariant(id[0]) + id[1..], "tool");

    public static DockState Preset(string name)
    {
        var center = new DockGroup();
        var left = new DockGroup { Region = "left", Tools = [Tool("projects")] };
        var top = new DockGroup { Region = "right", Tools = [Tool("specs"), Tool("files")], Weight = 1.25 };
        var lower = new DockGroup { Region = "right", Tools = [Tool("changes"), Tool("review")] };
        var bottom = new DockGroup { Region = "bottom" };
        var state = new DockState { Center = new() { GroupId = center.Id }, Groups = [center, left, top, lower, bottom] };
        if (name == "focus") state.LeftVisible = state.RightVisible = state.BottomVisible = false;
        if (name == "review")
        {
            var secondary = new DockGroup();
            state.Groups.Add(secondary);
            state.Center = new() { Axis = "vertical", First = new() { GroupId = center.Id }, Second = new() { GroupId = secondary.Id } };
            top.Tools = [Tool("changes"), Tool("review")]; top.Weight = 1.4;
            lower.Tools = [Tool("specs"), Tool("files")];
            state.LeftWidth = .16; state.RightWidth = .32;
        }
        return state;
    }
}
