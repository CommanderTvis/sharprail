using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Notifications;

/// <summary>
/// Turns plugins' attention requests into desktop notifications. Requests collect for one window of time and flush
/// as a single notification, the latest per terminal; the decision to show is taken at the flush, so attention the
/// user gave meanwhile raises nothing. A focused app never notifies: the tab's status mark is already in view.
/// </summary>
internal sealed class AttentionNotifications
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);
    private const string BatchId = "attention";

    private readonly Workbench workbench;
    private readonly IDesktopNotifier notifier;
    private readonly DispatcherTimer timer;
    private readonly List<AttentionNotification> pending = [];
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

    private void Flush()
    {
        timer.Stop();
        var batch = pending.Where(Wanted).ToArray();
        pending.Clear();
        if (batch.Length == 0 || !workbench.State.Current.Settings.NotificationsEnabled || workbench.Focused) return;
        var latest = batch[^1];
        var id = batch.Length == 1 ? BatchId + ":" + latest.WorkspaceId + "\n" + latest.TabKey : BatchId;
        targets[id] = (latest.WorkspaceId, latest.TabKey);
        notifier.Show(batch.Length == 1
            ? new(id, latest.Title, workbench.State.Label(latest.WorkspaceId), latest.Body)
            : new(id, "SharpRail", null, $"{batch.Length} terminals need your attention"));
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
        pending.Clear();
        notifier.Activated -= Activated;
    }
}