using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Notifications;

/// <summary>
/// Turns plugins' attention requests into desktop notifications. Requests collect for one window of time and flush
/// as a single notification, the latest per terminal; the decision to show is taken at the flush, so attention the
/// user gave meanwhile raises nothing. A focused app never notifies: the tab's status mark is already in view.
/// A program's own notification (OSC 9, OSC 777) shares the window, so an agent's request for the same tab replaces
/// it rather than adding a second; it shows unless its tab is in view, like Ghostty, and an identical one within
/// five seconds of the last is dropped.
/// </summary>
internal sealed class AttentionNotifications
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);
    private const string BatchId = "attention";

    private readonly Workbench workbench;
    private readonly IDesktopNotifier notifier;
    private readonly DispatcherTimer timer;
    private static readonly TimeSpan IdenticalInterval = TimeSpan.FromSeconds(5);
    private readonly List<AttentionNotification> pending = [];
    // Program notifications pending, with the title of the tab they came from.
    private readonly Dictionary<AttentionNotification, string> programs = new(ReferenceEqualityComparer.Instance);
    private (string Text, DateTime At)? lastProgram;
    private readonly Dictionary<string, (string Workspace, string TabKey)> targets = [];

    public AttentionNotifications(Workbench workbench, IDesktopNotifier notifier, TimeSpan window)
    {
        this.workbench = workbench; this.notifier = notifier;
        timer = new DispatcherTimer { Interval = window };
        timer.Tick += (_, _) => Flush();
        notifier.Activated += Activated;
    }

    public void Request(AttentionNotification notification)
    {
        pending.RemoveAll(earlier => earlier.WorkspaceId == notification.WorkspaceId && earlier.TabKey == notification.TabKey);
        pending.Add(notification);
        if (!timer.IsEnabled) timer.Start();
    }

    /// <summary>Queues a program's notification; an agent's request already pending for its tab wins.</summary>
    public void RequestProgram(AttentionNotification notification, string tabTitle)
    {
        if (pending.Any(earlier => earlier.WorkspaceId == notification.WorkspaceId && earlier.TabKey == notification.TabKey && !programs.ContainsKey(earlier))) return;
        Request(notification);
        programs[notification] = tabTitle;
    }

    private void Flush()
    {
        timer.Stop();
        var settings = workbench.State.Current.Settings;
        var batch = pending.Where(Wanted).Where(notification => programs.ContainsKey(notification)
            ? settings.TerminalNotificationsEnabled && !workbench.ShowsTerminal(notification.WorkspaceId, notification.TabKey) && Fresh(notification)
            : settings.NotificationsEnabled && !workbench.Focused).ToArray();
        var tabs = new Dictionary<AttentionNotification, string>(programs, ReferenceEqualityComparer.Instance);
        pending.Clear(); programs.Clear();
        if (batch.Length == 0) return;
        var latest = batch[^1];
        var id = batch.Length == 1 ? BatchId + ":" + latest.WorkspaceId + "\n" + latest.TabKey : BatchId;
        targets[id] = (latest.WorkspaceId, latest.TabKey);
        notifier.Show(batch.Length == 1
            // A program's notification names its terminal: the tab's title and its workspace.
            ? new(id, latest.Title, tabs.TryGetValue(latest, out var tab) ? tab + " — " + workbench.State.Label(latest.WorkspaceId) : workbench.State.Label(latest.WorkspaceId), latest.Body)
            : new(id, "SharpRail", null, $"{batch.Length} terminals need your attention"));
    }

    private bool Fresh(AttentionNotification notification)
    {
        var text = notification.Title + "\n" + notification.Body;
        var now = DateTime.UtcNow;
        if (lastProgram is { } last && last.Text == text && now - last.At < IdenticalInterval) return false;
        lastProgram = (text, now);
        return true;
    }

    private bool Wanted(AttentionNotification notification)
    {
        if (workbench.WindowHolding(notification.WorkspaceId, notification.TabKey) is null) return false;
        try { return notification.StillNeeded?.Invoke() ?? true; }
        catch (Exception error) { Console.Error.WriteLine("Plugin predicate failed: " + error.Message); return false; }
    }

    private void Activated(string id) => Dispatcher.UIThread.Post(() =>
    {
        if (targets.Remove(id, out var target)) _ = workbench.RevealTerminalAsync(target.Workspace, target.TabKey);
    });

    public void Stop()
    {
        timer.Stop();
        pending.Clear(); programs.Clear();
        notifier.Activated -= Activated;
    }
}