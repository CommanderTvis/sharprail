using Avalonia;
using Avalonia.Input;

namespace SharpRail.Scintilla;

/// <summary>The command modifier of the running platform, read from Avalonia's platform hotkey configuration.</summary>
public static class PlatformCommand
{
    /// <summary>Meta on macOS, Control elsewhere and the matching one in a browser; before an application exists, from the host OS.</summary>
    public static KeyModifiers Modifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers
        ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
}