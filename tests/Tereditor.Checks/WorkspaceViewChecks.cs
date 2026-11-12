using SharpRail.UI.Docking;

namespace SharpRail.Checks;

/// <summary>The layout keeps one view per workspace the host still lists: creating and removing workspaces returns the count to the live set.</summary>
internal static class WorkspaceViewChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var session = new LayoutSession();
        var live = new HashSet<string> { "/w/main", "/w/keep" };
        session.SwitchWorkspace("/w/keep");
        session.SwitchWorkspace("/w/main");
        for (var round = 0; round < 20; round++)
        {
            var path = $"/w/temp-{round}";
            session.SwitchWorkspace(path);
            session.SwitchWorkspace("/w/main");
            if (round % 2 == 0) session.DropWorkspace(path);
            // The odd rounds model removals this window never saw (another client, or while the app was closed).
        }
        session.PruneWorkspaces(live);
        Require(session.State.Workspaces.Count == live.Count && live.All(session.State.Workspaces.ContainsKey),
            $"{session.State.Workspaces.Count} workspace views remained for {live.Count} live workspaces.");
        session.PruneWorkspaces(new HashSet<string> { "/w/keep" });
        Require(session.State.Workspaces.ContainsKey("/w/main") && session.State.Workspaces.ContainsKey("/w/keep"), "Pruning dropped the active workspace or a live one.");
        session.PruneWorkspaces(new HashSet<string>());
        Require(session.State.Workspaces.Count == 2, "An empty registry (not yet synced) must not prune anything.");
        Console.WriteLine("PASS Workspace views are pruned to the live workspaces and never include a dropped one");
    }
}