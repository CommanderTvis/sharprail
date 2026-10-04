using System.Diagnostics;

using Avalonia.Headless;
using Avalonia.Threading;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Times a click on a workspace row until the target workspace is mounted, laid out and a frame
/// is rendered, between worktrees of one project that each have an open document.
/// </summary>
internal static class SwitchLatencyChecks
{
    private const int Warmup = 4, Samples = 20;

    internal static void Run(string root)
    {
        var budget = double.TryParse(Environment.GetEnvironmentVariable("SHARPRAIL_SWITCH_BUDGET_MS"), out var value) ? value : 100;
        using var app = OpenFixtureProject(Path.Combine(root, "switch-latency"));
        var workspaces = new[] { CreateWorkspaceViaDialog(app), CreateWorkspaceViaDialog(app) };
        foreach (var path in workspaces)
        {
            Switch(app, path, document: false);
            app.Click(app.Find<Avalonia.Controls.Button>("Tab_files"));
            app.Open("README.md", true);
        }
        var times = new List<double>();
        for (var i = 0; i < Warmup + Samples; i++)
        {
            var elapsed = Switch(app, workspaces[i % 2]);
            if (i >= Warmup) times.Add(elapsed);
            Settle(150);
        }
        times.Sort();
        double median = times[times.Count / 2], p90 = times[(int)(times.Count * 0.9) - 1], max = times[^1];
        Console.WriteLine($"SHARPRAIL_SWITCH_MS median={median:F1} p90={p90:F1} max={max:F1} budget={budget:F0} samples={times.Count}");
        Require(p90 < budget, $"Workspace switches must complete within {budget:F0} ms at p90 (median {median:F1}, p90 {p90:F1}, max {max:F1}).");
        Console.WriteLine($"PASS workspace switch latency: p90 {p90:F1} ms < {budget:F0} ms");
    }

    private static double Switch(E2eWorkspace app, string path, bool document = true)
    {
        var select = Select(app, path);
        app.Window.MouseMove(new Avalonia.Point(app.Window.Bounds.Width - 2, app.Window.Bounds.Height - 2));
        Settle(550);
        var clock = Stopwatch.StartNew();
        app.Click(select, freshGesture: false);
        while (!(Active(app, path) && (!document || app.Tabs.Any(tab => tab.Path == "README.md"))))
        {
            Require(clock.Elapsed < TimeSpan.FromSeconds(10), "Workspace switch timed out.");
            using var stop = new CancellationTokenSource(1);
            Dispatcher.UIThread.MainLoop(stop.Token);
        }
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        app.Window.UpdateLayout();
        app.Window.CaptureRenderedFrame();
        return clock.Elapsed.TotalMilliseconds;
    }
}