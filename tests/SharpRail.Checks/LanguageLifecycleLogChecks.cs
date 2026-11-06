using System.Reflection;

using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;

namespace SharpRail.Checks;

/// <summary>An <see cref="IProjectServices"/> whose language calls hang forever, or throw.</summary>
public class LanguageFakeHost : DispatchProxy
{
    public bool Throw;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "LanguageAsync")
        {
            if (Throw) throw new InvalidOperationException("fake host failure");
            return new ValueTask<LanguageResult>(new TaskCompletionSource<LanguageResult>().Task);
        }
        var type = method!.ReturnType;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>The Code Intelligence pane never waits forever on the host and never stays dead afterwards, and its lifecycle log is bounded and written off the calling thread.</summary>
internal static class LanguageLifecycleLogChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static void Run(string fixture)
    {
        var log = Path.Combine(fixture, "ci-log", "code-intelligence.log");
        var previousPath = CodeIntelligenceLog.Path; CodeIntelligenceLog.Path = log;
        var timeouts = (SharpRail.UI.Panels.LanguagePanel.ReadTimeout, SharpRail.UI.Panels.LanguagePanel.StartTimeout);
        try
        {
            Pane(log, throwing: false);
            Pane(log, throwing: true);
            StartIsNotCancelled(log);
            Watchdog(log);
            Bounded(fixture);
            Console.WriteLine("PASS The Code Intelligence pane times out with Retry, stays usable and logs its lifecycle to a bounded file");
        }
        finally
        {
            (SharpRail.UI.Panels.LanguagePanel.ReadTimeout, SharpRail.UI.Panels.LanguagePanel.StartTimeout) = timeouts;
            CodeIntelligenceLog.Path = previousPath;
        }
    }

    private static string Read(string log) { CodeIntelligenceLog.Flush(); return File.Exists(log) ? File.ReadAllText(log) : ""; }

    private static void Pane(string log, bool throwing)
    {
        SharpRail.UI.Panels.LanguagePanel.ReadTimeout = TimeSpan.FromSeconds(1);
        var host = DispatchProxy.Create<IProjectServices, LanguageFakeHost>();
        ((LanguageFakeHost)(object)host).Throw = throwing;
        var root = Path.GetDirectoryName(log)!;
        var pane = new SharpRail.UI.Panels.LanguagePanel(host, root, _ => Task.CompletedTask);
        var window = new Window { Width = 400, Height = 400, Content = pane };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        try
        {
            var retry = pane.FindControl<Button>("RetryRead")!;
            E2E.E2eWorkspace.Until(() => retry.IsVisible && pane.Idle, "The pane never showed Retry after a failing host call.");
            var status = pane.FindControl<TextBlock>("Status")!.Text ?? "";
            Require(throwing ? status.Contains("fake host failure") : status.Contains("did not answer"), "The pane's status does not say what went wrong: " + status);
            Require(pane.IsEnabled && pane.IsHitTestVisible && pane.FindControl<ScrollViewer>("PaneScroll")!.IsEnabled, "The pane is not usable after the failed call.");
            var text = Read(log);
            Require(text.Contains("evt=pane.rpc.begin") && text.Contains("evt=pane.rpc.end") && text.Contains("evt=pane.refresh.end"), "The log has no begin and end lines for the host call:\n" + text);
            Require(text.Contains(throwing ? "error=InvalidOperationException" : "timeout=true"), "The log does not name the failure:\n" + text);
            Require(text.Contains("durationMs=") && text.Contains("tid="), "The log lines carry no duration or thread id.");
            // Retry asks again instead of being absorbed.
            retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(!retry.IsVisible, "Retry stays visible after it was clicked.");
            E2E.E2eWorkspace.Until(() => retry.IsVisible && pane.Idle, "Retry did not run a second request that finished.");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    // A start that outlasts the pane's timeout must keep running in the host (cancelling it killed IntelliJ while it was still loading); a read is abandoned.
    private static void StartIsNotCancelled(string log)
    {
        var host = DispatchProxy.Create<IProjectServices, LanguageFakeHost>();
        var pane = new SharpRail.UI.Panels.LanguagePanel(host, Path.GetDirectoryName(log)!, _ => Task.CompletedTask);
        // Blocking on the call below would deadlock on the UI thread's context, so run without one like the pane checks do.
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            foreach (var cancelOnTimeout in new[] { false, true })
            {
                CancellationToken seen = default;
                var timedOut = false;
                try
                {
                    pane.HostCall("test", "", TimeSpan.FromMilliseconds(200), CancellationToken.None, async token => { seen = token; await Task.Delay(1500, CancellationToken.None); return 1; }, cancelOnTimeout).GetAwaiter().GetResult();
                }
                catch (TimeoutException) { timedOut = true; }
                Require(timedOut, "The slow host call did not time out.");
                Require(seen.IsCancellationRequested == cancelOnTimeout, cancelOnTimeout ? "A read that timed out was not cancelled." : "A start that outlasted the pane's timeout was cancelled in the host.");
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void Watchdog(string log)
    {
        using var watchdog = new SharpRail.UI.UiLagWatchdog(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));
        var until = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < until) { Thread.Sleep(700); Dispatcher.UIThread.RunJobs(); }
        var text = Read(log);
        Require(text.Contains("WARN") && text.Contains("evt=ui-lag lagMs="), "A blocked UI thread was not logged as a lag:\n" + text);
    }

    private static void Bounded(string fixture)
    {
        var bounded = Path.Combine(fixture, "ci-log-bounded", "code-intelligence.log");
        CodeIntelligenceLog.Path = bounded;
        var block = new string('x', 250);
        for (var index = 0; index < 20_000; index++) CodeIntelligenceLog.Info("fill", ("n", index), ("text", block));
        CodeIntelligenceLog.Flush();
        var size = new FileInfo(bounded).Length;
        Require(size <= 2 * 1024 * 1024 + 4096, $"The log grew to {size} bytes.");
        Require(File.Exists(bounded + ".1"), "The oversized log was not rotated.");
        Require(!File.Exists(bounded + ".2"), "More than one rotated copy exists.");
    }
}