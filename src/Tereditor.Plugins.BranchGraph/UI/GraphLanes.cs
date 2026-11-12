namespace SharpRail.Plugins.BranchGraph.UI;

/// <param name="Lane">The lane the line occupies in the gap above this row.</param>
/// <param name="Joins">Where it lands on this row: null for straight through, or the lane of a merge or branch joining this commit.</param>
internal sealed record GraphEdge(int Lane, int? Joins);

/// <param name="Lane">The lane the commit's own dot sits in.</param>
/// <param name="Edges">Lines crossing the gap above this row; never the commit's own lane, which <paramref name="FromAbove"/> covers.</param>
/// <param name="FromAbove">A child drawn above continues down into this dot; false on a branch tip.</param>
/// <param name="ToBelow">A first parent continues below it; false on a root commit.</param>
/// <param name="Branches">Lanes this commit opens below itself, one per parent after the first.</param>
/// <param name="Width">The widest lane index in play on this row, for sizing the gutter.</param>
internal sealed record GraphRow(GitGraphCommit Commit, int Lane, IReadOnlyList<GraphEdge> Edges, bool FromAbove, bool ToBelow,
    IReadOnlyList<int> Branches, int Width);

/// <summary>
/// Lanes for a commit list already in date order, the way <c>git log --graph</c> lays them out: a lane is claimed by
/// the sha it waits for, a commit takes the leftmost lane waiting for it, its first parent inherits that lane and the
/// rest open new ones. The layout itself is never capped, so no line loses track of the commit it leads to; only the
/// drawing is, because a rail cannot widen to fit: every lane past the eighth is drawn on the eighth, as one line.
/// </summary>
internal static class GraphLanes
{
    public const int MaxLanes = 8;

    private static int Drawn(int lane) => Math.Min(lane, MaxLanes - 1);

    /// <param name="carry">What the lanes were waiting for when the previous page ran out, so this page carries on.</param>
    public static (IReadOnlyList<GraphRow> Rows, IReadOnlyList<string> Carry) Layout(IReadOnlyList<GitGraphCommit> commits, IReadOnlyList<string>? carry = null)
    {
        // Lane i is waiting for lanes[i]; an empty string is a free lane.
        var lanes = new List<string>(carry ?? []);
        var rows = new List<GraphRow>();

        int Claim(string sha)
        {
            var existing = lanes.IndexOf(sha);
            if (existing >= 0) return existing;
            var free = lanes.IndexOf("");
            if (free >= 0) { lanes[free] = sha; return free; }
            lanes.Add(sha);
            return lanes.Count - 1;
        }

        foreach (var commit in commits)
        {
            // Asked before claiming: a lane the claim itself opened has nothing above it to draw.
            var fromAbove = lanes.Contains(commit.Sha);
            var lane = Claim(commit.Sha);
            var drawn = Drawn(lane);
            // Every other lane still waiting for something draws a line through the gap above this row. One that
            // shares this commit's drawn lane is the line above the dot instead.
            var edges = new List<GraphEdge>();
            for (var index = 0; index < lanes.Count; index++)
            {
                if (lanes[index].Length == 0 || index == lane) continue;
                var edge = new GraphEdge(Drawn(index), lanes[index] == commit.Sha ? drawn : null);
                if (edge.Lane == drawn) fromAbove = true;
                else if (!edges.Contains(edge)) edges.Add(edge);
            }

            // The lanes that were waiting for this commit are answered; the first parent inherits this lane.
            for (var index = 0; index < lanes.Count; index++)
                if (lanes[index] == commit.Sha) lanes[index] = "";
            if (commit.Parents.Count > 0) lanes[lane] = commit.Parents[0];
            // A merge's other parents each open a lane, and the drawing has to say where they came from.
            var branches = new List<int>();
            foreach (var parent in commit.Parents.Skip(1))
            {
                var opened = Drawn(Claim(parent));
                if (opened != drawn && !branches.Contains(opened)) branches.Add(opened);
            }
            while (lanes.Count > 0 && lanes[^1].Length == 0) lanes.RemoveAt(lanes.Count - 1);

            var width = drawn;
            var toBelow = commit.Parents.Count > 0;
            for (var index = 0; index < lanes.Count; index++)
            {
                if (lanes[index].Length == 0) continue;
                width = Math.Max(width, Drawn(index));
                // Another line sharing this drawn lane carries on below the dot even when this commit is a root.
                if (Drawn(index) == drawn) toBelow = true;
            }
            rows.Add(new(commit, drawn, edges, fromAbove, toBelow, branches, branches.Aggregate(width, Math.Max)));
        }
        return (rows, lanes);
    }
}