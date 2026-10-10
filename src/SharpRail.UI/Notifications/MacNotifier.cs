using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpRail.UI.Notifications;

/// <summary>
/// macOS notifications. Inside the app bundle they go through User Notifications, attributed to SharpRail, and
/// activating one comes back through <see cref="Activated"/>. The system refuses that API to a process without a
/// bundle identifier, so a run from source falls back to AppleScript's <c>display notification</c>: the banner
/// appears under Script Editor's name and its click cannot return to the app.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacNotifier : IDesktopNotifier
{
    private const string Library = "SharpRailNotifications";
    private static MacNotifier? current;
    private readonly bool bundled;

    private MacNotifier(bool bundled) => this.bundled = bundled;

    public event Action<string>? Activated;

    /// <summary>The process's notifier; one per process, as the system has one delegate.</summary>
    public static MacNotifier Create()
    {
        if (current is not null) return current;
        bool bundled;
        try { bundled = Start(&OnActivated) != 0; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { bundled = false; }
        return current = new(bundled);
    }

    public bool Bundled => bundled;

    public void RequestPermission()
    {
        if (bundled) _ = Task.Run(Authorize);
    }

    public void Show(DesktopNotification notification) => _ = Task.Run(() =>
    {
        try
        {
            if (bundled) Show(notification.Id, notification.Title, notification.Subtitle ?? "", notification.Body);
            else Script(notification);
        }
        catch (Exception error) { Console.Error.WriteLine("Desktop notification failed: " + error.Message); }
    });

    // The texts travel as arguments, never inside the script, so nothing in them is read as AppleScript.
    private static void Script(DesktopNotification notification)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[]
        {
            "-e", "on run argv", "-e", "display notification (item 3 of argv) with title (item 1 of argv) subtitle (item 2 of argv)", "-e", "end run",
            "--", notification.Title, notification.Subtitle ?? "", notification.Body
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        process?.WaitForExit(5000);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnActivated(byte* identifier)
    {
        try
        {
            if (Marshal.PtrToStringUTF8((nint)identifier) is { } id) current?.Activated?.Invoke(id);
        }
        catch (Exception error) { Console.Error.WriteLine("Desktop notification activation failed: " + error.Message); }
    }

    [DllImport(Library, EntryPoint = "sharprail_notifications_start")]
    private static extern int Start(delegate* unmanaged[Cdecl]<byte*, void> activated);

    [DllImport(Library, EntryPoint = "sharprail_notifications_authorize")]
    private static extern void Authorize();

    [DllImport(Library, EntryPoint = "sharprail_notifications_show")]
    private static extern void Show([MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string subtitle, [MarshalAs(UnmanagedType.LPUTF8Str)] string body);
}