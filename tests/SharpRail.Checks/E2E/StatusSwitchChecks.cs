using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>
/// The header's status area through a workspace switch with a slow branch status read: every block keeps its bounds after every
/// dispatcher turn, the switch does not wait for the status, and the busy line shows only for a load past its delay.
/// </summary>
internal static class StatusSwitchChecks
{
    private static readonly string[] Blocks = ["ProjectSegment", "BranchSegment", "ScopeBranch", "RemoteBlock", "RailStatus"];

    internal static void Run(string root)
    {
        using var app = OpenFixtureProject(Path.Combine(root, "status-switch"));
        var workspaces = new[] { CreateWorkspaceViaDialog(app), CreateWorkspaceViaDialog(app) };
        Until(() => app.Window.BranchStatus is not null);
        var delay = TimeSpan.FromMilliseconds(1400);

        // A fast read: neither a first visit nor a repeat shows the busy line, and the area never moves.
        foreach (var uncached in new[] { true, false })
        {
            if (uncached) app.Window.ForgetBranchFacts();
            var fast = Switch(app, workspaces[0], workspaces[1]);
            Require(!fast.BusySeen && fast.Moves == 0 && fast.Changes <= (uncached ? 2 : 1), $"A fast status read must not show the busy line or move the status area ({Describe(fast)}).");
            Console.WriteLine($"SHARPRAIL_STATUS_SWITCH fast uncached={uncached} content={fast.ContentMs:F0}ms final={fast.FinalMs:F0}ms samples={fast.Samples}");
            Switch(app, workspaces[1], workspaces[0]);
        }

        // A slow read: the content shows before the status, the layout holds still, and the busy line comes after its delay.
        foreach (var uncached in new[] { true, false })
        {
            if (uncached) app.Window.ForgetBranchFacts();
            app.Host.StatusDelay = delay;
            var slow = Switch(app, workspaces[0], workspaces[1]);
            app.Host.StatusDelay = default;
            Require(slow.Moves == 0 && slow.Changes <= (uncached ? 2 : 1), $"The status area must keep its bounds through a slow status read ({Describe(slow)}).");
            Require(slow.ContentMs < delay.TotalMilliseconds * 0.6, $"The switch must not wait for the status ({Describe(slow)}).");
            Require(slow.FinalMs >= delay.TotalMilliseconds * 0.9, $"The status arrives only after its delay ({Describe(slow)}).");
            if (!uncached) Require(!slow.BusySeen, $"A cached status is never loading, so the busy line stays off ({Describe(slow)}).");
            else Require(slow.BusyAtMs is { } busy && busy >= SharpRail.UI.WorkbenchWindow.StatusBusyDelay.TotalMilliseconds * 0.8 && busy < delay.TotalMilliseconds, $"The busy line must show after the threshold and before the status arrives ({Describe(slow)}).");
            Require(slow.BusyEnded, "The busy line must be gone once the status arrived.");
            Console.WriteLine($"SHARPRAIL_STATUS_SWITCH slow uncached={uncached} content={slow.ContentMs:F0}ms busy={slow.BusyAtMs?.ToString("F0") ?? "none"}ms final={slow.FinalMs:F0}ms samples={slow.Samples}");
            Switch(app, workspaces[1], workspaces[0]);
        }
        Console.WriteLine("PASS status area: stable bounds through a slow status read, busy line only past its delay");
    }

    private static string Describe(Result result) =>
        $"moves={result.Moves} changes={result.Changes} {result.First}; busy={result.BusySeen}; content={result.ContentMs:F0}ms final={result.FinalMs:F0}ms";

    private sealed record Result(int Samples, int Moves, int Changes, string First, bool BusySeen, double? BusyAtMs, bool BusyEnded, double ContentMs, double FinalMs);

    private static string Signature(E2eWorkspace app)
    {
        var parts = new List<string>();
        foreach (var name in Blocks)
        {
            var control = app.Window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
            if (control is null || !control.IsEffectivelyVisible) { parts.Add(name + "=hidden"); continue; }
            var origin = control.TranslatePoint(default, app.Window) ?? default;
            parts.Add($"{name}={origin.X:F0},{origin.Y:F0},{control.Bounds.Width:F0},{control.Bounds.Height:F0}");
        }
        return string.Join(" ", parts);
    }

