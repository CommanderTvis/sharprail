using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.UI;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks;

/// <summary>
/// Re-pointing Changes from a target with no changes to one with thousands: a probe thread posts to the dispatcher
/// every few milliseconds and the longest wait must stay under the budget while the list fills; a second pick made
/// before the first lands wins.
/// </summary>
internal static class ChangesBranchSwitchChecks
{
    private const int Files = 4000;

    internal static void Run(string root)
    {
        var budget = double.TryParse(Environment.GetEnvironmentVariable("SHARPRAIL_BRANCH_SWITCH_STALL_BUDGET_MS"), out var value) ? value : 250;
        using var git = new IsolatedGit(Path.Combine(root, "changes-branch-switch-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "changes-branch-switch"));
        Git(app.Root, "checkout", "-b", "wide");
        var bulk = Directory.CreateDirectory(Path.Combine(app.Root, "bulk")).FullName;
        for (var folder = 0; folder < 40; folder++) Directory.CreateDirectory(Path.Combine(bulk, $"d{folder}"));
        for (var file = 0; file < Files; file++) File.WriteAllText(Path.Combine(bulk, $"d{file % 40}", $"f{file}.txt"), $"line {file}\n");
        Git(app.Root, "add", "-A");
        Git(app.Root, "commit", "-m", "wide");
        Git(app.Root, "branch", "near");
        LocationBarChecks.Switch(app, app.Root);
        app.ShowTool("changes");
        Until(() => Picker(app) is not null);
        Pick(app, "near");
        Until(() => Rows(app) == 0 && Label(app) == "vs near");
        Settle(1000);
        ToolbarWraps(app);

        var stalls = new List<double>();
        using var stop = new CancellationTokenSource();
        var probe = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var clock = Stopwatch.StartNew();
                var job = Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
                while (!job.GetTask().IsCompleted && !stop.IsCancellationRequested) Thread.Sleep(1);
                lock (stalls) stalls.Add(clock.Elapsed.TotalMilliseconds);
                Thread.Sleep(4);
            }
        })
        { IsBackground = true };
        probe.Start();
        var started = Stopwatch.StartNew();
        Pick(app, "main");
        var returned = started.Elapsed.TotalMilliseconds;
        Until(() => Rows(app) == Files);
        Settle(500);
        stop.Cancel();
        probe.Join();
        double[] samples;
        lock (stalls) samples = [.. stalls.Order()];
        var max = samples[^1];
        Console.WriteLine($"SHARPRAIL_BRANCH_SWITCH_STALL_MS max={max:F1} pick={returned:F1} budget={budget:F0} samples={samples.Length} total={started.Elapsed.TotalMilliseconds:F0}");
        Require(returned < budget, $"Picking a comparison target must return at once (took {returned:F1} ms).");
        Require(max < budget, $"Listing {Files} changes after a target switch must not stall the dispatcher beyond {budget:F0} ms (max {max:F1}).");

        // A second pick made before the first lands supersedes it: the list ends on the second target's changes.
        Pick(app, "near");
        Pick(app, "main");
        Pick(app, "near");
        Until(() => Rows(app) == 0 && Label(app) == "vs near");
        Settle(1500);
        Require(Rows(app) == 0 && Label(app) == "vs near", $"A superseded target's changes must not land: {Rows(app)} rows under {Label(app)}.");
        Console.WriteLine($"PASS a Changes target switch to {Files} files keeps the dispatcher under {budget:F0} ms (max {max:F1}) and the last pick wins");
    }

    // A narrow pane moves the comparison target to its own row instead of clipping it; a wide one keeps one row.
    private static void ToolbarWraps(E2eWorkspace app)
    {
        var toolbar = app.Window.GetLogicalDescendants().OfType<ChangesToolbarLayout>().Single();
        var compare = Picker(app)!;
        var scope = toolbar.Children[0];
        var natural = toolbar.Children.Sum(child => child.DesiredSize.Width) + 4 * (toolbar.Children.Count - 1);
        foreach (var width in new[] { Math.Min(300, natural - 1), 700 })
        {
            toolbar.Width = width;
            app.Window.UpdateLayout();
            var wrapped = width < natural;
            Require(toolbar.Wrapped == wrapped && toolbar.Bounds.Height == (wrapped ? 64 : 32) && (compare.Bounds.Top >= scope.Bounds.Bottom) == wrapped,
                $"The Changes toolbar at {width} px must use {(wrapped ? "two rows" : "one row")}: height {toolbar.Bounds.Height}, scope {scope.Bounds}, compare {compare.Bounds}.");
            Require(compare.Bounds.Width >= compare.DesiredSize.Width - 0.5,
                $"The comparison target must not be clipped at {width} px: {compare.Bounds.Width} of {compare.DesiredSize.Width}.");
        }
        toolbar.Width = double.NaN;
        app.Window.UpdateLayout();
        Console.WriteLine($"PASS the Changes toolbar wraps the comparison target to a second row below {natural:F0} px and keeps one row at 700 px");
    }

    private static Button? Picker(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "ChangesBranch");

    private static string Label(E2eWorkspace app) => string.Join(" ", Picker(app)!.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    // The list realizes only the rows near its viewport; its items are every listed change.
    private static int Rows(E2eWorkspace app) => app.Window.GetLogicalDescendants().OfType<ItemsControl>().SingleOrDefault(list => list.Name == "ChangesRows")?
        .Items.Count(item => item is not string) ?? -1;

    private static void Pick(E2eWorkspace app, string branch)
    {
        Until(() => Items(Picker(app)!.ContextMenu!.Items).Any(item => Equals(item.Tag, branch)));
        Items(Picker(app)!.ContextMenu!.Items).First(item => Equals(item.Tag, branch)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static IEnumerable<MenuItem> Items(IEnumerable<object?> items) => items.OfType<MenuItem>().SelectMany(item => new[] { item }.Concat(Items(item.Items)));
}