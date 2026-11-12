using Tereditor.Host.Abstractions;

namespace Tereditor.UI;

/// <summary>This window's view of which project the rail shows expanded for the workspace it landed on.</summary>
public sealed partial class WorkbenchWindow
{
    // The project of the workspace the window landed on, shown expanded over the persisted collapsed set until
    // the window lands in another project or the user collapses it; the persisted set itself is untouched.
    private string? revealedProject;
    private string revealedFor = "";

    private void WireProjectReveal() => LocationChanged += () => { if (ObserveActiveProject()) UpdateRailSelection(); };

    /// <summary>The rail's rows of a project, Default first and then the others newest created first.</summary>
    private IReadOnlyList<WorkspaceRecord> RailOrder(string project)
    {
        ObserveActiveProject();
        var rows = RailWorkspaces(project);
        return rows.Where(row => row.Kind == WorkspaceKinds.Default).Concat(rows.Where(row => row.Kind != WorkspaceKinds.Default).Reverse()).ToArray();
    }

    /// <summary>Reveals the active workspace's project once per change of project. Returns whether the rail needs redrawing.</summary>
    private bool ObserveActiveProject()
    {
        var owner = atHome ? "" : projectRoot;
        if (owner.Length == 0 || owner == revealedFor) return false;
        revealedFor = owner;
        var changed = profile.Data.CollapsedProjects.Contains(owner) && revealedProject != owner;
        revealedProject = owner;
        return changed;
    }

    private bool Collapsed(string project) => profile.Data.CollapsedProjects.Contains(project) && project != revealedProject;
}