    private static string Hidden(string signature) => string.Join(" ", signature.Split(' ').Where(part => part.EndsWith("=hidden", StringComparison.Ordinal)));

    private static bool Same(string left, string right)
    {
        static double[] Numbers(string text) => [.. text.Split([' ', ',', '='], StringSplitOptions.RemoveEmptyEntries).Select(part => double.TryParse(part, out var value) ? value : double.NaN)];
        var a = Numbers(left); var b = Numbers(right);
        return left.Split(' ').Select(part => part.Split('=')[0]).SequenceEqual(right.Split(' ').Select(part => part.Split('=')[0])) &&
            a.Length == b.Length && a.Zip(b).All(pair => double.IsNaN(pair.First) == double.IsNaN(pair.Second) && (double.IsNaN(pair.First) || Math.Abs(pair.First - pair.Second) <= 1));
    }

    /// <summary>Clicks the target row and samples the status area after every dispatcher turn until the status arrived.</summary>
    private static Result Switch(E2eWorkspace app, string from, string to)
    {
        if (!Active(app, from)) app.Click(Select(app, from));
        Until(() => Active(app, from) && app.Window.BranchStatus is not null && !app.Window.StatusLookupPending);
        Settle(300);
        var baseline = Signature(app);
        var clock = Stopwatch.StartNew();
        app.Click(Select(app, to), freshGesture: false);
        // A block may change its width once when the target's own branch name replaces the previous one; it never hides, and nothing
        // moves from the moment that name shows through to the status arriving (moves counts those samples).
        int samples = 0, moves = 0, changes = 0;
        var previous = baseline; var named = false;
        string first = "";
        double content = -1, final = -1;
        double? busyAt = null;
        bool busySeen = false, busyEnded = false;
        while (final < 0 || (busySeen && !busyEnded))
        {
            Require(clock.Elapsed < TimeSpan.FromSeconds(20), $"The status never settled after the switch: content={content} status={app.Window.BranchStatus is not null} label={app.Window.FindControl<TextBlock>("BranchLabel")!.Text} pending={app.Window.StatusBusyPending}/{app.Window.StatusLookupPending}.");
            using var stop = new CancellationTokenSource(1);
            Dispatcher.UIThread.MainLoop(stop.Token);
            samples++;
            app.Window.UpdateLayout();
            var now = Signature(app);
            if (Hidden(now) != Hidden(baseline)) { moves++; first = $"hidden set changed: baseline [{baseline}] now [{now}] at {clock.Elapsed.TotalMilliseconds:F0}ms"; }
            if (!Same(now, previous))
            {
                changes++;
                if (named) { moves++; if (first.Length == 0) first = $"moved after the name: [{previous}] to [{now}] at {clock.Elapsed.TotalMilliseconds:F0}ms"; }
            }
            previous = now;
            named |= app.Window.WorkspaceRoot == to && app.Window.FindControl<TextBlock>("BranchLabel")!.Text == Path.GetFileName(to);
            if (content < 0 && Active(app, to) && app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == to) content = clock.Elapsed.TotalMilliseconds;
            if (app.Window.StatusBusyShown) { busySeen = true; busyAt ??= clock.Elapsed.TotalMilliseconds; busyEnded = false; }
            else if (busySeen) busyEnded = true;
            if (final < 0 && content >= 0 && app.Window.BranchStatus is not null && app.Window.WorkspaceRoot == to &&
                app.Window.FindControl<TextBlock>("BranchLabel")!.Text == Path.GetFileName(to) && !app.Window.StatusBusyPending && !app.Window.StatusLookupPending)
                final = clock.Elapsed.TotalMilliseconds;
        }
        if (!busySeen) busyEnded = true;
        return new(samples, moves, changes, first, busySeen, busyAt, busyEnded, content, final);
    }
}