using System.Diagnostics;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;


using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Profiles switching between workspaces of one realistic project (a few thousand tracked files over several commits, specs,
/// open documents and terminal tabs in every workspace): for each switch, the time from the rail click to the loading
/// placeholder, the workspace mounted, the first rendered frame and the visible content usable, and every host call issued
/// before the mount. The budget is the number of host round trips on the critical path, not wall time, which CI stretches.
/// </summary>
internal static class WorkspaceSwitchProfile
{
    // Opening the target is the one host call a switch needs before it mounts; everything else loads after.
    private const int CriticalHostCalls = 1;
    private const int Warmup = 2, Samples = 12;

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "switch-profile-git"));
        var project = Path.Combine(root, "switch-profile", "sample-project");
        IsolatedGit.Repository(project);
        for (var module = 0; module < 60; module++)
        {
            var directory = Path.Combine(project, "src", "m" + module);
            Directory.CreateDirectory(directory);
            for (var file = 0; file < 50; file++)
                File.WriteAllText(Path.Combine(directory, $"F{file}.cs"), $"namespace M{module};\n\ninternal static class F{file} {{ }}\n");
            if (module % 6 == 0)
                File.WriteAllText(Path.Combine(directory, "SPEC.md"), $"---\nid: m{module}\ntype: module\ntitle: Module {module}\n---\n\n## Purpose\n\nFixture module.\n");
            if (module % 15 == 14)
                Commit(project, "modules to " + module);
        }
        var log = new HostCallLog();
        using var app = new E2eWorkspace(project, embedded: true, wrap: inner => HostCallLog.Wrap(inner, log));
        Commit(project, "fixture");
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        GoProjectHome(app);
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        var workspaces = new List<string> { CreateWorkspaceViaDialog(app), CreateWorkspaceViaDialog(app), CreateWorkspaceViaDialog(app) };
        app.Click(Select(app, app.Window.ProjectRoot));
        Until(() => Active(app, app.Window.ProjectRoot));
        workspaces.Insert(0, app.Window.ProjectRoot);
        foreach (var path in workspaces)
        {
            if (!Active(app, path)) { app.Click(Select(app, path)); Until(() => Active(app, path)); }
            app.Open("README.md", true);
            app.Open("notes.txt", true);
            if (OperatingSystem.IsMacOS()) app.Window.Layout.Open(new DockTab("terminal:profile", "Terminal", "terminal"), keep: true);
            app.Open("README.md", true);
            Settle(300);
        }

        // Cold: each target was last shown three switches ago. Warm: switching back to the workspace just left.
        var cold = Measure(app, log, index => workspaces[index % workspaces.Count]);
        var warm = Measure(app, log, index => workspaces[index % 2]);
        Report("cold", cold);
        Report("warm", warm);
        var worst = cold.Concat(warm).MaxBy(sample => sample.Critical.Count)!;
        Require(worst.Critical.Count <= CriticalHostCalls,
            $"A workspace switch awaited {worst.Critical.Count} host calls before mounting (budget {CriticalHostCalls}): {string.Join(", ", worst.Critical)}.");
        Console.WriteLine($"PASS workspace switch critical path: at most {CriticalHostCalls} host call before the workspace mounts");
    }

    // The window's Git status, or Git's own background work, can hold the index lock for a moment.
    private static void Commit(string project, string message)
    {
        for (var attempt = 0; ; attempt++)
            try { IsolatedGit.Run(project, "add", "-A"); IsolatedGit.Run(project, "commit", "-q", "-m", message); return; }
            catch (InvalidOperationException error) when (attempt < 50 && error.Message.Contains("index.lock", StringComparison.Ordinal)) { Settle(100); }
    }

    private sealed record Sample(double Loading, double Mounted, double Frame, double Usable, IReadOnlyList<string> Critical, IReadOnlyList<string> After);

    private static List<Sample> Measure(E2eWorkspace app, HostCallLog log, Func<int, string> target)
    {
        var samples = new List<Sample>();
        for (var index = 0; index < Warmup + Samples; index++)
        {
            var path = target(index);
            if (Active(app, path)) continue;
            var sample = Switch(app, log, path);
            if (index >= Warmup) samples.Add(sample);
        }
        return samples;
    }

    private static Sample Switch(E2eWorkspace app, HostCallLog log, string path)
    {
        var select = Select(app, path);
        app.Window.UpdateLayout();
        var point = select.TranslatePoint(new Point(24, select.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point);
        Settle(600);
        // The press and release go in directly: the shared click helper drains the dispatcher afterwards, which would run the
        // whole switch, and the loads after it, before the first sample.
        log.Start();
        var clock = Stopwatch.StartNew();
        double loading = -1, mounted = -1, frame = -1, usable = -1;
        var critical = new List<string>();
        SharpRail.UI.SwitchTimeline.Observer = step =>
        {
            if (step == "placeholder" && loading < 0) loading = clock.Elapsed.TotalMilliseconds;
            if (step == "mounted") { mounted = clock.Elapsed.TotalMilliseconds; critical.AddRange(log.SwitchCalls()); }
            // The mount's own layout pass has run: render the frame it presents.
            if (step == "layout" && mounted >= 0 && frame < 0)
            {
                app.Window.CaptureRenderedFrame(); frame = clock.Elapsed.TotalMilliseconds;
                if (Usable(app)) usable = frame;
            }
        };
        try
        {
            app.Window.MouseDown(point, MouseButton.Left);
            app.Window.MouseUp(point, MouseButton.Left);
            while (frame < 0)
            {
                Require(clock.Elapsed < TimeSpan.FromSeconds(20), "Workspace switch timed out.");
                using var stop = new CancellationTokenSource(1);
                Dispatcher.UIThread.MainLoop(stop.Token);
            }
        }
        finally { SharpRail.UI.SwitchTimeline.Observer = null; }
        Require(Active(app, path), "The switch must land on the requested workspace.");
        while (usable < 0 && !Usable(app))
        {
            Require(clock.Elapsed < TimeSpan.FromSeconds(20), "Workspace content never became usable.");
            using var stop = new CancellationTokenSource(1);
            Dispatcher.UIThread.MainLoop(stop.Token);
            app.Window.UpdateLayout();
        }
        if (usable < 0) usable = clock.Elapsed.TotalMilliseconds;
        Settle(400);
        var after = log.Calls().ToList();
        foreach (var call in critical) after.Remove(call);
        return new(loading, mounted, frame, usable, critical, after);
    }

    private static bool LoadingVisible(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "WorkspaceLoading" && control.IsEffectivelyVisible);

    // The selected document shows its content: no workspace, document or terminal loading body is visible.
    private static bool Usable(E2eWorkspace app) =>
        app.Window.Layout.Selected(app.Window.Layout.View.FocusedCenter)?.Path == "README.md" &&
        !app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible &&
            (text.Name == "WorkspaceLoading" || text.Text is "Loading document…" or "Loading terminal…"));

    private static void Report(string kind, List<Sample> samples)
    {
        string Stat(Func<Sample, double> pick)
        {
            var values = samples.Select(pick).Where(value => value >= 0).Order().ToArray();
            if (values.Length == 0) return "never";
            return $"median {values[values.Length / 2]:F1} p95 {values[Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1)]:F1}";
        }
        var shown = samples.Count(sample => sample.Loading >= 0);
        Console.WriteLine($"SHARPRAIL_SWITCH_PROFILE {kind} samples={samples.Count} loading-shown={shown} loading=({Stat(s => s.Loading)}) " +
            $"mounted=({Stat(s => s.Mounted)}) frame=({Stat(s => s.Frame)}) usable=({Stat(s => s.Usable)})");
        var before = samples.SelectMany(sample => sample.Critical).GroupBy(name => name).Select(group => $"{group.Key}x{group.Count()}");
        var after = samples.SelectMany(sample => sample.After).GroupBy(name => name).Select(group => $"{group.Key}x{group.Count()}");
        Console.WriteLine($"SHARPRAIL_SWITCH_PROFILE {kind} before-mount: {string.Join(" ", before)}");
        Console.WriteLine($"SHARPRAIL_SWITCH_PROFILE {kind} after-mount: {string.Join(" ", after)}");
    }

    /// <summary>
    /// The project host calls a window issues since the last <see cref="Start"/>, each marked when the switch itself issued it,
    /// so that an unrelated refresh still running from before the click is not charged to the switch.
    /// </summary>
    internal sealed class HostCallLog
    {
        private readonly List<(string Name, bool Switch)> calls = [];
        internal void Start() { lock (calls) calls.Clear(); }
        internal void Add(string name, bool fromSwitch) { lock (calls) calls.Add((name, fromSwitch)); }
        internal string[] Calls() { lock (calls) return [.. calls.Select(call => call.Name)]; }
        internal string[] SwitchCalls() { lock (calls) return [.. calls.Where(call => call.Switch).Select(call => call.Name)]; }

        internal static IProjectServices Wrap(IProjectServices inner, HostCallLog log)
        {
            var proxy = DispatchProxy.Create<IProjectServices, Recorder>();
            ((Recorder)(object)proxy).Bind(inner, log);
            return proxy;
        }
    }

    public class Recorder : DispatchProxy
    {
        private IProjectServices inner = null!;
        private HostCallLog log = null!;
        internal void Bind(IProjectServices services, HostCallLog calls) { inner = services; log = calls; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            // The synchronous part of an awaited call runs inside the switch's own state machine.
            var fromSwitch = new StackTrace().GetFrames().Any(frame => frame.GetMethod()?.DeclaringType?.Name.Contains("OpenWorkspaceAsync", StringComparison.Ordinal) == true);
            log.Add(method!.Name.Replace("Async", ""), fromSwitch);
            try { return method.Invoke(inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerException);
                throw;
            }
        }
    }
}