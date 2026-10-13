using SharpRail.UI.Notifications;

namespace SharpRail.UI.Browser;

internal sealed class BrowserNotifier : IDesktopNotifier
{
    internal static BrowserNotifier Instance { get; } = new();
    public event Action<string>? Activated;
    internal void OnActivated(string id) => Activated?.Invoke(id);
    public void RequestPermission() => BrowserInterop.RequestNotificationPermission();
    public void Show(DesktopNotification notification) => BrowserInterop.Notify(notification.Id, notification.Title,
        notification.Subtitle is { } subtitle ? subtitle + "\n" + notification.Body : notification.Body);
}