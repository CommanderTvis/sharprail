namespace SharpRail.UI.Notifications;

/// <summary>One notification outside the app's windows. A later one with the same id replaces it.</summary>
public sealed record DesktopNotification(string Id, string Title, string? Subtitle, string Body);

/// <summary>
/// The operating system's notification channel. The composition root supplies it; a workbench composed without one
/// raises nothing. Calls return immediately and never fail the caller: delivery is the system's concern.
/// </summary>
public interface IDesktopNotifier
{
    /// <summary>Asks the system for permission while the user is present, ahead of the first notification.</summary>
    void RequestPermission();

    void Show(DesktopNotification notification);

    /// <summary>Raised, on any thread, with the id of a notification the user activated.</summary>
    event Action<string>? Activated;
}