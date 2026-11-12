using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Host.Abstractions;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks.E2E;

/// <summary>
/// The header's status area through every content variant and rapid refreshes: its bounds never change, the Branch and Remote
/// cells keep their control instances, and a refresh of an unchanged value writes nothing.
/// </summary>
internal static class StatusRefreshChecks
{
    private static readonly string[] Blocks = ["ProjectSegment", "BranchSegment", "ScopeBranch", "RemoteBlock", "RemoteRow", "RailStatus"];

    internal static void Run(string root)
    {
        using var app = OpenFixtureProject(Path.Combine(root, "status-refresh"));
        CreateWorkspaceViaDialog(app);
        Until(() => app.Window.BranchStatus is not null && !app.Window.StatusLookupPending);
        var real = app.Window.BranchStatus!;
        var variants = new (string Name, BranchStatus Status)[]
        {
            ("in sync", real with { HasOrigin = true, Published = true, Ahead = 0, Behind = 0 }),
            ("ahead", real with { HasOrigin = true, Published = true, Ahead = 12, Behind = 0 }),
            ("behind", real with { HasOrigin = true, Published = true, Ahead = 0, Behind = 3 }),
            ("diverged", real with { HasOrigin = true, Published = true, Ahead = 123, Behind = 456 }),
            ("not published", real with { HasOrigin = true, Published = false, Ahead = 0, Behind = 0 }),
            ("no remote", real with { HasOrigin = false, Published = false, Ahead = 0, Behind = 0 })
        };
        Require(!app.Window.GetVisualDescendants().OfType<Control>().Any(control => control.Name?.StartsWith("PullRequest", StringComparison.Ordinal) == true),
            "The status area carries no pull request segment or item.");
        var instances = Instances(app);
        var parts = app.Window.RemoteParts.ToArray();
        var baseline = Signature(app);

        // Every content variant, with a long and a detached branch name between: the area keeps its bounds, the controls their identity.
        // Only the rail (unified chrome) holds the width; the top bar sizes its segments to their content.
        var names = app.Window.HasUnifiedChrome ? new[] { null, new string('x', 160), "(HEAD detached at 0123abc)" } : [null];
        foreach (var variant in variants)
            foreach (var name in names)
            {
                app.Host.StatusMap = _ => variant.Status;
                app.Window.RefreshBranchStatus();
                Until(() => !app.Window.StatusLookupPending && app.Window.BranchStatus == variant.Status);
                if (name is not null) app.Window.SetBranch(name);
                app.Window.UpdateLayout();
                var now = Signature(app);
                Require(now == baseline, $"The status area changed with '{variant.Name}'/'{name}': [{baseline}] became [{now}].");
                Require(Instances(app).SequenceEqual(instances, ReferenceComparer.Instance), $"A control was replaced with '{variant.Name}'.");
            }
        app.Window.SetBranch(real.Branch);

        // A refresh of the value already shown writes nothing: no property change on any cell control, no tree change, no edit.
        var settled = variants[3];
        app.Host.StatusMap = _ => settled.Status;
        app.Window.RefreshBranchStatus();
        Until(() => !app.Window.StatusLookupPending && app.Window.BranchStatus == settled.Status);
        var changes = new List<string>();
        var watched = parts.Concat(instances.Where(c => !parts.Contains(c))).ToArray();
        foreach (var control in watched)
            control.PropertyChanged += (_, e) => { if (e.Property != Visual.BoundsProperty && e.Property.Name != "TemplatedParent") changes.Add($"{control.Name ?? control.GetType().Name}.{e.Property.Name}"); };
        var row = app.Find<DockPanel>("RemoteRow");
        row.Children.CollectionChanged += (_, _) => changes.Add("RemoteRow.Children");
        var applies = app.Window.ShipApplies;
        for (var round = 0; round < 25; round++)
        {
            app.Host.StatusMap = _ => settled.Status with { };
            app.Window.RefreshBranchStatus();
        }
        Until(() => !app.Window.StatusLookupPending);
        app.Window.SetBranch(app.Window.FindControl<TextBlock>("BranchLabel")!.Text ?? "");
        app.Window.UpdateLayout(); Settle(200);
        Require(changes.Count == 0, "A refresh of unchanged values wrote: " + string.Join(", ", changes.Distinct()));
        Require(app.Window.ShipApplies == applies, $"A refresh of an unchanged status edited the Remote block ({app.Window.ShipApplies - applies} times).");
        Require(Signature(app) == baseline && Instances(app).SequenceEqual(instances, ReferenceComparer.Instance), "Rapid refreshes moved the status area or replaced a control.");

        // A changed value edits in place, writing only to the cells that show it.
        changes.Clear();
        app.Host.StatusMap = _ => settled.Status with { Behind = 457 };
        app.Window.RefreshBranchStatus();
        Until(() => !app.Window.StatusLookupPending && app.Window.BranchStatus?.Behind == 457);
        Require(changes.All(change => change.StartsWith("RemoteBehind", StringComparison.Ordinal)) && changes.Count > 0, "A changed behind count wrote beyond its own cell: " + string.Join(", ", changes.Distinct()));
        Require(Signature(app) == baseline, "A changed value moved the status area.");
        app.Host.StatusMap = null;
        Console.WriteLine("PASS status area: fixed bounds across content variants, same controls, unchanged refreshes write nothing");
    }

    private sealed class ReferenceComparer : IEqualityComparer<Control>
    {
        internal static readonly ReferenceComparer Instance = new();
        public bool Equals(Control? left, Control? right) => ReferenceEquals(left, right);
        public int GetHashCode(Control control) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(control);
    }

    private static Control[] Instances(E2eWorkspace app) =>
    [
        app.Find<Button>("ScopeBranch"), app.Find<TextBlock>("BranchLabel"), app.Find<Grid>("RemoteBlock"), app.Find<DockPanel>("RemoteRow"),
        .. app.Window.RemoteParts
    ];

    private static string Signature(E2eWorkspace app) => string.Join(" ", Blocks.Select(name =>
    {
        var control = app.Window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        if (control is null) return name + "=missing";
        var origin = control.TranslatePoint(default, app.Window) ?? default;
        return $"{name}={origin.X:F1},{origin.Y:F1},{control.Bounds.Width:F1},{control.Bounds.Height:F1}";
    }));
}