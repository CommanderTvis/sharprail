namespace Tereditor.UI;

/// <summary>
/// Whether the window has no top bar: its left column is the fixed Projects rail with the project, branch and overflow
/// controls on top, and Settings and About sit in the overflow menu. On by default on macOS, on Android and in the browser (a phone-sized
/// Android workbench keeps its pages, which have no rail);
/// the other platforms keep the top bar and the dock's left column. Checks override it to run either layout on any OS.
/// </summary>
public static class UnifiedChrome
{
    public static bool? Override { get; set; }

    public static bool Enabled => Override ?? (OperatingSystem.IsMacOS() || OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid());
}