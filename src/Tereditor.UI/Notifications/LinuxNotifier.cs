using System.Diagnostics;
using System.Runtime.Versioning;

namespace SharpRail.UI.Notifications;

/// <summary>
/// Linux notifications through <c>notify-send</c> (libnotify's command line tool, speaking the freedesktop notification
/// service), with no library linked. Without the tool or a notification service nothing is shown; a click cannot return
/// to the app, so <see cref="Activated"/> never fires.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxNotifier : IDesktopNotifier
{
    public event Action<string>? Activated { add { } remove { } }

    public void RequestPermission() { }

    public void Show(DesktopNotification notification) => _ = Task.Run(() =>
    {
        try
        {
            var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "--app-name=SharpRail", "--", notification.Subtitle is { Length: > 0 } subtitle ? $"{notification.Title}: {subtitle}" : notification.Title, notification.Body })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            process?.WaitForExit(5000);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { }
    });
}